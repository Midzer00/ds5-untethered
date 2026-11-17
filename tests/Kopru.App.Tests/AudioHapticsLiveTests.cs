// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Audio;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;

namespace Kopru.App.Tests;

// "Turn sound into haptics" on the real service: every way the app makes the service captures with WASAPI (not the
// "no sound capture" stand-in), and the switch, strength and band reach the running game at once.
public sealed class AudioHapticsLiveServiceTests
{
	private sealed class Capture(AudioCaptureMode mode) : IAudioCapture
	{
		public volatile bool Disposed;
		public AudioCaptureMode Mode => mode;
		public void Dispose() => Disposed = true;
	}

	private sealed class CaptureFactory : IAudioCaptureFactory
	{
		private int starts;
		public volatile Capture? Last;
		public int Starts => Volatile.Read(ref starts);

		public IAudioCapture? Start(int processId, IAudioCaptureSink sink, Action<string> log)
		{
			Interlocked.Increment(ref starts);
			return Last = new Capture(AudioCaptureMode.ProcessLoopback);
		}
	}

	private static void WaitUntil(Func<bool> condition, string what) => Assert.True(SpinWait.SpinUntil(condition, 5000), what);

	// The app's own construction (App.StartDesktop) once gave no capture at all: the runner then logged "no sound capture
	// in this build" for every game. Made without one, the service now captures with WASAPI.
	[Fact]
	public void MadeWithoutACaptureFactory_TheServiceCapturesWithWasapi()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}
		var controllers = new TestControllers();
		using var service = new RealControllerService(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher()), new TestHiding(),
			controllers, a => a(), _ => null, timers: false);
		Assert.IsType<WasapiCaptureFactory>(service.AudioCapture);
	}

	[Fact]
	public void TheGivenCaptureFactory_IsTheOneUsed()
	{
		var controllers = new TestControllers();
		var factory = new CaptureFactory();
		using var service = new RealControllerService(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher()), new TestHiding(),
			controllers, a => a(), _ => null, timers: false, audioCapture: factory);
		Assert.Same(factory, service.AudioCapture);
	}

	// While the game runs: on starts the capture, a new strength or band keeps the same capture, off stops it, on again
	// starts it again; no game restart needed.
	[Fact]
	public void WhileTheGameRuns_OnOffStrengthAndBand_ApplyAtOnce()
	{
		var controllers = new TestControllers();
		var factory = new CaptureFactory();
		var watcher = new FakeGameWatcher("Cyberpunk2077.exe");
		using var service = new RealControllerService(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher()), new TestHiding(),
			controllers, a => a(), _ => null, timers: false, games: new GameModeController(FakeControllerService.SampleCatalog, watcher, null),
			audioCapture: factory);
		service.Games.Tick();
		Assert.NotNull(service.Games.ActiveGame);

		ControllerSettings on = new() { AudioHapticsOn = true };
		service.ApplySettings(on);
		WaitUntil(() => factory.Starts == 1 && service.AudioHapticsStatus.Kind == AudioHapticsStatusKind.Listening, "started");
		Capture first = factory.Last!;

		service.ApplySettings(on with { AudioHapticsStrength = 160, AudioHapticsBand = AudioHapticsBand.Full });
		service.ApplySettings(on with { AudioHapticsStrength = 40, AudioHapticsBand = AudioHapticsBand.Bass });
		Thread.Sleep(100);
		Assert.Equal(1, factory.Starts);
		Assert.False(first.Disposed);

		service.ApplySettings(on with { AudioHapticsOn = false });
		WaitUntil(() => first.Disposed && service.AudioHapticsStatus == AudioHapticsStatus.Off, "stopped");

		service.ApplySettings(on);
		WaitUntil(() => factory.Starts == 2 && service.AudioHapticsStatus.Kind == AudioHapticsStatusKind.Listening, "started again");
		Assert.False(factory.Last!.Disposed);
	}

	// Quick toggles end in the state the user left (an older reading of the switch never lands after a newer one).
	[Fact]
	public void QuickToggles_EndOff_WhenLeftOff()
	{
		var controllers = new TestControllers();
		var factory = new CaptureFactory();
		using var service = new RealControllerService(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher()), new TestHiding(),
			controllers, a => a(), _ => null, timers: false,
			games: new GameModeController(FakeControllerService.SampleCatalog, new FakeGameWatcher("Cyberpunk2077.exe"), null), audioCapture: factory);
		service.Games.Tick();
		ControllerSettings on = new() { AudioHapticsOn = true };
		for (int i = 0; i < 20; i++)
		{
			service.ApplySettings(on);
			service.ApplySettings(on with { AudioHapticsOn = false });
		}
		WaitUntil(() => service.AudioHapticsStatus == AudioHapticsStatus.Off && (factory.Last == null || factory.Last.Disposed), "ends off");
		Thread.Sleep(100);
		Assert.Equal(AudioHapticsStatus.Off, service.AudioHapticsStatus);
		Assert.True(factory.Last == null || factory.Last.Disposed);
	}
}

