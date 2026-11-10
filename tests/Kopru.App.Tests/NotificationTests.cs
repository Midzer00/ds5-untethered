// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;
using Kopru.Core.Steam;

namespace Kopru.App.Tests;

// Notifications (event mapping, thresholds, switches, rate limits), the tray menu, close-to-tray and the toast rendering.
public class NotificationTests
{
	private sealed class Harness
	{
		public long Now = 1_000_000;
		public bool FullScreen;
		public FakeControllerService Fake { get; }
		public MainWindowViewModel Vm { get; }
		public List<AppNotification> Raised { get; } = [];
		public SettingsState Settings => Vm.Settings;

		public Harness(bool connected = true, ISettingsStore? store = null)
		{
			Fake = new FakeControllerService(animate: false) { IsConnected = connected };
			Vm = new MainWindowViewModel(Fake, new AppEnvironment(store, FullScreen: () => FullScreen, Clock: () => Now));
			Vm.Sticks.StopPreview();
			Vm.Notifications.Raised += Raised.Add;
		}

		public void Change(Action<FakeControllerService> change)
		{
			change(Fake);
			Fake.RaiseStateChanged();
		}

		// Moves the clock past every cooldown.
		public void Later() => Now += (long)NotificationCenter.RepeatCooldown.TotalMilliseconds + 1000;

		public List<NotificationType> Types => Raised.Select(n => n.Type).ToList();
	}

	private sealed class FakeShell : IAppShell
	{
		public List<AppPage?> Shown { get; } = [];
		public int Exits { get; private set; }

		public void ShowMainWindow(AppPage? page = null) => Shown.Add(page);

		public void Exit() => Exits++;
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

	// Event mapping

	[AvaloniaFact]
	public void Connection_ChangesBecomeNotifications_ButNotTheStartState()
	{
		var h = new Harness(connected: false);
		h.Fake.RaiseStateChanged();
		Assert.Empty(h.Raised);
		WithLanguage(Language.Turkish, () => h.Change(f => f.IsConnected = true));
		AppNotification connected = Assert.Single(h.Raised);
		Assert.Equal(NotificationType.Connected, connected.Type);
		Assert.Equal(NotificationKind.Success, connected.Kind);
		Assert.Equal("Kumanda Bağlandı", connected.Title);
		Assert.Equal("DualSense hazır", connected.Body);
		Assert.Equal(78, connected.Value);
		h.Change(f => f.IsConnected = false);
		Assert.Equal([NotificationType.Connected, NotificationType.Disconnected], h.Types);
		Assert.Equal(AppPage.Overview, h.Raised[1].Page);
	}

	[AvaloniaFact]
	public void Battery_WarnsAt20And10_OncePerDischarge()
	{
		var h = new Harness();
		foreach (double p in new double[] { 30, 21, 20, 19, 15, 11 })
		{
			h.Later();
			h.Change(f => f.BatteryPercent = p);
		}
		Assert.Equal([NotificationType.BatteryLow], h.Types);
		Assert.Equal(NotificationKind.Warning, h.Raised[0].Kind);
		Assert.Equal(20, h.Raised[0].Value);
		foreach (double p in new double[] { 10, 9, 5 })
		{
			h.Later();
			h.Change(f => f.BatteryPercent = p);
		}
		Assert.Equal([NotificationType.BatteryLow, NotificationType.BatteryCritical], h.Types);
		Assert.Equal(NotificationKind.Error, h.Raised[1].Kind);

		// Charging starts a new discharge: the thresholds count again.
		h.Later();
		h.Change(f => f.IsCharging = true);
		h.Later();
		h.Change(f => { f.IsCharging = false; f.BatteryPercent = 18; });
		Assert.Equal(NotificationType.BatteryLow, h.Raised[^1].Type);
		Assert.Equal(3, h.Raised.Count);
	}

	[AvaloniaFact]
	public void Battery_JumpingStraightBelow10_GivesOnlyTheCriticalWarning()
	{
		var h = new Harness();
		h.Change(f => f.BatteryPercent = 8);
		h.Later();
		h.Change(f => f.BatteryPercent = 7);
		Assert.Equal([NotificationType.BatteryCritical], h.Types);
	}

