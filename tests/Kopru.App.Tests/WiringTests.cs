// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Input;
using Kopru.Core.Localization;
using Kopru.Core.Steam;

namespace Kopru.App.Tests;

// Tuning, power, latency, diagnostics export, HidHide early registration and the Steam Input check in the app.
public class WiringTests
{
	private const string BtId = @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\test";

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private static (MainWindowViewModel Vm, FakeControllerService Fake) NewViewModel(ControllerKind kind = ControllerKind.DualSense, ISettingsStore? store = null)
	{
		var fake = new FakeControllerService(kind, animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(store));
		vm.Sticks.StopPreview();
		return (vm, fake);
	}

	private static void WithLanguage(Language language, Action action)
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = language;
			action();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// Sticks & triggers

	[AvaloniaFact]
	public void StickAndTriggerSliders_ChangeTheTuning_AndReachTheService()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		SticksViewModel page = vm.Sticks;
		Assert.True(fake.Settings.Tuning.IsIdentity);

		page.LeftStick.Inner = 12;
		page.LeftStick.Outer = 5;
		page.RightStick.Anti = 8;
		page.RightStick.SelectedCurve = page.RightStick.Curves.Single(c => c.Curve == ResponseCurve.Smooth);
		page.L2.Start = 10;
		page.R2.End = 80;

		InputTuning t = fake.Settings.Tuning;
		Assert.Equal(12, t.LeftStick.InnerDeadzone);
		Assert.Equal(5, t.LeftStick.OuterDeadzone);
		Assert.Equal(8, t.RightStick.AntiDeadzone);
		Assert.Equal(ResponseCurve.Smooth, t.RightStick.Curve);
		Assert.Equal(10, t.L2.Start);
		Assert.Equal(80, t.R2.End);
		Assert.Equal(Text.PercentValue(12), page.LeftStick.InnerText);

		// The end stays above the start.
		page.R2.Start = 90;
		Assert.Equal(75, fake.Settings.Tuning.R2.Start);

