// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Health;
using Kopru.Core.Hid;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

// The app's recurring work while it sits next to the clock or a game runs: one controller listing per tick, the haptics
// meter only with the bridge, StateChanged only for a change the UI shows (and that change still reaches the view
// models), page timers only while the window is on screen, settings.json not rewritten for every battery percent.
public class IdleCostAppTests
{
	private sealed class CountingFinder : IControllerFinder, IControllerSource
	{
		public List<HidDevice> Devices { get; } = [];
		public int Calls { get; private set; }

		public IReadOnlyList<HidDevice> Find()
		{
			Calls++;
			return [.. Devices];
		}

		public IHidLink? TryOpen() => null;
	}

	private static RealControllerService Service(CountingFinder finder, HealthProbe? probe = null) =>
		new(new BridgeHost(finder, new TestIdentity(true), new TestAttacher())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
		}, new TestHiding(), finder, a => a(), _ => null, timers: false, healthProbe: probe);

	private static void Settle() => Dispatcher.UIThread.RunJobs();

	[Fact]
	public void PresenceTick_ListsTheControllersOnce()
	{
		CountingFinder finder = new();
		finder.Devices.Add(new HidDevice(TestLink.BtPath, 0x054C, HidDevice.DualSenseProductId, 78, 547, 64));
		using RealControllerService service = Service(finder);
		service.RefreshPresence();
		Assert.Equal(1, finder.Calls);
		Assert.True(service.IsConnected);
		service.RefreshPresence();
		Assert.Equal(2, finder.Calls);
	}

	[Fact]
	public async Task HapticsMeter_RunsOnlyWhileTheBridgeRuns()
	{
		using RealControllerService service = Service(new CountingFinder());
		Assert.False(service.IsMeterRunning);
		await service.StartBridgeAsync();
		Assert.True(service.IsMeterRunning);
		// Taking the levels raises no StateChanged (the Overview reads them itself).
		int raised = 0;
		service.StateChanged += (_, _) => raised++;
		service.MeterTick();
		service.MeterTick();
		Assert.Equal(0, raised);
		await service.StopBridgeAsync();
		Assert.False(service.IsMeterRunning);
	}

	private static BridgeStatus Running(double rate = 250.2, int battery = 70) =>
		new(BridgeState.Running, new BatteryStatus(battery, ChargeState.Discharging), rate, 250, BridgeError.None, null, 1.21, 3.44, 0.051,
			new LinkQualityReading(LinkQuality.Good, 3.4, 0, 0, rate));

	[Fact]
	public void HostStatus_AnUnchangedTick_RaisesNothing_ARealChangeDoes()
	{
		using RealControllerService service = Service(new CountingFinder());
		int raised = 0;
		service.StateChanged += (_, _) => raised++;

		service.OnHostStatus(1, Running());
		Assert.Equal(1, raised);
		// The three 250 ms ticks between two once-a-second measurements, and jitter below what is shown.
		service.OnHostStatus(1, Running());
		service.OnHostStatus(1, Running());
		service.OnHostStatus(1, Running(rate: 250.4));
		Assert.Equal(1, raised);

		// What the UI shows changed.
		service.OnHostStatus(1, Running(rate: 251.0));
		Assert.Equal(2, raised);
		service.OnHostStatus(1, Running(rate: 251.0, battery: 60));
		Assert.Equal(3, raised);
		service.OnHostStatus(1, Running(rate: 251.0, battery: 60) with { State = BridgeState.WaitingForController });
		Assert.Equal(4, raised);
		service.OnHostStatus(1, Running(rate: 251.0, battery: 60) with { State = BridgeState.WaitingForController, LinkLatencyP50Ms = 1.5 });
		Assert.Equal(5, raised);
		BridgeStatus poor = Running(rate: 251.0, battery: 60) with { Link = new LinkQualityReading(LinkQuality.Poor, 40, 2, 3, 251) };
		service.OnHostStatus(1, poor);
		Assert.Equal(6, raised);
		// Each slot is followed on its own.
		service.OnHostStatus(2, poor);
		Assert.Equal(7, raised);

		// A due weak-link warning is looked at on every tick (its toast may wait for the game to be left).
		BridgeStatus due = poor with { Link = poor.Link! with { WarningDue = true } };
		service.OnHostStatus(1, due);
		service.OnHostStatus(1, due);
		Assert.Equal(9, raised);
	}

	[Fact]
	public void HeadphonesPlugged_RaisesStateChanged_OnItsOwn()
	{
		using RealControllerService service = Service(new CountingFinder());
		int raised = 0;
		service.StateChanged += (_, _) => raised++;
		service.OnHostStatus(1, Running(), headphonesPlugged: false);
		service.OnHostStatus(1, Running(), headphonesPlugged: true);
		Assert.Equal(2, raised);
		service.OnHostStatus(1, Running(), headphonesPlugged: true);
		Assert.Equal(2, raised);
		service.OnHostStatus(1, Running(), headphonesPlugged: false);
		Assert.Equal(3, raised);
	}

	// How each value the UI (Overview, sidebar, tray, Settings) reads from the service reaches it now that the status
	// heartbeat no longer raises StateChanged on every tick. A new service value fails this test until it has an entry,
	// and its entry must name a path that really raises (or a reader that polls it).
	private static readonly Dictionary<string, string> HowServiceValuesReachTheUi = new()
	{
		[nameof(IControllerService.Kind)] = "device: presence change Raise, bridge state in ShownStatus, manager ControllersChanged Raise",
		[nameof(IControllerService.DeviceName)] = "follows Kind",
		[nameof(IControllerService.IsConnected)] = "device: presence change Raise, bridge state in ShownStatus, manager ControllersChanged Raise",
		[nameof(IControllerService.HasBattery)] = "ShownStatus.BatteryPercent; battery probe Raise",
		[nameof(IControllerService.BatteryPercent)] = "ShownStatus.BatteryPercent; battery probe Raise",
		[nameof(IControllerService.IsCharging)] = "ShownStatus.Charge",
		[nameof(IControllerService.InputRateHz)] = "ShownStatus.InputRate (rounded as shown)",
		[nameof(IControllerService.LatencyMs)] = "follows InputRateHz",
		[nameof(IControllerService.Latency)] = "ShownStatus.LinkP50/LinkP99/Relay (as printed)",
		[nameof(IControllerService.LinkQuality)] = "ShownStatus.Link/LinkWarningDue",
		[nameof(IControllerService.LinkSeconds)] = "polled once a second by the Overview live card while shown (the bridge keeps the history meanwhile)",
		[nameof(IControllerService.RawInput)] = "polled by the Overview and tester timers",
		[nameof(IControllerService.SteamInput)] = "CheckSteamInput Raise on change",
		[nameof(IControllerService.IsBridgeRunning)] = "start/stop Raise",
		[nameof(IControllerService.IsHapticsActive)] = "ShownStatus.HapticsActive",
		[nameof(IControllerService.HeadphonesPlugged)] = "ShownStatus.HeadphonesPlugged (read beside each status)",
		[nameof(IControllerService.IsGameConnected)] = "ShownStatus.State",
		[nameof(IControllerService.HapticsLeftLevel)] = "polled by the Overview live card's Signals view",
		[nameof(IControllerService.HapticsRightLevel)] = "polled by the Overview live card's Signals view",
		[nameof(IControllerService.Warning)] = "ShownStatus.LastError; hiding/test warnings Raise",
		[nameof(IControllerService.Games)] = "Games.StateChanged and MemoryChanged Raise",
		[nameof(IControllerService.Connection)] = "health refresh Raise when the report changed",
		[nameof(IControllerService.Health)] = "health refresh Raise when the report changed",
		[nameof(IControllerService.LastKnownBattery)] = "RecordBattery Raise",
		[nameof(IControllerService.CanStartBridge)] = "follows Health",
		[nameof(IControllerService.CanStartBridgeReason)] = "follows Health",
		[nameof(IControllerService.CanTest)] = "follows Health",
		[nameof(IControllerService.CanTestReason)] = "follows Health",
		[nameof(IControllerService.Details)] = "DetailsRead Raise, USB details Raise",
		[nameof(IControllerService.ConnectedSince)] = "polled by the controller info clock",
		[nameof(IControllerService.CablePluggedIn)] = "device: cable twin set change Raise, bridge stop Raise",
		[nameof(IControllerService.IsWired)] = "device: wired reconcile Raise, tick Raise on change",
		[nameof(IControllerService.IsCableOnly)] = "device: tick Raise when the cable-only pad changes, wired reconcile Raise, host status",
		[nameof(IControllerService.Controllers)] = "manager ControllersChanged Raise, per-slot ShownStatus, own profile Raise",
		[nameof(IControllerService.SelectedController)] = "SelectController Raise, follows Controllers",
		[nameof(IControllerService.AudioDevices)] = "AudioDevices.Changed (own event)",
		[nameof(IControllerService.AudioHapticsStatus)] = "AudioHapticsStatusChanged (own event)",
		[nameof(IControllerService.DsxStatus)] = "DsxStatusChanged (own event: state changes, a mod's first message or end, once a second while a mod's effects are active)",
		[nameof(IControllerService.ControllerLight)] = "polled by LiveLight while the window is shown (pushed only when its change counter moves)",
		// Mouse Mode inputs, not shown: read on demand by SettingsState when its own event fires, or set by the UI.
		[nameof(IControllerService.GameUsingControllerNow)] = "not shown: read on demand by SettingsState.OnForegroundChanged (ForegroundChanged)",
		[nameof(IControllerService.PossibleGameInFront)] = "not shown: own event ForegroundChanged (game tick), also read on demand on GameUsingController",
		[nameof(IControllerService.ButtonPageShown)] = "not shown: input set by MainWindowViewModel, read on demand by Shortcuts.AppInFront (touchpad double press)",
	};

	[Fact]
	public void EveryServiceValue_HasAWayToReachTheUi()
	{
		string[] values = [.. typeof(IControllerService).GetProperties().Select(p => p.Name)];
		Assert.Empty(values.Except(HowServiceValuesReachTheUi.Keys));
		Assert.Empty(HowServiceValuesReachTheUi.Keys.Except(values));
	}

	// "Charging, level not reported" (USB) is shown differently from a real 0 % charging reading, so a switch between
	// them raises StateChanged.
	[Fact]
	public void ShownStatus_ChargingWithoutALevel_DiffersFromZeroPercent()
	{
		BridgeStatus s = Running();
		ShownStatus known = ShownStatus.Of(s with { Battery = new BatteryStatus(0, ChargeState.Charging) });
		ShownStatus unknown = ShownStatus.Of(s with { Battery = new BatteryStatus(0, ChargeState.Charging, LevelKnown: false) });
		Assert.NotEqual(known, unknown);
		Assert.Null(unknown.BatteryPercent);
		Assert.Equal(ChargeState.Charging, unknown.Charge);
	}

	// Every part of a bridge status is either in ShownStatus (a change raises StateChanged) or explicitly not shown.
	[Fact]
	public void EveryBridgeStatusValue_IsShownOrKnownNotToBe()
	{
		BridgeStatus s = Running();
		Dictionary<string, BridgeStatus?> changed = new()
		{
			[nameof(BridgeStatus.State)] = s with { State = BridgeState.Connecting },
			[nameof(BridgeStatus.Battery)] = s with { Battery = new BatteryStatus(70, ChargeState.Charging) },
			[nameof(BridgeStatus.InputRateHz)] = s with { InputRateHz = 200 },
			[nameof(BridgeStatus.HapticsReportsPerSecond)] = s with { HapticsReportsPerSecond = 0 },
			[nameof(BridgeStatus.LastError)] = s with { LastError = BridgeError.ControllerLost },
			[nameof(BridgeStatus.LastErrorDetail)] = s with { LastErrorDetail = "x" },
			[nameof(BridgeStatus.LinkLatencyP50Ms)] = s with { LinkLatencyP50Ms = 2.5 },
			[nameof(BridgeStatus.LinkLatencyP99Ms)] = s with { LinkLatencyP99Ms = 9.5 },
			[nameof(BridgeStatus.RelayMs)] = s with { RelayMs = 0.2 },
			[nameof(BridgeStatus.Link)] = s with { Link = s.Link! with { State = LinkQuality.Borderline } },
		};
		// Not shown: the last minute of the link, polled once a second by the Overview's live card while it is shown.
		string[] polled = [nameof(BridgeStatus.LinkSeconds)];
		Assert.Equal(ShownStatus.Of(s), ShownStatus.Of(s with { LinkSeconds = [new LinkSecondPoint(1, 4, 700, 700, 2, 1)] }));
		string[] parts = [.. typeof(BridgeStatus).GetConstructors().Single(c => c.GetParameters().Length > 1).GetParameters().Select(p => p.Name!)];
		Assert.Equal(parts.Except(polled).OrderBy(p => p), changed.Keys.OrderBy(p => p));
		foreach ((string name, BridgeStatus? next) in changed)
		{
			Assert.True(ShownStatus.Of(s) != ShownStatus.Of(next!), name + " is not in ShownStatus");
		}

		// The link reading: the UI shows its class and acts on its warning; the numbers behind the class are not shown.
		LinkQualityReading link = s.Link!;
		Dictionary<string, (LinkQualityReading Next, bool Shown)> linkParts = new()
		{
			[nameof(LinkQualityReading.State)] = (link with { State = LinkQuality.Poor }, true),
			[nameof(LinkQualityReading.WarningDue)] = (link with { WarningDue = true }, true),
			[nameof(LinkQualityReading.WorstDelayMs)] = (link with { WorstDelayMs = 30 }, false),
			[nameof(LinkQualityReading.LostPercent)] = (link with { LostPercent = 3 }, false),
			[nameof(LinkQualityReading.LongGaps)] = (link with { LongGaps = 4 }, false),
			[nameof(LinkQualityReading.RateHz)] = (link with { RateHz = 100 }, false),
		};
		string[] linkNames = [.. typeof(LinkQualityReading).GetConstructors().Single(c => c.GetParameters().Length > 1).GetParameters().Select(p => p.Name!)];
		Assert.Equal(linkNames.OrderBy(p => p), linkParts.Keys.OrderBy(p => p));
		foreach ((string name, (LinkQualityReading next, bool shown)) in linkParts)
		{
			Assert.Equal(shown, ShownStatus.Of(s) != ShownStatus.Of(s with { Link = next }));
		}
		Assert.NotEqual(ShownStatus.Of(s, headphonesPlugged: false), ShownStatus.Of(s, headphonesPlugged: true));
	}

	[Fact]
	public void ShownStatus_FollowsTheShownPrecision()
	{
		Assert.Equal(ShownStatus.Of(Running(rate: 249.6)), ShownStatus.Of(Running(rate: 250.4)));
		Assert.NotEqual(ShownStatus.Of(Running()), ShownStatus.Of(Running() with { LinkLatencyP99Ms = 3.5 }));
		Assert.NotEqual(ShownStatus.Of(Running()), ShownStatus.Of(Running() with { HapticsReportsPerSecond = 0 }));
		Assert.NotEqual(ShownStatus.Of(Running()), ShownStatus.Of(Running() with { LastError = BridgeError.ControllerLost }));
	}

	[AvaloniaFact]
	public void ARealChange_StillReachesTheViewModels_AndAnUnchangedOneRaisesNothing()
	{
		FakeControllerService fake = new(animate: false);
		MainWindowViewModel vm = new(fake);
		List<string?> sidebar = [];
		vm.PropertyChanged += (_, e) => sidebar.Add(e.PropertyName);
		TrayViewModel tray = new(fake, vm.Settings, new NoShell());
		int trayRefreshes = 0;
		tray.PropertyChanged += (_, _) => trayRefreshes++;

		fake.RaiseStateChanged();
		Settle();
		Assert.Empty(sidebar);
		Assert.Equal(0, trayRefreshes);

		fake.BatteryPercent = 41;
		fake.RaiseStateChanged();
		Settle();
		Assert.Contains(nameof(MainWindowViewModel.BatteryShort), sidebar);
		Assert.Contains(nameof(MainWindowViewModel.BatteryRowText), sidebar);
		Assert.Contains(nameof(MainWindowViewModel.BatteryPercent), sidebar);
		Assert.DoesNotContain(nameof(MainWindowViewModel.IsConnected), sidebar);
		Assert.DoesNotContain(string.Empty, sidebar);
		Assert.Equal(Kopru.Core.Localization.Text.BatteryValue(41), vm.BatteryShort);
		Assert.Equal(1, trayRefreshes);
		Assert.Contains(Kopru.Core.Localization.Text.TrayBattery(41), tray.ToolTip);

		sidebar.Clear();
		fake.IsConnected = false;
		fake.RaiseStateChanged();
		Settle();
		Assert.Contains(nameof(MainWindowViewModel.IsConnected), sidebar);
		Assert.Contains(nameof(MainWindowViewModel.DeviceName), sidebar);
		Assert.Equal(2, trayRefreshes);
		Assert.Equal(Kopru.Core.Localization.Text.DeviceNoController(), vm.DeviceName);
	}

	private sealed class NoShell : IAppShell
	{
		public void ShowMainWindow(AppPage? page = null)
		{
		}

		public void Exit()
		{
		}
	}

	private static (MainWindowViewModel Vm, MainWindow Window) Open(FakeControllerService service)
	{
		var vm = new MainWindowViewModel(service);
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		Settle();
		return (vm, window);
	}

	private static void Go(MainWindowViewModel vm, Window window, PageViewModel page)
	{
		vm.Navigate(page);
		Settle();
		window.UpdateLayout();
	}

	[AvaloniaFact]
	public void AWindowNeverShown_RunsNoPageTimer()
	{
		var service = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(service);
		Assert.True(vm.Overview.IsFollowing);
		// Started with --minimized: the window exists but was never shown.
		var window = new MainWindow { DataContext = vm };
		Assert.False(vm.IsWindowShown);
		Assert.False(vm.Overview.IsFollowing);
		Assert.False(vm.SettingsPage.Info.IsTicking);
		window.Show();
		Settle();
		Assert.True(vm.IsWindowShown);
		Assert.True(vm.Overview.IsFollowing);
		Assert.True(vm.SettingsPage.Info.IsTicking);
		window.Close();
	}

	[AvaloniaFact]
	public void PageTimers_StopWhenTheWindowIsHiddenOrMinimised_AndResumeWhenShown()
	{
		var service = new FakeControllerService(animate: false);
		(MainWindowViewModel vm, MainWindow window) = Open(service);
		Assert.True(vm.Overview.IsFollowing);

		// Hidden to the icon next to the clock.
		window.Hide();
		Settle();
		Assert.False(vm.IsWindowShown);
		Assert.False(vm.Overview.IsFollowing);
		window.Show();
		Settle();
		Assert.True(vm.Overview.IsFollowing);

		// Minimised.
		window.WindowState = WindowState.Minimized;
		Settle();
		Assert.False(vm.Overview.IsFollowing);
		window.WindowState = WindowState.Normal;
		Settle();
		Assert.True(vm.Overview.IsFollowing);

		// The controller page's tester holds the controller only while the window is on screen.
		vm.OpenController(ControllerPageViewModel.TestTab);
		Settle();
		Assert.NotNull(service.LiveHandler);
		Assert.False(vm.Overview.IsFollowing);
		window.Hide();
		Settle();
		Assert.Null(service.LiveHandler);
		window.Show();
		Settle();
		Assert.NotNull(service.LiveHandler);

		// The gyro preview (Gyro & Sticks tab).
		vm.Controller.SelectedTab = ControllerPageViewModel.GyroTab;
		Assert.True(vm.Sticks.IsPreviewRunning);
		window.Hide();
		Settle();
		Assert.False(vm.Sticks.IsPreviewRunning);
		window.Show();
		Settle();
		Assert.True(vm.Sticks.IsPreviewRunning);

		// The light preview.
		Go(vm, window, vm.Light);
		LightView light = window.GetVisualDescendants().OfType<LightView>().Single();
		Assert.True(light.IsPreviewRunning);
		window.Hide();
		Settle();
		Assert.False(light.IsPreviewRunning);
		window.Show();
		Settle();
		Assert.True(light.IsPreviewRunning);
		window.Close();
	}

	[AvaloniaFact]
	public async Task MicMeter_RunsOnlyOnTheShownSettingsPage()
	{
		var service = new FakeControllerService(animate: false);
		(MainWindowViewModel vm, MainWindow window) = Open(service);
		vm.Settings.MicEnabled = true;
		await service.StartBridgeAsync();
		Settle();
		Assert.False(vm.SettingsPage.IsMicMeterRunning);
		Go(vm, window, vm.SettingsPage);
		Assert.True(vm.SettingsPage.IsMicMeterRunning);
		window.Hide();
		Settle();
		Assert.False(vm.SettingsPage.IsMicMeterRunning);
		Assert.False(vm.SettingsPage.Info.IsTicking);
		window.Show();
		Settle();
		Assert.True(vm.SettingsPage.IsMicMeterRunning);
		Assert.True(vm.SettingsPage.Info.IsTicking);
		Go(vm, window, vm.Overview);
		Assert.False(vm.SettingsPage.IsMicMeterRunning);
		window.Close();
	}

	[AvaloniaFact]
	public void ShowingTheWindow_AndTheSetupPage_AskForAFullHealthRead()
	{
		var service = new HealthCountingService();
		var probes = new RealServiceTests.Probes(drivers: true);
		var vm = new MainWindowViewModel(service, new AppEnvironment(null, null,
			() => new SetupWizard(new SystemCheck(probes, probes, probes, probes), new RealServiceTests.Links())));
		int before = service.HealthRefreshes;
		vm.ShowSetup();
		Assert.Equal(before + 1, service.HealthRefreshes);
		vm.SetWindowShown(false);
		vm.SetWindowShown(true);
		Assert.Equal(before + 2, service.HealthRefreshes);
	}

	private sealed class HealthCountingService : IControllerService
	{
		private readonly FakeControllerService inner = new(animate: false);
		public int HealthRefreshes { get; private set; }
		public void RefreshHealth() => HealthRefreshes++;
		public ControllerKind Kind => inner.Kind;
		public string DeviceName => inner.DeviceName;
		public bool IsConnected => inner.IsConnected;
		public bool HasBattery => inner.HasBattery;
		public double BatteryPercent => inner.BatteryPercent;
		public bool IsCharging => inner.IsCharging;
		public double InputRateHz => inner.InputRateHz;
		public double LatencyMs => inner.LatencyMs;
		public LatencyReading? Latency => inner.Latency;
		public byte[]? RawInput => inner.RawInput;
		public Kopru.Core.Steam.SteamInputResult? SteamInput => inner.SteamInput;
		public bool IsBridgeRunning => inner.IsBridgeRunning;
		public bool IsHapticsActive => inner.IsHapticsActive;
		public bool HeadphonesPlugged => inner.HeadphonesPlugged;
		public bool IsGameConnected => inner.IsGameConnected;
		public double HapticsLeftLevel => inner.HapticsLeftLevel;
		public double HapticsRightLevel => inner.HapticsRightLevel;
		public string Warning => inner.Warning;
		public Kopru.Core.Games.GameModeController Games => inner.Games;
		public ConnectionState Connection => inner.Connection;
		public HealthReport Health => inner.Health;
		public LastKnownBattery? LastKnownBattery => inner.LastKnownBattery;
		public bool CanStartBridge => inner.CanStartBridge;
		public string CanStartBridgeReason => inner.CanStartBridgeReason;
		public bool CanTest => inner.CanTest;
		public string CanTestReason => inner.CanTestReason;
		public Task ExecuteFix(FixAction fix) => inner.ExecuteFix(fix);

		public event EventHandler<FixAction>? FixRequested
		{
			add => inner.FixRequested += value;
			remove => inner.FixRequested -= value;
		}

		public Kopru.Core.Mods.DsxStatus DsxStatus => inner.DsxStatus;

		public event EventHandler? DsxStatusChanged
		{
			add => inner.DsxStatusChanged += value;
			remove => inner.DsxStatusChanged -= value;
		}

		public event EventHandler? StateChanged
		{
			add => inner.StateChanged += value;
			remove => inner.StateChanged -= value;
		}

		public event EventHandler<ServiceNotice>? Notice
		{
			add => inner.Notice += value;
			remove => inner.Notice -= value;
		}

		public event EventHandler<Kopru.Core.Input.ShortcutAction>? Shortcut
		{
			add => inner.Shortcut += value;
			remove => inner.Shortcut -= value;
		}

		public event EventHandler<GameUse>? GameUsingController
		{
			add => inner.GameUsingController += value;
			remove => inner.GameUsingController -= value;
		}

		public GameUse? GameUsingControllerNow => inner.GameUsingControllerNow;

		public string? PossibleGameInFront => inner.PossibleGameInFront;

		public event EventHandler? ForegroundChanged
		{
			add => inner.ForegroundChanged += value;
			remove => inner.ForegroundChanged -= value;
		}

		public bool ButtonPageShown
		{
			get => inner.ButtonPageShown;
			set => inner.ButtonPageShown = value;
		}

		public void ShowProfileIndicator(int index) => inner.ShowProfileIndicator(index);
		public Task StartBridgeAsync() => inner.StartBridgeAsync();
		public Task StopBridgeAsync() => inner.StopBridgeAsync();
		public void ApplySettings(ControllerSettings settings) => inner.ApplySettings(settings);
		public void TestHaptics(HapticSide side) => inner.TestHaptics(side);
		public void TestRumble() => inner.TestRumble();
		public void TestFallbackTrigger() => inner.TestFallbackTrigger();
		public void TestTriggers(byte[] right, byte[] left) => inner.TestTriggers(right, left);
		public void TestTrigger(TriggerTestEffect effect) => inner.TestTrigger(effect);
		public void TestLight(uint rgb) => inner.TestLight(rgb);
		public Task<AdapterTestResult> TestAdapterAsync(CancellationToken cancel = default) => inner.TestAdapterAsync(cancel);
		public Task<Kopru.Core.Input.DriftCalibrationResult> CalibrateDriftAsync(TimeSpan duration, IProgress<double>? progress = null) =>
			inner.CalibrateDriftAsync(duration, progress);
		public Task<bool> PowerOffAsync() => inner.PowerOffAsync();
		public Task ExportDiagnosticsAsync(string path) => inner.ExportDiagnosticsAsync(path);
		public Task ShutdownAsync() => inner.ShutdownAsync();
	}

	private static ControllerEntry Entry(double battery) =>
		new("fake-pad-a", 1, "DualSense", false, true, battery, false, BridgeState.Stopped);

	[Fact]
	public void Registry_ABatteryReadingAlone_IsNotAChange()
	{
		ControllerRegistry r = new();
		int changes = 0, batteries = 0;
		r.Changed += () => changes++;
		r.BatteryChanged += () => batteries++;
		r.Observe([Entry(70)]);
		Assert.Equal(1, changes);
		r.Observe([Entry(70)]);
		r.Observe([Entry(60)]);
		Assert.Equal(1, changes);
		Assert.Equal(1, batteries);
		Assert.Equal(60, r.Find("fake-pad-a")!.LastBattery);
	}

	// The debounce timer writes a snapshot taken before a newer battery reading: that reading still goes out at exit.
	[AvaloniaFact]
	public void Settings_ABatteryReadingNewerThanThePendingSave_IsStillSavedAtExit()
	{
		long now = 0;
		FakeControllerService fake = new(animate: false);
		MemorySettingsStore store = new(new AppSettings());
		MainWindowViewModel vm = new(fake, new AppEnvironment(store, Clock: () => now));
		fake.ControllerList.Add(Entry(70));
		fake.RaiseStateChanged();
		Settle();
		vm.Settings.SaveNow();
		int saves = store.Saves;

		// A rename schedules a save (snapshot with 70 %); a new reading arrives before the debounce timer writes it.
		now += 10_000;
		Assert.True(vm.Settings.RenameController("fake-pad-a", "Couch Pad"));
		fake.ControllerList[0] = Entry(60);
		fake.RaiseStateChanged();
		Settle();
		Assert.True(SpinWait.SpinUntil(() => store.Saves == saves + 1, 5000));
		Assert.Equal(70, store.Saved!.KnownControllers![0].LastBattery);
		Assert.Equal("Couch Pad", store.Saved!.KnownControllers![0].Name);

		vm.Settings.SaveNow();
		Assert.Equal(saves + 2, store.Saves);
		Assert.Equal(60, store.Saved!.KnownControllers![0].LastBattery);
		// Nothing left to write afterwards.
		vm.Settings.SaveNow();
		Assert.Equal(saves + 2, store.Saves);
	}

	[AvaloniaFact]
	public void Settings_ABatteryReading_IsSavedAtMostEveryFewMinutes_AndAtExit()
	{
		long now = 0;
		FakeControllerService fake = new(animate: false);
		MemorySettingsStore store = new(new AppSettings());
		MainWindowViewModel vm = new(fake, new AppEnvironment(store, Clock: () => now));
		fake.ControllerList.Add(Entry(70));
		fake.RaiseStateChanged();
		Settle();
		vm.Settings.SaveNow();
		int saves = store.Saves;
		int rowsChanged = 0;
		vm.Settings.KnownControllersChanged += () => rowsChanged++;

		// A new battery percent a minute later: shown at once, not written.
		now += 60_000;
		fake.ControllerList[0] = Entry(60);
		fake.RaiseStateChanged();
		Settle();
		Assert.Equal(1, rowsChanged);
		Thread.Sleep(SettingsState.SaveDelay * 2);
		Assert.Equal(saves, store.Saves);
		Assert.Equal(70, store.Saved!.KnownControllers![0].LastBattery);

		// Five minutes after the last write: written again (with the debounce).
		now += (long)SettingsState.BatterySaveInterval.TotalMilliseconds;
		fake.ControllerList[0] = Entry(50);
		fake.RaiseStateChanged();
		Settle();
		Assert.True(SpinWait.SpinUntil(() => store.Saves == saves + 1, 5000));
		Assert.Equal(50, store.Saved!.KnownControllers![0].LastBattery);

		// Another reading soon after, then the app exits: the exit save carries it.
		now += 10_000;
		fake.ControllerList[0] = Entry(40);
		fake.RaiseStateChanged();
		Settle();
		vm.Settings.SaveNow();
		Assert.Equal(saves + 2, store.Saves);
		Assert.Equal(40, store.Saved!.KnownControllers![0].LastBattery);
	}
}
