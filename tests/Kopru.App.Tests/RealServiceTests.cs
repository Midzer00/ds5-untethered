// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Hid;
using Kopru.Core.Localization;
using Kopru.Core.Protocol;
using Kopru.Core.Setup;
using Kopru.Core.Virtual;

namespace Kopru.App.Tests;

internal sealed class TestLink : IHidLink
{
	public const string BtPath = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_test";

	private readonly BlockingCollection<byte[]> inputs = [];
	private readonly CancellationTokenSource closed = new();

	public HidDevice Device { get; } = new(BtPath, 0x054C, 0x0CE6, 78, 547, 64);
	public ConcurrentQueue<byte[]> Writes { get; } = new();

	public void Feed(byte status)
	{
		byte[] report = new byte[78];
		report[0] = 0x31;
		report[2 + BatteryStatus.StatusOffset] = status;
		BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), Crc.Compute(Crc.InputSeed, report.AsSpan(0, 74)));
		inputs.Add(report);
	}

	public int Read(Span<byte> buffer)
	{
		try
		{
			byte[] next = inputs.Take(closed.Token);
			next.CopyTo(buffer);
			return next.Length;
		}
		catch (OperationCanceledException)
		{
			throw new IOException("closed");
		}
	}

	public void Write(ReadOnlySpan<byte> report) => Writes.Enqueue(report.ToArray());
	public byte[] GetFeature(byte reportId) => new byte[64];
	public void CancelRead() => closed.Cancel();
	public void Dispose() => closed.Cancel();
}

internal sealed class TestControllers : IControllerSource, IControllerFinder
{
	public ConcurrentQueue<IHidLink> Links { get; } = new();
	public List<HidDevice> Paired { get; } = [];
	public IHidLink? TryOpen() => Links.TryDequeue(out IHidLink? l) ? l : null;
	public IReadOnlyList<HidDevice> Find() => Paired;
}

internal sealed class TestIdentity(bool present) : IIdentitySource
{
	private static readonly byte[] DeviceDescriptor = [18, 1, 0x00, 0x02, 0, 0, 0, 64, 0x4C, 0x05, 0xE6, 0x0C, 0x00, 0x01, 1, 2, 0, 1];
	private static readonly byte[] Configuration =
	[
		9, 2, 59, 0, 3, 1, 0, 0xC0, 0xFA,
		9, 4, 0, 0, 0, 1, 1, 0, 0,
		9, 4, 1, 1, 1, 1, 2, 0, 0,
		7, 5, 0x01, 9, 0x88, 0x01, 4,
		9, 4, 3, 0, 2, 3, 0, 0, 0,
		9, 0x21, 0x11, 0x01, 0, 1, 0x22, 0x08, 0x00,
		7, 5, 0x84, 3, 64, 0, 6,
	];

	public DeviceIdentity? Load() => present
		? DeviceIdentity.Create(DeviceDescriptor, Configuration, [0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
			new Dictionary<int, string> { [1] = "S", [2] = "D" }, new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] })
		: null;
}

internal sealed class TestAttacher : IUsbIpAttacher
{
	public Task<int> AttachAsync(int tcpPort, string busId, CancellationToken cancel) => Task.FromResult(1);
	public Task DetachAsync(int port) => Task.CompletedTask;
}

internal sealed class TestHiding(bool installed = true) : IHidingService
{
	public List<string> Calls { get; } = [];
	public bool IsInstalled => installed;
	public Task RecoverAsync() => Log("recover");
	public Task BeginAsync() => Log("begin");
	public Task HideAsync(string instanceId) => Log("hide " + instanceId);
	public Task RestoreAsync() => Log("restore");
	public Task RegisterAppEarlyAsync() => Log("register early");
	public Task ReleaseAppEarlyAsync() => Log("release early");

	private Task Log(string call)
	{
		lock (Calls)
		{
			Calls.Add(call);
		}
		return Task.CompletedTask;
	}
}

internal sealed class TestStartup : IStartupRegistration
{
	public List<bool> Calls { get; } = [];
	public void Set(bool enabled) => Calls.Add(enabled);
}

public class RealServiceTests
{
	private const string BtId = @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\test";

