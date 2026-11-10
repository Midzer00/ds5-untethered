// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

// DualSense Mode turns the controller speaker on with it (one push per switch, never a bridge restart), Low Power still
// wins, a speaker turned off by hand holds until the mode is switched again, and the user's own switch comes back.
public class DualSenseModeAppTests
{
	private static (MainWindowViewModel Vm, FakeControllerService Fake) Create(ISettingsStore? store = null)
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(store));
		return (vm, fake);
	}

	private static void Settle() => Dispatcher.UIThread.RunJobs();

	[AvaloniaFact]
	public async Task TheModeSwitch_TurnsTheSpeakerOnAndOff_OnePushEach_NoRestart()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		await fake.StartBridgeAsync();
		Settle();
		Assert.False(vm.Overview.IsSupportedModeOn);
		Assert.False(fake.Settings.Speaker.Enabled);
		int applies = fake.Applies;

		vm.Overview.IsSupportedModeOn = true;
		Settle();
		Assert.Equal(applies + 1, fake.Applies);
		Assert.True(fake.Settings.Speaker.Enabled);
		Assert.True(vm.Overview.Quick.SpeakerOn);
		Assert.True(vm.Settings.SpeakerActive);
		// The user's own switch is not touched.
		Assert.False(vm.Settings.SpeakerEnabled);

		vm.Overview.IsSupportedModeOn = false;
		Settle();
		Assert.Equal(applies + 2, fake.Applies);
		Assert.False(fake.Settings.Speaker.Enabled);
		Assert.False(vm.Overview.Quick.SpeakerOn);
		Assert.Equal(1, fake.Starts);
		Assert.Equal(0, fake.Stops);
	}

	[AvaloniaFact]
	public void SpeakerOffByHand_HoldsUntilTheModeIsSwitched_AndTheUsersSwitchComesBack()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		QuickSettingsViewModel q = vm.Overview.Quick;
		q.SpeakerOn = true;
		Settle();
		Assert.True(vm.Settings.SpeakerEnabled);
		int applies = fake.Applies;

		// Already on: the mode changes nothing the controller gets.
		vm.Overview.IsSupportedModeOn = true;
		Settle();
		Assert.Equal(applies, fake.Applies);

		q.SpeakerOn = false;
		Settle();
		Assert.Equal(applies + 1, fake.Applies);
		Assert.False(fake.Settings.Speaker.Enabled);
		Assert.False(q.SpeakerOn);
		// Only this session's choice: the user's own switch stays on.
		Assert.True(vm.Settings.SpeakerEnabled);

		// Same value again: nothing.
		q.SpeakerOn = false;
		Settle();
		Assert.Equal(applies + 1, fake.Applies);

		// Mode off: the user's own switch (on) again.
		vm.Overview.IsSupportedModeOn = false;
		Settle();
		Assert.Equal(applies + 2, fake.Applies);
		Assert.True(fake.Settings.Speaker.Enabled);

		// Mode on again: the whole set is on, the earlier choice is gone (and the speaker was on already: no push).
		vm.Overview.IsSupportedModeOn = true;
		Settle();
		Assert.Equal(applies + 2, fake.Applies);
		Assert.True(fake.Settings.Speaker.Enabled);
		Assert.True(q.SpeakerOn);
	}

	[AvaloniaFact]
	public void SpeakerOnByHand_InTheMode_ClearsTheChoiceWithOnePush()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		vm.Overview.IsSupportedModeOn = true;
		vm.Overview.Quick.SpeakerOn = false;
		Settle();
		int applies = fake.Applies;
		vm.Overview.Quick.SpeakerOn = true;
		Settle();
		Assert.Equal(applies + 1, fake.Applies);
		Assert.True(fake.Settings.Speaker.Enabled);
		Assert.False(vm.Settings.SpeakerEnabled);
	}

	[AvaloniaFact]
	public void LowPower_StillWins()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		vm.Overview.IsSupportedModeOn = true;
		Settle();
		Assert.True(fake.Settings.Speaker.Enabled);
		int applies = fake.Applies;

		vm.Overview.Quick.LowPowerOn = true;
		Settle();
		Assert.Equal(applies + 1, fake.Applies);
		Assert.False(fake.Settings.Speaker.Enabled);
		Assert.False(fake.Settings.LightFromGame);

		// While Low Power holds the speaker off, switching the mode or the tile sends nothing.
		vm.Overview.IsSupportedModeOn = false;
		vm.Overview.IsSupportedModeOn = true;
		vm.Overview.Quick.SpeakerOn = false;
		vm.Overview.Quick.SpeakerOn = true;
		Settle();
		Assert.Equal(applies + 1, fake.Applies);

		vm.Overview.Quick.LowPowerOn = false;
		Settle();
		Assert.Equal(applies + 2, fake.Applies);
		Assert.True(fake.Settings.Speaker.Enabled);
	}

	[AvaloniaFact]
	public void AModeSwitchedByAGame_TakesTheSpeakerAlong()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		fake.Games.SetAlways("Returnal", true);
		fake.Watcher.Exe = "Returnal-Win64-Shipping.exe";
		fake.Games.Tick();
		Settle();
		Assert.True(fake.Games.IsSupportedModeOn);
		Assert.True(fake.Settings.Speaker.Enabled);
		Assert.True(vm.Overview.Quick.SpeakerOn);

		fake.Watcher.Exit("Returnal-Win64-Shipping.exe");
		fake.Games.Tick();
		Settle();
		Assert.False(fake.Games.IsSupportedModeOn);
		Assert.False(fake.Settings.Speaker.Enabled);
	}

	[AvaloniaFact]
	public void AFreshInstall_StartsWithTheModeAndTheAutomaticStartOn()
	{
		var store = new MemorySettingsStore();
		(MainWindowViewModel vm, FakeControllerService fake) = Create(store);
		Assert.True(vm.Settings.IsFirstRun);
		Assert.True(fake.Games.IsSupportedModeOn);
		Assert.True(vm.Overview.IsSupportedModeOn);
		Assert.True(vm.Settings.AutoStartBridge);
		Assert.True(fake.Settings.Speaker.Enabled);
		vm.Settings.KeepInTray = false;
		vm.Settings.SaveNow();
		Assert.True(store.Saved!.SupportedModeOn);
		Assert.True(store.Saved.AutoStartBridge);
	}

	[AvaloniaFact]
	public void AnOldFile_WithoutTheKeys_TakesTheNewDefaults()
	{
		string path = TempFile("{ \"GameHapticsLevel\": 120, \"SpeakerEnabled\": false }");
		try
		{
			(MainWindowViewModel vm, FakeControllerService fake) = Create(new JsonSettingsStore(path));
			Assert.False(vm.Settings.IsFirstRun);
			Assert.Equal(120, vm.Settings.GameHapticsLevel);
			Assert.True(fake.Games.IsSupportedModeOn);
			Assert.True(vm.Settings.AutoStartBridge);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[AvaloniaFact]
	public void ExplicitSavedChoices_AreKept()
	{
		string path = TempFile("{ \"SupportedModeOn\": false, \"AutoStartBridge\": false }");
		try
		{
			var store = new JsonSettingsStore(path);
			(MainWindowViewModel vm, FakeControllerService fake) = Create(store);
			Assert.False(fake.Games.IsSupportedModeOn);
			Assert.False(vm.Settings.AutoStartBridge);
			vm.AttachAutoStart();
			Settle();
			Assert.Equal(0, fake.Starts);
			vm.Settings.KeepInTray = false;
			vm.Settings.SaveNow();
			AppSettings saved = store.Load()!;
			Assert.False(saved.SupportedModeOn);
			Assert.False(saved.AutoStartBridge);
		}
		finally
		{
			File.Delete(path);
		}
	}

	private static string TempFile(string json)
	{
		string path = Path.Combine(Path.GetTempPath(), "untethered-ds-" + Guid.NewGuid().ToString("N") + ".json");
		File.WriteAllText(path, json);
		return path;
	}
}

// "Start The Bridge Automatically": once at app start with a controller and nothing blocking, not again after a stop by
// hand, again after the controller reconnects.
public class BridgeAutoStartAppTests
{
	private static void Settle() => Dispatcher.UIThread.RunJobs();

	private static Kopru.Core.Health.HealthReport Report(bool driver) => Kopru.Core.Health.HealthReport.Build(
		new Kopru.Core.Health.HealthEnvironment(driver ? new ComponentInfo(true, "1.0", null, true) : ComponentInfo.Missing,
			new ComponentInfo(true, "1.6", null, true), RadioState.On, [], null, null),
		new Kopru.Core.Health.HealthRuntime([new Kopru.Core.Hid.HidDevice(@"\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_fake", 0x054C, 0x0CE6, 78, 547, 64)],
			false, Kopru.Core.Bridge.BridgeState.Stopped, Kopru.Core.Bridge.BridgeError.None, null), DateTimeOffset.Now);

	[AvaloniaFact]
	public async Task StartsOnce_NotAfterAStopByHand_AgainAfterAReconnect()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(null));
		Settle();
		// Not attached (tests, or before the app turns it on): nothing starts.
		Assert.Equal(0, fake.Starts);

		vm.AttachAutoStart();
		Settle();
		Assert.Equal(1, fake.Starts);
		Assert.True(fake.IsBridgeRunning);
		fake.RaiseStateChanged();
		Settle();
		Assert.Equal(1, fake.Starts);

		await fake.StopBridgeAsync();
		fake.RaiseStateChanged();
		Settle();
		Assert.Equal(1, fake.Starts);

		fake.IsConnected = false;
		fake.RaiseStateChanged();
		fake.IsConnected = true;
		fake.RaiseStateChanged();
		Settle();
		Assert.Equal(2, fake.Starts);
	}

	[AvaloniaFact]
	public void WaitsUntilNothingBlocks_AndHonoursTheSetting()
	{
		var fake = new FakeControllerService(animate: false) { Health = Report(driver: false) };
		var vm = new MainWindowViewModel(fake, new AppEnvironment(null));
		vm.AttachAutoStart();
		Settle();
		Assert.Equal(0, fake.Starts);

		vm.Settings.AutoStartBridge = false;
		fake.Health = Report(driver: true);
		fake.RaiseStateChanged();
		Settle();
		Assert.Equal(0, fake.Starts);

		// Turned on again with the controller there: it starts.
		vm.Settings.AutoStartBridge = true;
		Settle();
		Assert.Equal(1, fake.Starts);
	}

	[AvaloniaFact]
	public void WaitsWhileTheSetupPageIsOpen()
	{
		var fake = new FakeControllerService(animate: false);
		var probes = new RealServiceTests.Probes(drivers: true);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(null, Wizard: () => new SetupWizard(new SystemCheck(probes, probes, probes, probes),
			new RealServiceTests.Links())));
		vm.ShowSetup();
		vm.AttachAutoStart();
		Settle();
		Assert.Equal(0, fake.Starts);
		vm.Setup = null;
		Settle();
		Assert.Equal(1, fake.Starts);
	}

	// Closing the setup page (its close button or "Go To Overview") lands on the Overview and starts the bridge once; a
	// second close request (the page already gone) changes nothing.
	[AvaloniaTheory]
	[InlineData(false)]
	[InlineData(true)]
	public void ClosingTheSetupPage_GoesToTheOverview_AndStartsTheBridgeOnce(bool viaFinish)
	{
		var fake = new FakeControllerService(animate: false);
		var probes = new RealServiceTests.Probes(drivers: true);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(null, Wizard: () => new SetupWizard(new SystemCheck(probes, probes, probes, probes),
			new RealServiceTests.Links())));
		vm.OpenController(ControllerPageViewModel.TestTab);
		vm.ShowSetup();
		SetupViewModel setup = vm.Setup!;
		vm.AttachAutoStart();
		Settle();
		Assert.Equal(0, fake.Starts);
		Assert.Same(setup, vm.CurrentPage);

		(viaFinish ? setup.GoToOverviewCommand : setup.CloseCommand).Execute(null);
		Settle();
		Assert.Null(vm.Setup);
		Assert.Same(vm.Overview, vm.CurrentPage);
		Assert.Equal(1, fake.Starts);

		// Stopped by hand, then a stale close from the old page: the bridge is not started again.
		fake.StopBridgeAsync().GetAwaiter().GetResult();
		fake.RaiseStateChanged();
		Settle();
		vm.CloseSetup();
		Settle();
		Assert.Equal(1, fake.Starts);
		Assert.Same(vm.Overview, vm.CurrentPage);
	}
}