		page.ResetCommand.Execute(null);
		Assert.True(fake.Settings.Tuning.IsIdentity);
		Assert.Equal(0, page.LeftStick.Inner);
	}

	[AvaloniaFact]
	public void LivePreview_ShowsRawAndTunedPositions()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		SticksViewModel page = vm.Sticks;
		fake.RawInput = null;
		page.RefreshInput();
		Assert.False(page.HasLiveInput);
		Assert.Equal(Text.LivePreviewNeedsBridge(), page.PreviewHint);

		fake.RawInput = [140, 128, 128, 128, 100, 255, 0, 0, 0];
		page.RefreshInput();
		Assert.True(page.HasLiveInput);
		Assert.True(page.LeftStick.IsLive);
		Assert.Equal(Text.StickPreviewHint(), page.PreviewHint);
		Assert.True(page.LeftStick.RawX > 0.05);
		Assert.Equal(page.LeftStick.RawX, page.LeftStick.TunedX, 3);
		Assert.Equal(100 / 255.0 * 100, page.L2.RawLevel, 3);

		// A big inner deadzone keeps the slightly moved stick at the center; a trigger start above the press reads 0.
		page.LeftStick.Inner = 30;
		page.L2.Start = 50;
		Assert.True(page.LeftStick.RawX > 0.05);
		Assert.Equal(0, page.LeftStick.TunedX, 2);
		Assert.Equal(0, page.L2.TunedLevel);
	}

	[AvaloniaFact]
	public async Task DriftCalibration_AppliesOffsetsAndDeadzone_OrReportsWhyNot()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		SticksViewModel page = vm.Sticks;

		await page.CalibrateCommand.ExecuteAsync(null);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(1, fake.Calibrations);
		Assert.Equal(3, fake.Settings.Tuning.LeftStick.DriftX);
		Assert.Equal(-2, fake.Settings.Tuning.LeftStick.DriftY);
		Assert.Equal(6, fake.Settings.Tuning.LeftStick.InnerDeadzone);
		Assert.Equal(4, fake.Settings.Tuning.RightStick.InnerDeadzone);
		Assert.Equal(Text.CalibrationDone(), page.CalibrationText);
		Assert.Equal(100, page.CalibrationProgress);
		Assert.False(page.IsCalibrating);

		page.ResetCommand.Execute(null);
		fake.CalibrationResult = new DriftCalibrationResult(DriftCalibrationStatus.SticksMoved, 0, 0, 0, 0, 0, 0);
		await page.CalibrateCommand.ExecuteAsync(null);
		Assert.True(fake.Settings.Tuning.IsIdentity);
		Assert.Equal(Text.CalibrationSticksMoved(), page.CalibrationText);
	}

	[AvaloniaFact]
	public void TuningAndPowerSetting_RoundTripThroughTheStore()
	{
		var store = new MemorySettingsStore();
		(MainWindowViewModel vm, _) = NewViewModel(store: store);
		vm.Sticks.LeftStick.Inner = 9;
		vm.Sticks.R2.Start = 20;
		vm.Settings.PowerOffWhenIdle = true;
		vm.Settings.SaveNow();
		Assert.True(SpinWait.SpinUntil(() => store.Saved?.PowerOffWhenIdle == true, 3000));
		vm.Settings.SaveNow();

		string dir = Path.Combine(Path.GetTempPath(), "untethered-" + Guid.NewGuid().ToString("N"));
		try
		{
			var json = new JsonSettingsStore(Path.Combine(dir, "settings.json"));
			json.Save(store.Saved!);
			(MainWindowViewModel loaded, FakeControllerService fake) = NewViewModel(store: json);
			Assert.Equal(9, loaded.Settings.Tuning.LeftStick.InnerDeadzone);
			Assert.Equal(20, loaded.Settings.Tuning.R2.Start);
			Assert.True(loaded.Settings.PowerOffWhenIdle);
			Assert.True(fake.Settings.PowerOffWhenIdle);
			Assert.Equal(loaded.Settings.Tuning, fake.Settings.Tuning);

			// A file from before these settings existed loads with the defaults.
			File.WriteAllText(json.Path, "{ \"Brightness\": 40 }");
			(MainWindowViewModel old, _) = NewViewModel(store: json);
			Assert.Equal(40, old.Settings.Brightness);
			Assert.True(old.Settings.Tuning.IsIdentity);
			Assert.False(old.Settings.PowerOffWhenIdle);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	// Power, latency, diagnostics

	[AvaloniaFact]
	public async Task TurnOffController_CallsTheService_AndShowsTheResult()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		Assert.True(vm.Overview.CanPowerOff);
		await vm.Overview.PowerOffCommand.ExecuteAsync(null);
		Assert.Equal(1, fake.PowerOffs);
		Assert.Equal(Text.PowerOffDone(), vm.Overview.PowerOffResult);

		fake.PowerOffSucceeds = false;
		await vm.Overview.PowerOffCommand.ExecuteAsync(null);
		Assert.Equal(Text.PowerOffFailed(), vm.Overview.PowerOffResult);

		(MainWindowViewModel none, FakeControllerService noController) = NewViewModel(ControllerKind.None);
		Assert.False(none.Overview.CanPowerOff);
		await none.Overview.PowerOffCommand.ExecuteAsync(null);
		Assert.Equal(0, noController.PowerOffs);
	}

	[AvaloniaFact]
	public void Latency_ShowsTheMeasuredValues_OrADash()
	{
		WithLanguage(Language.English, () =>
		{
			(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
			Assert.Equal("1.2 / 3.4 ms", vm.Overview.LatencyValue);
			Assert.StartsWith(Text.LatencyValue(1.2, 3.4, 0.05), vm.Overview.LatencyTip);
			fake.Latency = null;
			Assert.Equal("—", vm.Overview.LatencyValue);
			Assert.Equal(Text.LatencyHint(), vm.Overview.LatencyTip);
		});
		WithLanguage(Language.Turkish, () =>
		{
			(MainWindowViewModel vm, _) = NewViewModel();
			Assert.Equal("1,2 / 3,4 ms", vm.Overview.LatencyValue);
		});
	}

	[AvaloniaFact]
	public async Task ExportDiagnostics_SavesWhereThePickerSays()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		SettingsViewModel settings = vm.SettingsPage;
		string? suggested = null;
		settings.PickSavePath = name =>
		{
			suggested = name;
			return Task.FromResult<string?>(@"C:\x\diag.zip");
		};
		await settings.ExportCommand.ExecuteAsync(null);
		Assert.Equal(SettingsViewModel.DiagnosticsFileName(DateTime.Now), suggested);
		Assert.Matches(@"^ds5-untethered-diagnostics-\d{4}-\d{2}-\d{2}\.zip$", suggested);
		Assert.Equal([@"C:\x\diag.zip"], fake.Exports);
		Assert.Equal(Text.DiagnosticsSaved(@"C:\x\diag.zip"), settings.ToolResult);

		fake.ExportFailure = new IOException("disk full");
		await settings.ExportCommand.ExecuteAsync(null);
		Assert.Equal(Text.DiagnosticsFailed("disk full"), settings.ToolResult);

		// Cancelled: nothing is written.
		settings.PickSavePath = _ => Task.FromResult<string?>(null);
		await settings.ExportCommand.ExecuteAsync(null);
		Assert.Equal(2, fake.Exports.Count);
	}

	[Fact]
	public void AdapterResult_IsWrittenForTheExport()
	{
		string dir = Path.Combine(Path.GetTempPath(), "untethered-" + Guid.NewGuid().ToString("N"));
		try
		{
			string path = Path.Combine(dir, DiagnosticsExport.AdapterResultFileName);
			RealControllerService.WriteAdapterResult(path, null, new AdapterTestResult(AdapterVerdict.Suitable, "fine"));
			string text = File.ReadAllText(path);
			Assert.Contains("Suitable", text);
			Assert.Contains("fine", text);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	// HidHide early registration

	[Fact]
	public async Task HidingLifecycle_RegistersAfterRecovery_AndReleasesAfterTheSession()
	{
		TestHiding hiding = new();
		await HidingLifecycle.StartAsync(hiding);
		Assert.Equal(["recover", "register early"], hiding.Calls);

		var controllers = new TestControllers();
		using RealControllerService service = RealService(controllers, hiding);
		controllers.Links.Enqueue(new TestLink());
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected);
		await service.ShutdownAsync();
		Assert.Equal(["recover", "register early", "begin", "hide " + BtId, "restore", "release early"], hiding.Calls);

		TestHiding missing = new(installed: false);
		await HidingLifecycle.StartAsync(missing);
		await HidingLifecycle.StopAsync(missing);
		Assert.Empty(missing.Calls);
	}

	// Real service on fakes: tuning, raw input, calibration, Steam check

	private static RealControllerService RealService(TestControllers controllers, TestHiding hiding, Func<string?, SteamInputResult>? steam = null,
		Kopru.Core.Games.GameModeController? games = null) =>
		new(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan
		}, hiding, controllers, a => a(), _ => BtId, timers: false, games: games, steamCheck: steam);

	private static BridgeHost HostOf(RealControllerService service) =>
		(BridgeHost)typeof(RealControllerService).GetField("host", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service)!;

	[Fact]
	public async Task Tuning_IsAppliedToTheBridge_AndRawInputAndCalibrationWork()
	{
		var controllers = new TestControllers();
		using RealControllerService service = RealService(controllers, new TestHiding());
		BridgeHost host = HostOf(service);
		InputTuning tuning = InputTuning.Default with { LeftStick = new StickTuning { InnerDeadzone = 10 } };
		service.ApplySettings(new ControllerSettings { Tuning = tuning, PowerOffWhenIdle = true });
		Assert.Equal(tuning, host.InputTransform.Tuning);
		Assert.True(host.PowerOffWhenIdle);

		Assert.Null(service.RawInput);
		Assert.Null(service.Latency);
		Assert.False(await service.PowerOffAsync()); // nothing running

		TestLink link = new();
		controllers.Links.Enqueue(link);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected);
		link.Feed(0x17);
		WaitUntil(() => service.RawInput != null);

		// The tuning set while running reaches the running connection too.
		InputTuning other = InputTuning.Default with { R2 = new TriggerTuning { Start = 20 } };
		service.ApplySettings(new ControllerSettings { Tuning = other });
		Assert.Equal(other, host.InputTransform.Tuning);

		using CancellationTokenSource stop = new();
		Task feeder = Task.Run(async () =>
		{
			while (!stop.IsCancellationRequested)
			{
				for (int i = 0; i < 8; i++)
				{
					link.Feed(0x17);
				}
				await Task.Delay(5);
			}
		});
		List<double> progress = [];
		DriftCalibrationResult result = await service.CalibrateDriftAsync(TimeSpan.FromMilliseconds(600), new SyncProgress(progress));
		stop.Cancel();
		await feeder;
		Assert.Equal(DriftCalibrationStatus.Success, result.Status);
		Assert.Equal(-127.5, result.LeftX); // the test reports have every stick at 0
		Assert.Equal(1, progress[^1]);

		await service.ShutdownAsync();
	}

	[Fact]
	public void SteamCheck_RunsThrottled_UsesTheGamesAppId_AndIsKeptOnlyWhenRelevant()
	{
		var controllers = new TestControllers();
		var watcher = new FakeGameWatcher("Cyberpunk2077.exe");
		var games = new Kopru.Core.Games.GameModeController(FakeControllerService.SampleCatalog, watcher, null);
		List<string?> calls = [];
		SteamInputStatus status = SteamInputStatus.OnForApp;
		bool running = false;
		SteamInputResult Check(string? appId)
		{
			calls.Add(appId);
			return new SteamInputResult(status, running, Text.SteamInputAdvice(status));
		}
		using RealControllerService service = RealService(controllers, new TestHiding(), Check, games);

		service.GameTick();
		Assert.Equal(["1091500"], calls);
		Assert.Equal(SteamInputStatus.OnForApp, service.SteamInput?.Status);

		service.GameTick();
		Assert.Single(calls); // not again within the interval

		// Another game: checked at once. No Steam game and Steam not running: nothing to show.
		watcher.Exe = "notepad.exe";
		service.GameTick();
		Assert.Equal(2, calls.Count);
		Assert.Null(calls[^1]);
		Assert.Null(service.SteamInput);

		running = true;
		status = SteamInputStatus.OnGlobally;
		service.CheckSteamInput(force: true);
		Assert.Equal(SteamInputStatus.OnGlobally, service.SteamInput?.Status);
	}

	[AvaloniaFact]
	public void SteamWarning_VisibilityFollowsTheStatus()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		foreach (SteamInputStatus? status in new SteamInputStatus?[] { null, SteamInputStatus.NotInstalled, SteamInputStatus.Off,
			SteamInputStatus.OnGlobally, SteamInputStatus.OnForApp, SteamInputStatus.Unknown })
		{
			fake.SteamInput = status is SteamInputStatus s ? new SteamInputResult(s, true, Text.SteamInputAdvice(s)) : null;
			fake.Tick();
			Dispatcher.UIThread.RunJobs();
			bool warn = status is SteamInputStatus.OnGlobally or SteamInputStatus.OnForApp;
			Assert.Equal(warn, vm.Overview.HasSteamWarning);
			Assert.Equal(warn, vm.SupportedGames.HasSteamWarning);
			Assert.Equal(status == SteamInputStatus.Unknown, vm.SupportedGames.HasSteamNote);
			Assert.Equal(warn ? fake.SteamInput!.Advice : string.Empty, vm.Overview.SteamWarning);
			Assert.Equal(warn || status == SteamInputStatus.Unknown ? fake.SteamInput!.Advice : string.Empty, vm.SupportedGames.SteamText);
		}
	}

	private sealed class SyncProgress(List<double> values) : IProgress<double>
	{
		public void Report(double value)
		{
			lock (values)
			{
				values.Add(value);
			}
		}
	}
}