	private readonly TestControllers controllers = new();

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private RealControllerService Service(TestHiding hiding, bool identity = true) =>
		new(new BridgeHost(controllers, new TestIdentity(identity), new TestAttacher())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan
		}, hiding, controllers, a => a(), _ => BtId, timers: false);

	[Fact]
	public async Task Start_HidesTheOpenedController_RunsAndReportsBattery_StopRestores()
	{
		TestHiding hiding = new();
		using RealControllerService service = Service(hiding);
		TestLink link = new();
		controllers.Links.Enqueue(link);
		Assert.False(service.IsConnected);

		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected);
		link.Feed(0x17); // charging, 70 %
		WaitUntil(() => service.HasBattery);

		Assert.True(service.IsBridgeRunning);
		Assert.True(service.IsConnected);
		Assert.Equal(ControllerKind.DualSense, service.Kind);
		Assert.Equal(70, service.BatteryPercent);
		Assert.True(service.IsCharging);
		Assert.Equal(["begin", "hide " + BtId], hiding.Calls);
		Assert.Equal(string.Empty, service.Warning);

		await service.StopBridgeAsync();
		Assert.False(service.IsBridgeRunning);
		Assert.False(service.IsGameConnected);
		Assert.Equal("restore", hiding.Calls[^1]);
	}

	[Fact]
	public async Task WithoutHidHide_TheBridgeStillRuns_AndWarns()
	{
		TestHiding hiding = new(installed: false);
		using RealControllerService service = Service(hiding);
		controllers.Links.Enqueue(new TestLink());

		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected);

		Assert.Empty(hiding.Calls);
		Assert.Equal(Text.HidingUnavailable(), service.Warning);
		await service.ShutdownAsync();
		Assert.False(service.IsBridgeRunning);
	}

	[Fact]
	public async Task MissingIdentity_ShowsTheBridgeError()
	{
		using RealControllerService service = Service(new TestHiding(), identity: false);
		await service.StartBridgeAsync();
		Assert.Equal(Text.BridgeNoCapture(), service.Warning);
		await service.StopBridgeAsync();
	}

	[Fact]
	public void Presence_ComesFromTheListing_WhileStopped()
	{
		using RealControllerService service = Service(new TestHiding());
		int raised = 0;
		service.StateChanged += (_, _) => raised++;
		controllers.Paired.Add(new HidDevice(TestLink.BtPath, 0x054C, HidDevice.DualSenseEdgeProductId, 78, 547, 64));
		service.RefreshPresence();
		Assert.True(service.IsConnected);
		Assert.Equal(ControllerKind.DualSenseEdge, service.Kind);
		Assert.Equal("DualSense Edge", service.DeviceName);
		Assert.False(service.HasBattery);
		Assert.Equal(1, raised);
	}

	[Fact]
	public void HapticsTest_WhileStopped_StreamsHaptics_AndNoControllerWarns()
	{
		using RealControllerService service = Service(new TestHiding());
		service.TestHaptics(HapticSide.Left);
		WaitUntil(() => service.Warning == Text.TestNoController());

		TestLink link = new();
		controllers.Links.Enqueue(link);
		service.TestHaptics(HapticSide.Both);
		WaitUntil(() => link.Writes.Any(w => w[0] == BluetoothReports.HapticsId));
		WaitUntil(() => service.Warning.Length == 0);
	}

	[Fact]
	public void TriggerEffects_FollowTheStrengthSetting()
	{
		Assert.Equal(TriggerEffects.Feedback(2, 8), RealControllerService.TriggerEffect(TriggerTestEffect.Feedback, 100));
		Assert.Equal(TriggerEffects.Feedback(2, 4), RealControllerService.TriggerEffect(TriggerTestEffect.Feedback, 50));
		Assert.Equal(TriggerEffects.Weapon(2, 6, 1), RealControllerService.TriggerEffect(TriggerTestEffect.Weapon, 0));
		Assert.Equal(TriggerEffects.Off(), RealControllerService.TriggerEffect(TriggerTestEffect.Off, 100));
	}

	[Fact]
	public void AdapterResult_WithoutData_IsLocalized()
	{
		AdapterTestResult r = RealControllerService.Describe(null);
		Assert.Null(r.Verdict);
		Assert.Equal(Text.AdapterTestNoData(), r.Text);
	}

	[AvaloniaFact]
	public void Settings_LoadFromStore_SaveDebounced_AndStartupOnlyOnToggle()
	{
		Language? before = Localizer.Chosen;
		try
		{
			var store = new MemorySettingsStore(new AppSettings { GameHapticsLevel = 120, LightbarColour = 0xFF3B3B, StartWithWindows = true, Language = "en" });
			var startup = new TestStartup();
			var fake = new FakeControllerService(animate: false);
			var vm = new MainWindowViewModel(fake, new AppEnvironment(store, startup));

			Assert.Equal(120, vm.Settings.GameHapticsLevel);
			Assert.Equal(120, fake.Settings.GameHapticsLevel);
			Assert.Equal(0xFF3B3Bu, vm.Light.SelectedPreset.Rgb);
			Assert.True(vm.Settings.StartWithWindows);
			Assert.Equal(Language.English, Localizer.Chosen);
			Assert.False(vm.Settings.IsFirstRun);
			Assert.Empty(startup.Calls);
			Assert.Equal(0, store.Saves);

			vm.Settings.RumbleLevel = 50;
			vm.Settings.RumbleLevel = 60;
			Assert.Equal(0, store.Saves);
			Assert.True(SpinWait.SpinUntil(() => store.Saves == 1, 3000));
			Assert.Equal(60, store.Saved!.RumbleLevel);

			vm.Settings.StartWithWindows = false;
			Assert.Equal([false], startup.Calls);
			vm.Settings.SaveNow();
			Assert.False(store.Saved!.StartWithWindows);
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	[Fact]
	public void JsonStore_RoundTrips_AndMissingFileIsFirstRun()
	{
		string dir = Path.Combine(Path.GetTempPath(), "untethered-" + Guid.NewGuid().ToString("N"));
		var store = new JsonSettingsStore(Path.Combine(dir, "settings.json"));
		Assert.Null(store.Load());
		store.Save(new AppSettings { Brightness = 33, Language = "tr" });
		AppSettings loaded = store.Load()!;
		Assert.Equal(33, loaded.Brightness);
		Assert.Equal("tr", loaded.Language);
		Directory.Delete(dir, true);
	}

	internal sealed class Probes(bool drivers) : IInstallRegistry, IBluetoothRadio, IControllerFinder, IRestartProbe
	{
		public (string? Version, string? Location)? FindProgram(string displayNamePrefix) => drivers ? ("1.0", @"C:\x") : null;
		public bool ServiceExists(string serviceName) => drivers;
		public bool FileExists(string path) => drivers;
		public RadioState Query() => RadioState.On;
		public IReadOnlyList<HidDevice> Find() => [new HidDevice(TestLink.BtPath, 0x054C, 0x0CE6, 78, 547, 64)];
		public bool? IsRestartPending() => false;
	}

	internal sealed class Links : IUrlLauncher
	{
		public List<string> Opened { get; } = [];
		public void Open(string target) => Opened.Add(target);
	}

	[AvaloniaFact]
	public async Task SetupWizard_OpensOnFirstRun_RunsActions_AndCloses()
	{
		var links = new Links();
		var probes = new Probes(drivers: false);
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false),
			new AppEnvironment(new MemorySettingsStore(), new TestStartup(), () => new SetupWizard(new SystemCheck(probes, probes, probes, probes), links)));

		SetupViewModel setup = Assert.IsType<SetupViewModel>(vm.CurrentPage);
		SetupStepItem drivers = setup.Steps.Single(s => s.Id == SetupStepId.Drivers);
		Assert.Equal(StepStatus.Needed, drivers.Status);
		Assert.True(drivers.HasNotes);
		await drivers.Actions[0].Command.ExecuteAsync(null);
		Assert.Equal([SetupLinks.UsbIpReleases], links.Opened);

		SetupStepItem adapter = setup.Steps.Single(s => s.Id == SetupStepId.AdapterTest);
		await adapter.Actions[0].Command.ExecuteAsync(null);
		Assert.Equal(StepStatus.Done, setup.Steps.Single(s => s.Id == SetupStepId.AdapterTest).Status);
		Assert.True(setup.HasMessage);

		setup.CloseCommand.Execute(null);
		Assert.Same(vm.Overview, vm.CurrentPage);
		vm.SettingsPage.RunWizardCommand.Execute(null);
		Assert.IsType<SetupViewModel>(vm.CurrentPage);
		vm.SelectedNav = vm.NavItems[1];
		Assert.Same(vm.SupportedGames, vm.CurrentPage);
	}

	[AvaloniaFact]
	public void SetupWizard_StaysClosed_WhenNothingIsNeeded()
	{
		var probes = new Probes(drivers: true);
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false),
			new AppEnvironment(new MemorySettingsStore(), null, () =>
				new SetupWizard(new SystemCheck(probes, probes, probes, probes), new Links()) { AdapterVerdict = Kopru.Core.Diagnostics.AdapterVerdict.Suitable }));
		Assert.Same(vm.Overview, vm.CurrentPage);
	}
}
