// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Hid;
using Kopru.Core.Lighting;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;
using Kopru.Core.Protocol;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

// A named Bluetooth controller link (its device path ends with the name).
internal sealed class NamedLink(string name) : IHidLink
{
	private readonly BlockingCollection<byte[]> inputs = [];
	private readonly CancellationTokenSource closed = new();

	public HidDevice Device { get; } = new(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_" + name, 0x054C, 0x0CE6, 78, 547, 64);

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

	public void Write(ReadOnlySpan<byte> report)
	{
	}

	public byte[] GetFeature(byte reportId) => new byte[64];
	public void CancelRead() => closed.Cancel();
	public void Dispose() => closed.Cancel();
}

// Controllers that come and go by name.
internal sealed class NamedControllers : IControllerSource, IControllerFinder
{
	private readonly object gate = new();
	private readonly List<string> present = [];

	public ConcurrentDictionary<string, NamedLink> Open { get; } = new();

	public void Add(string id)
	{
		lock (gate)
		{
			present.Add(id);
		}
	}

	public void Remove(string id)
	{
		lock (gate)
		{
			present.Remove(id);
		}
		if (Open.TryRemove(id, out NamedLink? link))
		{
			link.CancelRead();
		}
	}

	public IReadOnlyList<ControllerPresence> Enumerate()
	{
		lock (gate)
		{
			return [.. present.Select(id => new ControllerPresence(id, new NamedLink(id).Device))];
		}
	}

	public IReadOnlyList<HidDevice> Find() => [.. Enumerate().Select(p => p.Device)];

	public IHidLink? TryOpen() => null;

	public IHidLink? TryOpen(string id)
	{
		lock (gate)
		{
			if (!present.Contains(id) || Open.ContainsKey(id))
			{
				return null;
			}
		}
		NamedLink link = new(id);
		Open[id] = link;
		return link;
	}
}

public class MultiControllerTests
{
	private readonly NamedControllers controllers = new();

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private BridgeHost Host() => new(controllers, new TestIdentity(true), new TestAttacher())
	{
		PollInterval = TimeSpan.FromMilliseconds(10),
		MonitorInterval = TimeSpan.FromMilliseconds(10),
		NoInputTimeout = Timeout.InfiniteTimeSpan
	};

	private (RealControllerService Service, BridgeManager Manager) Service(TestHiding hiding)
	{
		BridgeHost primary = Host();
		BridgeManager manager = new(controllers, primary, _ => Host());
		GameModeController games = new(FakeControllerService.SampleCatalog, new FakeGameWatcher(null), manager);
		RealControllerService service = new(primary, hiding, controllers, a => a(), d => d.Path, timers: false, games: games);
		service.UseManager(manager, watch: false);
		return (service, manager);
	}

	[Fact]
	public async Task TwoControllers_BothBridgedAndHidden_SelectionRoutesTheSingleMembers()
	{
		TestHiding hiding = new();
		(RealControllerService service, BridgeManager manager) = Service(hiding);
		using RealControllerService _ = service;
		controllers.Add("a");
		controllers.Add("b");

		await service.StartBridgeAsync();
		WaitUntil(() => manager.Host(1).State == BridgeState.Running && manager.Host(2).State == BridgeState.Running);
		controllers.Open["a"].Feed(0x05); // 50 %, discharging
		controllers.Open["b"].Feed(0x08); // 80 %
		WaitUntil(() => service.Controllers.Count == 2 && service.Controllers.All(c => c.HasBattery));

		Assert.Equal([1, 2], service.Controllers.Select(c => c.Slot));
		Assert.Contains("hide " + controllers.Open["a"].Device.Path, hiding.Calls);
		Assert.Contains("hide " + controllers.Open["b"].Device.Path, hiding.Calls);
		Assert.Equal("a", service.SelectedController!.Id);
		Assert.Equal(50, service.BatteryPercent);

		service.SelectController("b");
		Assert.Equal(2, service.SelectedController!.Slot);
		Assert.Equal(80, service.BatteryPercent);
		Assert.True(service.IsGameConnected);

		// The second controller's player LEDs show its slot.
		service.ApplySettings(new ControllerSettings());
		WaitUntil(() => manager.Host(2).Light.Options.PlayerPattern == PlayerLedPattern.Two);
		Assert.Equal(PlayerLedPattern.One, manager.Host(1).Light.Options.PlayerPattern);

		// It leaves: the selection falls back to slot 1.
		controllers.Remove("b");
		await manager.RefreshAsync();
		Assert.Equal(1, service.SelectedController!.Slot);
		Assert.Single(service.Controllers);

		await service.StopBridgeAsync();
		Assert.All(manager.Hosts, h => Assert.Equal(BridgeState.Stopped, h.State));
		Assert.Equal("restore", hiding.Calls[^1]);
	}