// The game pop-up while the game runs: its switch is never greyed out (it can always be turned off), the note says the
// game's own rumble is a separate setting, and a profile picked for the running game becomes the active one so the
// pop-up's changes reach the game at once.
public sealed class AudioHapticsLivePanelTests
{
	[AvaloniaFact]
	public void TheSwitch_StaysUsable_WhenTheCaptureFailed()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		fake.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == "Cyberpunk 2077"));
		panel.UseOwnSettingsCommand.Execute(null);
		panel.AudioHaptics.On = true;
		fake.AudioHapticsStatus = new AudioHapticsStatus(AudioHapticsStatusKind.Unavailable, "Cyberpunk 2077");
		FitCheck.Settle(window);

		ToggleSwitch toggle = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "PanelAudioSwitch");
		Assert.True(toggle.IsEffectivelyEnabled);
		Assert.True(toggle.IsChecked);
		Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Text.AudioHapticsRumbleNote() && t.IsEffectivelyVisible);

		toggle.IsChecked = false;
		Assert.False(vm.Settings.AudioHapticsOn);
		Assert.False(fake.Settings.Game.AudioHaptics.On);
		Assert.False(vm.Settings.Profiles.Active.Values.AudioHaptics);
		window.Close();
	}

	[AvaloniaFact]
	public void AProfilePickedForTheRunningGame_BecomesActive_AndThePanelEditsItLive()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		fake.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		ControllerProfile racing = vm.Settings.Profiles.Create("Racing", vm.Settings.Profiles.Default.Values with { AudioHaptics = true })!;
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		GameItemViewModel game = vm.SupportedGames.Games.Single(g => g.Name == "Cyberpunk 2077");
		vm.SupportedGames.OpenGameCommand.Execute(game);
		Assert.NotEqual(racing.Id, vm.Settings.Profiles.ActiveId);

		game.Profile!.Selected = game.Profile.Options.Single(o => o.Id == racing.Id);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(racing.Id, vm.Settings.Profiles.ActiveId);
		Assert.True(fake.Settings.Game.AudioHaptics.On);

		// The pop-up now edits the running profile: off, strength and band reach the service at once.
		panel.AudioHaptics.On = false;
		Assert.False(fake.Settings.Game.AudioHaptics.On);
		panel.AudioHaptics.On = true;
		panel.AudioHaptics.Strength = 150;
		panel.AudioHaptics.IsFull = true;
		Assert.Equal(new AudioHapticsSettings(true, 1.5, AudioHapticsBand.Full), fake.Settings.Game.AudioHaptics);
		Assert.True(vm.Settings.Profiles.Find(racing.Id)!.Values.AudioHaptics);

		// A profile the user picks by hand while the game runs stays (a later profile change does not snap back).
		vm.Settings.ActivateProfile(ProfileBook.DefaultId);
		vm.Settings.RumbleLevel = 70;
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(ProfileBook.DefaultId, vm.Settings.Profiles.ActiveId);
		Assert.False(fake.Settings.Game.AudioHaptics.On);
	}

	[Fact]
	public void RumbleNote_InBothLanguages()
	{
		Assert.Equal("Oyunun kendi titreşimi ayrıca gelir; onu Xbox ve DS4 Titreşimi'nden ayarla.", Text.AudioHapticsRumbleNote(Language.Turkish));
		Assert.Equal("The game's own rumble still comes through; set it in Xbox And DS4 Rumble.", Text.AudioHapticsRumbleNote(Language.English));
		Assert.Contains(Text.ClassicRumbleCard(Language.Turkish), Text.AudioHapticsRumbleNote(Language.Turkish));
		Assert.Contains(Text.ClassicRumbleCard(Language.English), Text.AudioHapticsRumbleNote(Language.English));
	}
}