	[AvaloniaFact]
	public void Charging_Complete_IsAnnouncedOnce()
	{
		var h = new Harness();
		h.Change(f => { f.IsCharging = true; f.BatteryPercent = 90; });
		Assert.Empty(h.Raised);
		h.Change(f => f.BatteryPercent = 100);
		h.Later();
		h.Change(f => f.IsCharging = false);
		h.Later();
		h.Change(f => f.BatteryPercent = 100);
		AppNotification charged = Assert.Single(h.Raised);
		Assert.Equal(NotificationType.Charged, charged.Type);
		Assert.Equal(NotificationKind.Success, charged.Kind);
	}

	private static void Focus(Harness h, string? exe)
	{
		h.Later();
		h.Fake.Watcher.Exe = exe;
		h.Fake.Games.Tick();
		h.Fake.RaiseStateChanged();
	}

	[AvaloniaFact]
	public void Game_KnownGameWithModeOff_AsksOncePerLaunch_WithButtons()
	{
		var h = new Harness();
		// Cyberpunk was already in front when the app started.
		h.Fake.RaiseStateChanged();
		Assert.Empty(h.Raised);
		WithLanguage(Language.Turkish, () => Focus(h, "Spider-Man.exe"));
		AppNotification ask = Assert.Single(h.Raised);
		Assert.Equal(NotificationType.GameAsk, ask.Type);
		Assert.Equal("Marvel's Spider-Man Remastered", ask.Title);
		Assert.Equal("DualSense modu açılsın mı?", ask.Body);
		Assert.Equal(["Şimdi Değil", "Her Zaman", "Aç"], ask.Actions.Select(a => a.Label));
		Assert.True(ask.Actions[2].IsPrimary);
		Assert.Equal(AppNotification.ActionDuration, ask.Duration);

		// Away (to nothing, to unknown apps) and back: the same launch, no repeat.
		Focus(h, null);
		Focus(h, "claude.exe");
		Focus(h, "chrome.exe");
		Focus(h, "Tetris.exe");
		Focus(h, "Spider-Man.exe");
		Assert.Single(h.Raised);

		// The game was closed and started again: asked again.
		h.Fake.Watcher.Exit("Spider-Man.exe");
		Focus(h, null);
		Focus(h, "Spider-Man.exe");
		Assert.Equal(2, h.Raised.Count(n => n.Type == NotificationType.GameAsk));
	}

	[AvaloniaFact]
	public void Game_AskShowsEvenWhileQuiet_ButNotWhenAskIsOff()
	{
		var h = new Harness();
		h.Settings.QuietWhilePlaying = true;
		h.FullScreen = true;
		Focus(h, "Returnal-Win64-Shipping.exe");
		Assert.Equal([NotificationType.GameAsk], h.Types);

		h.Settings.AskOnKnownGame = false;
		Assert.False(h.Fake.Games.AskOnKnownGame);
		Focus(h, "Spider-Man.exe");
		Assert.Single(h.Raised);
	}