	[AvaloniaFact]
	public async Task PerControllerProfile_AppliesToThatControllerOnly_AndIsSaved()
	{
		(RealControllerService service, BridgeManager manager) = Service(new TestHiding());
		using RealControllerService _ = service;
		controllers.Add("a");
		controllers.Add("b");
		await manager.RefreshAsync();
		SettingsState settings = new(service);
		ControllerProfile solo = settings.Profiles.Create("Solo", new ProfileValues { RumbleLevel = 40, LightbarColour = 0xFF0000 })!;

		settings.AssignControllerProfile("b", solo.Id);

		Assert.Equal(0.4, manager.Host(2).Profile.RumbleLevel, 3);
		Assert.Equal(1.0, manager.Host(1).Profile.RumbleLevel, 3);
		WaitUntil(() => manager.Host(2).Light.Red == 0xFF);
		Assert.Equal(solo.Id, settings.ControllerProfileId("b"));
		Assert.Equal(solo.Id, settings.ToAppSettings().ControllerProfiles!["b"]);
		Assert.True(service.Controllers.Single(c => c.Id == "b").HasOwnProfile);

		// Game mode switches still reach every controller without its own profile.
		settings.RumbleLevel = 60;
		Assert.Equal(0.6, manager.Host(1).Profile.RumbleLevel, 3);
		Assert.Equal(0.6, manager.Host(3).Profile.RumbleLevel, 3);
		Assert.Equal(0.4, manager.Host(2).Profile.RumbleLevel, 3);

		settings.AssignControllerProfile("b", null);
		Assert.Equal(0.6, manager.Host(2).Profile.RumbleLevel, 3);
		Assert.Null(settings.ToAppSettings().ControllerProfiles);
	}

	[AvaloniaFact]
	public async Task Notifications_NameTheSlot_WithSeveralControllers()
	{
		(RealControllerService service, BridgeManager manager) = Service(new TestHiding());
		using RealControllerService _ = service;
		MainWindowViewModel vm = new(service);
		List<string> titles = [];
		vm.Notifications.Raised += n => titles.Add(n.Title);

		controllers.Add("a");
		await manager.RefreshAsync();
		controllers.Add("b");
		await manager.RefreshAsync();
		controllers.Remove("b");
		await manager.RefreshAsync();

		Assert.Equal([Text.NotifyConnectedTitle(), Text.NotifyControllerConnectedTitle(2), Text.NotifyControllerDisconnectedTitle(2)], titles);
		Assert.Equal("Kumanda 2 Bağlandı", Text.NotifyControllerConnectedTitle(2, Language.Turkish));
	}

	[AvaloniaFact]
	public async Task SingleController_LooksAsBefore()
	{
		(RealControllerService service, BridgeManager manager) = Service(new TestHiding());
		using RealControllerService _ = service;
		MainWindowViewModel vm = new(service);
		List<string> titles = [];
		vm.Notifications.Raised += n => titles.Add(n.Title);

		controllers.Add("a");
		await manager.RefreshAsync();
		Dispatcher.UIThread.RunJobs();

		Assert.False(vm.Picker.IsMulti);
		Assert.False(vm.Overview.Picker.IsMulti);
		Assert.True(vm.IsConnected);
		Assert.Equal([Text.NotifyConnectedTitle()], titles);
		Assert.Equal(1, service.SelectedController!.Slot);
	}

	private static ControllerEntry Entry(string id, int slot, double battery, bool edge = false) =>
		new(id, slot, edge ? "DualSense Edge" : "DualSense", edge, true, battery, false, BridgeState.Running);

	[AvaloniaFact]
	public void Picker_ListsTheControllers_AndSelectsOne()
	{
		FakeControllerService fake = new(animate: false);
		fake.ControllerList.Add(Entry("a", 1, 78));
		fake.ControllerList.Add(Entry("b", 2, 40, edge: true));
		MainWindowViewModel vm = new(fake);
		ControllerPickerViewModel picker = vm.Light.Picker;

		Assert.True(picker.IsMulti);
		Assert.Equal([Text.ControllerSlotName(1), Text.ControllerSlotName(2)], picker.Items.Select(i => i.SlotText));
		Assert.True(picker.Items[0].IsSelected);

		picker.Items[1].SelectCommand.Execute(null);
		Dispatcher.UIThread.RunJobs();

		Assert.Equal("b", fake.SelectedId);
		Assert.True(vm.Picker.Items[1].IsSelected);
		Assert.True(vm.Tester.Picker.Items[1].IsSelected);

		// The profile picker sets the selected controller's own profile.
		ControllerProfile p = vm.Settings.Profiles.Create("Two", new ProfileValues())!;
		picker.SelectedProfile = picker.ProfileOptions.Single(o => o.Id == p.Id);
		Assert.NotNull(fake.ControllerProfiles["b"]);
		Assert.Equal(p.Id, picker.SelectedProfile!.Id);
	}

	// Renders the overview with two controllers (docs/screens/overview-multi-*.png when KOPRU_RENDER_SCREENS=1).
	[AvaloniaFact]
	public void RenderOverview_WithTwoControllers()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
		{
			Language? before = Localizer.Chosen;
			try
			{
				Localizer.Chosen = language;
				FakeControllerService fake = new(animate: false);
				fake.ControllerList.Add(Entry("a", 1, 78));
				fake.ControllerList.Add(Entry("b", 2, 41, edge: true));
				MainWindowViewModel vm = new(fake);
				MainWindow window = new() { DataContext = vm, Width = 1100, Height = 700 };
				window.Show();
				Dispatcher.UIThread.RunJobs();
				AvaloniaHeadlessPlatform.ForceRenderTimerTick();
				Thread.Sleep(320);
				Dispatcher.UIThread.RunJobs();
				AvaloniaHeadlessPlatform.ForceRenderTimerTick();
				using var frame = window.CaptureRenderedFrame();
				Assert.NotNull(frame);
				if (writeFiles)
				{
					string dir = Path.Combine(RepoRoot(), "docs", "screens");
					Directory.CreateDirectory(dir);
					frame!.Save(Path.Combine(dir, $"overview-multi-{code}.png"));
				}
				window.Close();
			}
			finally
			{
				Localizer.Chosen = before;
			}
		}
	}

	private static string RepoRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		return dir ?? throw new InvalidOperationException("Kopru.sln not found");
	}
}
