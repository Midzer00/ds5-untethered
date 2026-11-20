// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Health;
using Kopru.Core.Hid;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

public class HealthServiceTests : IDisposable
{
	private readonly TestControllers controllers = new();
	private readonly TestReadOnlySource readOnly = new();
	private readonly StepTime time = new();
	private readonly FakeSystem system = new();
	private readonly UrlLog urls = new();
	private readonly string dir = Path.Combine(Path.GetTempPath(), "kopru-health-svc-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		if (Directory.Exists(dir))
		{
			Directory.Delete(dir, true);
		}
	}

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private RealControllerService Service(LastBatteryStore? store = null) =>
		new(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
			BatteryProbe = new BatteryProbe(readOnly)
		}, new TestHiding(), controllers, a => a(), _ => null, timers: false, time: time,
			healthProbe: new HealthProbe(system, system, system, null, Path.Combine(dir, "adapter-check.json")),
			batteryStore: store, launcher: urls);

	private void Pair() => controllers.Paired.Add(new HidDevice(TestLink.BtPath, 0x054C, HidDevice.DualSenseProductId, 78, 547, 64));

	[Fact]
	public async Task Health_FollowsTheSystem()
	{
		using RealControllerService service = Service();
		system.UsbIp = false;
		HealthReport r = await service.RefreshHealthAsync();
		Assert.False(service.CanStartBridge);
		Assert.Equal(BlockingKind.DriverMissing, r.BlockingIssue?.Kind);
		Assert.Equal(Text.CannotStartDriver(), service.CanStartBridgeReason);

		system.UsbIp = true;
		system.Radio = RadioState.Off;
		await service.RefreshHealthAsync();
		Assert.Equal(ConnectionKind.BluetoothOff, service.Connection.Kind);

		system.Radio = RadioState.On;
		await service.RefreshHealthAsync();
		Assert.Equal(ConnectionKind.NeverPaired, service.Connection.Kind);

		system.Paired = [new PairedController("DualSense Wireless Controller", 1, false, true)];
		await service.RefreshHealthAsync();
		Assert.Equal(new ConnectionState(ConnectionKind.PairedNotConnected, true), service.Connection);
		Assert.True(service.CanStartBridge);
		Assert.False(service.CanTest);

		Pair();
		await service.RefreshHealthAsync();
		Assert.Equal(ConnectionKind.ConnectedBluetooth, service.Connection.Kind);
		Assert.True(service.CanTest);
		Assert.Empty(service.CanTestReason);
		Assert.Equal(HealthLevel.Unknown, service.Health.Chip(HealthChipKind.Adapter).Level);
	}

	[Fact]
	public async Task Health_RaisesStateChanged_OnlyWhenItChanged()
	{
		using RealControllerService service = Service();
		int raised = 0;
		service.StateChanged += (_, _) => raised++;
		await service.RefreshHealthAsync();
		int after = raised;
		Assert.True(after > 0);
		await service.RefreshHealthAsync();
		Assert.Equal(after, raised);
	}

	[Fact]
	public async Task Health_WhileRunning_ReportsHiding()
	{
		Pair();
		controllers.Links.Enqueue(new TestLink());
		using RealControllerService service = Service();
		await service.StartBridgeAsync();
		await service.RefreshHealthAsync();
		Assert.Equal(HealthLevel.Ok, service.Health.Chip(HealthChipKind.HidHide).Level);
		await service.StopBridgeAsync();
	}

	[Fact]
	public void Battery_IsPersisted_WhenAReadingArrives()
	{
		string path = Path.Combine(dir, LastBatteryStore.FileName);
		Pair();
		readOnly.Status = 0x16; // charging, 60 %
		using (RealControllerService service = Service(new LastBatteryStore(path)))
		{
			Assert.Null(service.LastKnownBattery);
			service.RefreshPresence();
			WaitUntil(() => service.LastKnownBattery != null);
			Assert.Equal(new LastKnownBattery(60, true, time.GetUtcNow()), service.LastKnownBattery);
		}
		Assert.Equal(60, new LastBatteryStore(path).Current?.Percent);
		time.Advance(TimeSpan.FromHours(2));
		Assert.Equal("Son: %60 (şarj oluyordu) · 2 sa önce", new LastBatteryStore(path).Current!.Describe(time.GetUtcNow(), Language.Turkish));
	}

	[Theory]
	[InlineData(FixAction.InstallDriver)]
	[InlineData(FixAction.InstallHidHide)]
	[InlineData(FixAction.OpenSteamInputHelp)]
	[InlineData(FixAction.RunAdapterTest)]
	[InlineData(FixAction.PairController)]
	public async Task Fix_PageActions_AreRaisedToTheUi(FixAction fix)
	{
		using RealControllerService service = Service();
		List<FixAction> seen = [];
		service.FixRequested += (_, f) => seen.Add(f);
		await service.ExecuteFix(fix);
		Assert.Equal([fix], seen);
		Assert.Empty(urls.Opened);
	}

	[Fact]
	public async Task Fix_BluetoothSettings_OpensSettings_AndNoneDoesNothing()
	{
		using RealControllerService service = Service();
		List<FixAction> seen = [];
		service.FixRequested += (_, f) => seen.Add(f);
		await service.ExecuteFix(FixAction.OpenBluetoothSettings);
		await service.ExecuteFix(FixAction.None);
		Assert.Equal([SetupLinks.BluetoothSettings], urls.Opened);
		Assert.Empty(seen);
	}

	[Fact]
	public async Task Fix_RestartBridge_StopsAndStarts()
	{
		using RealControllerService service = Service();
		await service.ExecuteFix(FixAction.RestartBridge);
		Assert.True(service.IsBridgeRunning);
		await service.StopBridgeAsync();
	}

	[Fact]
	public async Task FakeService_RoutesFixes()
	{
		using FakeControllerService fake = new(animate: false);
		List<FixAction> seen = [];
		fake.FixRequested += (_, f) => seen.Add(f);
		await fake.ExecuteFix(FixAction.RunAdapterTest);
		await fake.ExecuteFix(FixAction.RestartBridge);
		Assert.Equal([FixAction.RunAdapterTest], seen);
		Assert.Equal([FixAction.RunAdapterTest, FixAction.RestartBridge], fake.Fixes);
		Assert.True(fake.IsBridgeRunning);
		Assert.True(fake.CanStartBridge);
		Assert.Equal(ConnectionKind.ConnectedBluetooth, fake.Connection.Kind);
	}

	private sealed class FakeSystem : IInstallRegistry, IBluetoothRadio, IPairedControllers
	{
		public bool UsbIp { get; set; } = true;
		public RadioState Radio { get; set; } = RadioState.On;
		public IReadOnlyList<PairedController> Paired { get; set; } = [];

		public (string? Version, string? Location)? FindProgram(string prefix) => prefix switch
		{
			SystemCheck.UsbIpProgramPrefix when UsbIp => ("0.9.8.1", @"C:\USBip\"),
			SystemCheck.HidHideProgramPrefix => ("1.5", null),
			_ => null
		};

		public bool ServiceExists(string serviceName) => serviceName != "usbip2_ude" || UsbIp;
		public bool FileExists(string path) => UsbIp;
		public RadioState Query() => Radio;
		public IReadOnlyList<PairedController> Find() => Paired;
	}

	private sealed class UrlLog : IUrlLauncher
	{
		public List<string> Opened { get; } = [];
		public void Open(string target) => Opened.Add(target);
	}
}