	[AvaloniaFact]
	public void Game_AskButtons_TurnOn_NotNow_Always()
	{
		var h = new Harness();
		Focus(h, "Spider-Man.exe");
		h.Raised[^1].Actions.Single(a => a.Label == Text.AskTurnOn()).Run();
		Assert.True(h.Fake.Games.IsSupportedModeOn);
		Assert.True(h.Vm.Overview.IsSupportedModeOn);
		Assert.Equal(Text.ModeShortSupported(), h.Vm.Overview.ModeValue);
		// The game exits: back off.
		h.Fake.Watcher.Exit("Spider-Man.exe");
		Focus(h, null);
		Assert.False(h.Fake.Games.IsSupportedModeOn);

		Focus(h, "Returnal-Win64-Shipping.exe");
		h.Raised[^1].Actions.Single(a => a.Label == Text.AskNotNow()).Run();
		Assert.False(h.Fake.Games.IsSupportedModeOn);

		Focus(h, "AC2-Win64-Shipping.exe");
		h.Raised[^1].Actions.Single(a => a.Label == Text.AskAlways()).Run();
		Assert.True(h.Fake.Games.IsSupportedModeOn);
		Assert.True(h.Fake.Games.IsAlways("Assetto Corsa Competizione"));
		Assert.Contains(h.Vm.SupportedGames.Games, g => g.Name == "Assetto Corsa Competizione" && g.IsAlways);
		int asks = h.Raised.Count(n => n.Type == NotificationType.GameAsk);

		// Next launch: on by itself, announced once, no question.
		h.Fake.Watcher.Exit("AC2-Win64-Shipping.exe");
		Focus(h, null);
		Assert.False(h.Fake.Games.IsSupportedModeOn);
		Focus(h, "AC2-Win64-Shipping.exe");
		Assert.True(h.Fake.Games.IsSupportedModeOn);
		Assert.Equal(asks, h.Raised.Count(n => n.Type == NotificationType.GameAsk));
		Assert.Equal(NotificationType.GameDetected, h.Raised[^1].Type);
	}

	[AvaloniaFact]
	public void Game_UnknownApps_AreNeverAskedAbout()
	{
		var h = new Harness();
		Focus(h, "claude.exe");
		Focus(h, "chrome.exe");
		Focus(h, "Tetris.exe");
		Assert.Empty(h.Raised);
		Assert.Equal("Cyberpunk 2077", h.Fake.Games.State.Name);
	}

	[AvaloniaFact]
	public void Game_ModeTexts_InBothLanguages()
	{
		Assert.Equal("DualSense modu açık", Text.NotifyGameSupported(Language.Turkish));
		Assert.Equal("DualSense mode is on", Text.NotifyGameSupported(Language.English));
		Assert.Equal("Turn on DualSense mode?", Text.AskGameBody(Language.English));
		Assert.Equal(("Aç", "Şimdi Değil", "Her Zaman"), (Text.AskTurnOn(Language.Turkish), Text.AskNotNow(Language.Turkish), Text.AskAlways(Language.Turkish)));
		Assert.Equal(("Turn On", "Not Now", "Always"), (Text.AskTurnOn(Language.English), Text.AskNotNow(Language.English), Text.AskAlways(Language.English)));
		Assert.Equal("DualSense Modu", Text.SupportedMode(Language.Turkish));
		Assert.Equal("DualSense Mode", Text.SupportedMode(Language.English));
	}

	[AvaloniaFact]
	public void ToastActions_RunTheActionAndAskToClose()
	{
		int ran = 0;
		var n = new AppNotification(NotificationType.GameAsk, NotificationKind.Info, "t", "b", AppPage.SupportedGames)
		{
			Actions = [new("A", () => ran++), new("B", () => ran += 10, IsPrimary: true)]
		};
		var vm = new ToastViewModel(n);
		int closed = 0;
		vm.ActionInvoked += () => closed++;
		Assert.True(vm.HasActions);
		Assert.Equal(["A", "B"], vm.Actions.Select(a => a.Label));
		vm.Actions[1].Command.Execute(null);
		Assert.Equal((10, 1), (ran, closed));
		Assert.False(new ToastViewModel(Samples(Language.English).First().Notification).HasActions);
	}

	[AvaloniaFact]
	public void SteamInput_WarnsOncePerGameSession()
	{
		var h = new Harness();
		h.Change(f => f.SteamInput = new SteamInputResult(SteamInputStatus.OnGlobally, true, "advice"));
		h.Later();
		h.Fake.RaiseStateChanged();
		h.Later();
		h.Fake.RaiseStateChanged();
		Assert.Equal([NotificationType.SteamInput], h.Types);
		Assert.Equal(NotificationKind.Warning, h.Raised[0].Kind);

		// Off is no warning; a new game is a new session.
		h.Later();
		h.Fake.Watcher.Exe = "Returnal-Win64-Shipping.exe";
		h.Fake.Games.Tick();
		h.Fake.RaiseStateChanged();
		Assert.Equal([NotificationType.SteamInput, NotificationType.SteamInput, NotificationType.GameAsk], h.Types);
	}

	[AvaloniaFact]
	public void SteamInput_Off_GivesNoWarning()
	{
		var h = new Harness();
		h.Change(f => f.SteamInput = new SteamInputResult(SteamInputStatus.Off, true, "advice"));
		Assert.Empty(h.Raised);
	}

	[AvaloniaFact]
	public void ServiceNotices_BecomeNotifications()
	{
		var h = new Harness();
		WithLanguage(Language.English, () =>
		{
			h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "USB/IP is not installed."));
			h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.PoweredOffIdle));
			h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.HidHideMissing));
		});
		Assert.Equal([NotificationType.BridgeError, NotificationType.PoweredOffIdle, NotificationType.HidHideMissing], h.Types);
		Assert.Equal(NotificationKind.Error, h.Raised[0].Kind);
		Assert.Equal("Virtual Controller Error", h.Raised[0].Title);
		Assert.Equal("USB/IP is not installed.", h.Raised[0].Body);
		Assert.Equal(AppNotification.ErrorDuration, h.Raised[0].Duration);
		Assert.Equal(AppNotification.NormalDuration, h.Raised[1].Duration);
		Assert.Equal(NotificationKind.Warning, h.Raised[2].Kind);

		// HidHide only once per run, even after the cooldowns.
		h.Later();
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.HidHideMissing));
		Assert.Equal(3, h.Raised.Count);
	}

	[AvaloniaFact]
	public void IdlePowerOff_SuppressesTheDisconnectToast()
	{
		var h = new Harness();
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.PoweredOffIdle));
		h.Change(f => f.IsConnected = false);
		Assert.Equal([NotificationType.PoweredOffIdle], h.Types);
	}

	// Dedupe and rate limits

	[AvaloniaFact]
	public void SameType_WithinCooldown_IsDropped()
	{
		var h = new Harness();
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "a"));
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "b"));
		Assert.Single(h.Raised);
		h.Now += (long)NotificationCenter.TypeCooldown.TotalMilliseconds + 1;
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "b"));
		Assert.Equal(2, h.Raised.Count);
	}

	[AvaloniaFact]
	public void SameText_WithinRepeatCooldown_IsDropped()
	{
		var h = new Harness();
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "a"));
		h.Now += (long)NotificationCenter.TypeCooldown.TotalMilliseconds + 1;
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "a"));
		Assert.Single(h.Raised);
		h.Later();
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "a"));
		Assert.Equal(2, h.Raised.Count);
	}

	[AvaloniaFact]
	public void Flapping_Connection_DoesNotSpam()
	{
		var h = new Harness(connected: false);
		for (int i = 0; i < 10; i++)
		{
			h.Change(f => f.IsConnected = !f.IsConnected);
			h.Now += 500;
		}
		Assert.Equal([NotificationType.Connected, NotificationType.Disconnected], h.Types);
	}

	[AvaloniaFact]
	public void Burst_IsLimited()
	{
		var h = new Harness();
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "a"));
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.PoweredOffIdle));
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.HidHideMissing));
		h.Change(f => f.BatteryPercent = 15);
		h.Change(f => f.IsConnected = false);
		Assert.Equal(NotificationCenter.BurstLimit, h.Raised.Count);
		h.Now += (long)NotificationCenter.BurstWindow.TotalMilliseconds;
		h.Change(f => f.IsConnected = true);
		Assert.Equal(NotificationCenter.BurstLimit + 1, h.Raised.Count);
	}

	// Switches and "don't notify while playing"

	[AvaloniaFact]
	public void PerTypeSwitches_SilenceTheirKinds()
	{
		var h = new Harness();
		h.Settings.NotifyBattery = false;
		h.Settings.NotifyErrors = false;
		h.Change(f => f.BatteryPercent = 15);
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "a"));
		Assert.Empty(h.Raised);
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.PoweredOffIdle));
		Assert.Equal([NotificationType.PoweredOffIdle], h.Types);
		foreach (NotificationType type in Enum.GetValues<NotificationType>())
		{
			Assert.Equal(type is not (NotificationType.BatteryLow or NotificationType.BatteryCritical or NotificationType.Charged or NotificationType.BridgeError),
				h.Vm.Notifications.IsEnabled(type));
		}
	}

	[AvaloniaFact]
	public void MasterSwitch_SilencesEverything_ButTheTrayHint()
	{
		var h = new Harness();
		h.Settings.NotificationsEnabled = false;
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "a"));
		h.Change(f => f.IsConnected = false);
		Assert.Empty(h.Raised);
		Assert.True(h.Vm.HideToTrayOnClose(exiting: false));
		Assert.Equal([NotificationType.TrayHint], h.Types);
	}

	[AvaloniaFact]
	public void QuietWhilePlaying_SilencesDuringFullScreenOrAConnectedGame()
	{
		var h = new Harness();
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "while not playing"));
		Assert.Single(h.Raised);
		h.Later();
		h.Settings.QuietWhilePlaying = true;
		h.FullScreen = true;
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "full screen"));
		Assert.Single(h.Raised);
		h.FullScreen = false;
		h.Fake.StartBridgeAsync();
		h.Fake.Tick(); // the simulated game connects
		Assert.True(h.Fake.IsGameConnected);
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "game connected"));
		Assert.Single(h.Raised);
		h.Fake.StopBridgeAsync();
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "done playing"));
		Assert.Equal(2, h.Raised.Count);
	}

	[AvaloniaFact]
	public void PlayingFullScreen_WithoutQuietMode_StillNotifies()
	{
		var h = new Harness { FullScreen = true };
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, "a"));
		Assert.Single(h.Raised);
		Assert.True(h.Vm.Notifications.IsPlaying);
	}

	// Tray

	[AvaloniaFact]
	public void TrayMenu_CommandsCallTheServiceAndShell()
	{
		var h = new Harness();
		var shell = new FakeShell();
		var tray = new TrayViewModel(h.Fake, h.Settings, shell);
		WithLanguage(Language.English, () =>
		{
			Assert.Equal("Start Virtual Controller", tray.BridgeText);
			tray.ToggleBridgeCommand.Execute(null);
			Assert.Equal(1, h.Fake.Starts);
			Assert.True(h.Fake.IsBridgeRunning);
			Assert.Equal("Stop Virtual Controller", tray.BridgeText);
			Assert.True(tray.CanPowerOff);
			tray.PowerOffCommand.Execute(null);
			Assert.Equal(1, h.Fake.PowerOffs);
			tray.ToggleBridgeCommand.Execute(null);
			Assert.Equal(1, h.Fake.Stops);
			Assert.False(tray.CanPowerOff);

			Assert.Equal("Turn Off Notifications", tray.NotificationsText);
			tray.ToggleNotificationsCommand.Execute(null);
			Assert.False(h.Settings.NotificationsEnabled);
			Assert.Equal("Turn On Notifications", tray.NotificationsText);
			tray.ToggleNotificationsCommand.Execute(null);
			Assert.True(h.Settings.NotificationsEnabled);

			tray.OpenCommand.Execute(null);
			Assert.Equal([null], shell.Shown);
			tray.ExitCommand.Execute(null);
			Assert.Equal(1, shell.Exits);
		});
	}

	[AvaloniaFact]
	public void TrayTooltip_ShowsStateAndBattery()
	{
		var h = new Harness();
		var tray = new TrayViewModel(h.Fake, h.Settings, new FakeShell());
		WithLanguage(Language.Turkish, () =>
		{
			Assert.Equal("DS5 Untethered · Sanal Kumanda Kapalı · Pil %78", tray.ToolTip);
			h.Fake.StartBridgeAsync();
			Assert.Equal("DS5 Untethered · Oyun Bekleniyor · Pil %78", tray.ToolTip);
			h.Fake.Tick();
			Assert.StartsWith("DS5 Untethered · Çalışıyor · Pil %", tray.ToolTip);
			h.Fake.IsConnected = false;
			Assert.Equal("DS5 Untethered · Kumanda Yok", tray.ToolTip);
		});
		WithLanguage(Language.English, () => Assert.Equal("DS5 Untethered · No Controller", tray.ToolTip));
	}

	// Close to tray

	[AvaloniaFact]
	public void Closing_HidesToTray_AndExplainsItOnce()
	{
		var store = new MemorySettingsStore();
		var h = new Harness(store: store);
		Assert.True(h.Vm.HideToTrayOnClose(exiting: false));
		Assert.Equal([NotificationType.TrayHint], h.Types);
		Assert.True(h.Settings.TrayHintShown);
		Assert.True(h.Vm.HideToTrayOnClose(exiting: false));
		Assert.Single(h.Raised);
		Assert.False(h.Vm.HideToTrayOnClose(exiting: true));
		h.Settings.KeepInTray = false;
		Assert.False(h.Vm.HideToTrayOnClose(exiting: false));
		h.Settings.SaveNow();
		Assert.True(store.Saved!.TrayHintShown);
		Assert.False(store.Saved.KeepInTray);
	}

	[AvaloniaFact]
	public void ToastClick_PagesMapToNavigation()
	{
		var h = new Harness();
		h.Vm.Navigate(AppPage.SupportedGames);
		Assert.Same(h.Vm.SupportedGames, h.Vm.CurrentPage);
		h.Vm.Navigate(AppPage.OtherGames);
		Assert.Same(h.Vm.OtherGames, h.Vm.CurrentPage);
		h.Vm.Navigate(AppPage.Settings);
		Assert.Same(h.Vm.SettingsPage, h.Vm.CurrentPage);
		h.Vm.Navigate(AppPage.Overview);
		Assert.Same(h.Vm.Overview, h.Vm.CurrentPage);
	}

	// Settings

	[AvaloniaFact]
	public void Settings_RoundTripThroughJson()
	{
		string path = Path.Combine(Path.GetTempPath(), "kopru-notify-" + Guid.NewGuid().ToString("N") + ".json");
		try
		{
			var store = new JsonSettingsStore(path);
			var h = new Harness(store: store);
			h.Settings.NotificationsEnabled = false;
			h.Settings.NotifyConnection = false;
			h.Settings.NotifyBattery = false;
			h.Settings.NotifyGame = false;
			h.Settings.NotifySteamInput = false;
			h.Settings.NotifyPowerOff = false;
			h.Settings.NotifyErrors = false;
			h.Settings.NotifyHidHide = false;
			h.Settings.QuietWhilePlaying = true;
			h.Settings.KeepInTray = false;
			h.Settings.TrayHintShown = true;
			h.Settings.SaveNow();

			var loaded = new Harness(store: new JsonSettingsStore(path)).Settings;
			Assert.False(loaded.NotificationsEnabled);
			Assert.False(loaded.NotifyConnection);
			Assert.False(loaded.NotifyBattery);
			Assert.False(loaded.NotifyGame);
			Assert.False(loaded.NotifySteamInput);
			Assert.False(loaded.NotifyPowerOff);
			Assert.False(loaded.NotifyErrors);
			Assert.False(loaded.NotifyHidHide);
			Assert.True(loaded.QuietWhilePlaying);
			Assert.False(loaded.KeepInTray);
			Assert.True(loaded.TrayHintShown);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[AvaloniaFact]
	public void Settings_OlderFile_GetsNotificationDefaults()
	{
		string path = Path.Combine(Path.GetTempPath(), "kopru-notify-" + Guid.NewGuid().ToString("N") + ".json");
		try
		{
			File.WriteAllText(path, """{ "Language": "en", "GameHapticsLevel": 120 }""");
			SettingsState s = new Harness(store: new JsonSettingsStore(path)).Settings;
			Assert.Equal(120, s.GameHapticsLevel);
			Assert.True(s.NotificationsEnabled);
			Assert.True(s.NotifyConnection && s.NotifyBattery && s.NotifyGame && s.NotifySteamInput && s.NotifyPowerOff && s.NotifyErrors && s.NotifyHidHide);
			Assert.False(s.QuietWhilePlaying);
			Assert.True(s.KeepInTray);
			Assert.False(s.TrayHintShown);
		}
		finally
		{
			Localizer.Chosen = null;
			File.Delete(path);
		}
	}

	[Fact]
	public void StartWithWindows_StartsInTheTray() =>
		Assert.Equal("\"C:\\Apps\\DS5 Untethered.exe\" --minimized", RunKeyStartupRegistration.CommandLine(@"C:\Apps\DS5 Untethered.exe"));

	// Toast rendering

	public static IEnumerable<(string Name, AppNotification Notification)> Samples(Language l)
	{
		yield return ("connected", new(NotificationType.Connected, NotificationKind.Success, Text.NotifyConnectedTitle(l),
			Text.NotifyConnectedBody("DualSense Edge", l), AppPage.Overview, 78, IsEdge: true));
		yield return ("disconnected", new(NotificationType.Disconnected, NotificationKind.Info, Text.NotifyDisconnectedTitle(l),
			Text.NotifyDisconnectedBody(l), AppPage.Overview));
		yield return ("battery-low", new(NotificationType.BatteryLow, NotificationKind.Warning, Text.NotifyBatteryLowTitle(l),
			Text.NotifyBatteryLowBody(20, l), AppPage.Overview, 20));
		yield return ("battery-critical", new(NotificationType.BatteryCritical, NotificationKind.Error, Text.NotifyBatteryCriticalTitle(l),
			Text.NotifyBatteryCriticalBody(10, l), AppPage.Overview, 10));
		yield return ("charged", new(NotificationType.Charged, NotificationKind.Success, Text.NotifyChargedTitle(l),
			Text.NotifyChargedBody(l), AppPage.Overview, 100, IsCharging: true));
		yield return ("game", new(NotificationType.GameDetected, NotificationKind.Info, "Cyberpunk 2077",
			Text.NotifyGameSupported(l), AppPage.SupportedGames));
		// A long name: cut in the title, the question below stays whole.
		yield return ("ask", new(NotificationType.GameAsk, NotificationKind.Info, "Marvel's Spider-Man 2: Digital Deluxe Edition", Text.AskGameBody(l),
			AppPage.SupportedGames)
		{
			Actions = [new(Text.AskNotNow(l), () => { }), new(Text.AskAlways(l), () => { }), new(Text.AskTurnOn(l), () => { }, IsPrimary: true)]
		});
		yield return ("steam-input", new(NotificationType.SteamInput, NotificationKind.Warning, Text.NotifySteamInputTitle(l),
			Text.NotifySteamInputBody(l), AppPage.SupportedGames));
		yield return ("powered-off", new(NotificationType.PoweredOffIdle, NotificationKind.Info, Text.NotifyPoweredOffTitle(l),
			Text.NotifyPoweredOffBody(l), AppPage.Settings));
		yield return ("bridge-error", new(NotificationType.BridgeError, NotificationKind.Error, Text.NotifyBridgeErrorTitle(l),
			Text.NotifyBridgeUsbIpMissing(l), AppPage.Overview));
		yield return ("hidhide", new(NotificationType.HidHideMissing, NotificationKind.Warning, Text.NotifyHidHideTitle(l),
			Text.NotifyHidHideBody(l), AppPage.Overview));
		yield return ("tray", new(NotificationType.TrayHint, NotificationKind.Info, Text.NotifyTrayTitle(l), Text.NotifyTrayBody(l), AppPage.Settings));
	}

	// A dark desktop-like backdrop behind the cards, so the PNGs show them as they appear over a wallpaper.
	private static readonly IBrush Desktop = new LinearGradientBrush
	{
		StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
		EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
		GradientStops = { new GradientStop(Color.Parse("#2B3A55"), 0), new GradientStop(Color.Parse("#151A24"), 1) }
	};

	private static byte[] Render(Control content, double width, double height)
	{
		var window = new Window { Width = width, Height = height, Background = Desktop, Content = content };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		using var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		using var png = new MemoryStream();
		frame!.Save(png);
		window.Close();
		return png.ToArray();
	}

	private static ToastView Card(AppNotification n, bool compact = false) => new()
	{
		DataContext = new ToastViewModel(n, compact),
		HorizontalAlignment = HorizontalAlignment.Right,
		VerticalAlignment = VerticalAlignment.Bottom,
	};

	// Every toast is the same size, and every text fits it whole except a long game name in the title.
	[AvaloniaFact]
	public void Toasts_AllTheSameSize_AndTheirTextsFit()
	{
		foreach (Language language in new[] { Language.Turkish, Language.English })
		{
			WithLanguage(language, () =>
			{
				foreach ((string name, AppNotification n) in Samples(language))
				{
					ToastView card = Card(n);
					var window = new Window { Width = 420, Height = 172, Content = new Border { Padding = new Avalonia.Thickness(20), Child = card } };
					window.Show();
					Dispatcher.UIThread.RunJobs();
					Assert.Equal((380.0, 132.0), (card.Bounds.Width, card.Bounds.Height));
					foreach (TextBlock text in card.GetVisualDescendants().OfType<TextBlock>().Where(t => !string.IsNullOrEmpty(t.Text) && t.IsEffectivelyVisible))
					{
						bool gameTitle = text.Text == n.Title && n.Type is NotificationType.GameAsk or NotificationType.GameDetected;
						if (!gameTitle)
						{
							Assert.True(text.TextLayout.WidthIncludingTrailingWhitespace <= text.Bounds.Width + 0.5, $"{name}/{language}: '{text.Text}' does not fit");
						}
					}
					window.Close();
				}
			});
		}
	}

	// Renders each toast kind, a compact one, a stack of three and the settings group; PNGs go to docs/screens only with
	// KOPRU_RENDER_SCREENS=1.
	[AvaloniaFact]
	public void RenderToasts()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string outDir = Path.Combine(RepoRoot(), "docs", "screens");
		void Save(string name, byte[] png)
		{
			Assert.True(png.Length > 0);
			if (writeFiles)
			{
				Directory.CreateDirectory(outDir);
				File.WriteAllBytes(Path.Combine(outDir, name), png);
			}
		}
		foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
		{
			WithLanguage(language, () =>
			{
				foreach ((string name, AppNotification n) in Samples(language))
				{
					Save($"toast-{name}-{code}.png", Render(new Border { Padding = new Avalonia.Thickness(20), Child = Card(n) }, 420, 172));
				}
				AppNotification game = Samples(language).First(s => s.Name == "game").Notification;
				Save($"toast-compact-{code}.png", Render(new Border { Padding = new Avalonia.Thickness(20), Child = Card(game, compact: true) }, 420, 172));

				// Three stacked as on screen: oldest at the bottom, newest on top, 10 px apart.
				var stack = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right };
				foreach (string name in new[] { "steam-input", "game", "connected" })
				{
					stack.Children.Add(Card(Samples(language).First(s => s.Name == name).Notification));
				}
				Save($"toast-stack-{code}.png", Render(new Border { Padding = new Avalonia.Thickness(20), Child = stack }, 420, 456));

				// The settings page scrolled to the notifications group.
				var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
				vm.Sticks.StopPreview();
				vm.Navigate(AppPage.Settings);
				var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
				window.Show();
				Dispatcher.UIThread.RunJobs();
				AvaloniaHeadlessPlatform.ForceRenderTimerTick();
				Border panel = window.GetVisualDescendantsOfType<Border>().First(b => b.Name == "NotificationsPanel");
				panel.BringIntoView();
				Thread.Sleep(320);
				Dispatcher.UIThread.RunJobs();
				AvaloniaHeadlessPlatform.ForceRenderTimerTick();
				using var frame = window.CaptureRenderedFrame();
				Assert.NotNull(frame);
				using var png = new MemoryStream();
				frame!.Save(png);
				Save($"settings-notifications-{code}.png", png.ToArray());
				window.Close();
			});
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

internal static class VisualHelpers
{
	public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Avalonia.Visual visual) =>
		Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(visual).OfType<T>();
}
