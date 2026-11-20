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
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Steam;
using Kopru.Core.Virtual;

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

	// A known game starting is a plain information toast (no question, no buttons), once per launch; it never changes the mode.
	[AvaloniaFact]
	public void Game_KnownGame_IsAnnouncedOncePerLaunch_WithoutButtons()
	{
		var h = new Harness();
		// Cyberpunk was already in front when the app started.
		h.Fake.RaiseStateChanged();
		Assert.Empty(h.Raised);
		WithLanguage(Language.Turkish, () => Focus(h, "Spider-Man.exe"));
		AppNotification shown = Assert.Single(h.Raised);
		Assert.Equal(NotificationType.GameDetected, shown.Type);
		Assert.Equal(NotificationKind.Info, shown.Kind);
		Assert.Equal("Marvel's Spider-Man Remastered", shown.Title);
		Assert.Equal("Oyun Şunu Görüyor: DualSense", shown.Body);
		Assert.Equal(AppNotification.NormalDuration, shown.Duration);

		// Away (to nothing, to unknown apps) and back: the same launch, no repeat.
		Focus(h, null);
		Focus(h, "claude.exe");
		Focus(h, "chrome.exe");
		Focus(h, "Tetris.exe");
		Focus(h, "Spider-Man.exe");
		Assert.Single(h.Raised);

		// The game was closed and started again: announced again.
		h.Fake.Watcher.Exit("Spider-Man.exe");
		Focus(h, null);
		Focus(h, "Spider-Man.exe");
		Assert.Equal(2, h.Raised.Count(n => n.Type == NotificationType.GameDetected));
	}

	// The game toast honours the notification settings like every other toast.
	[AvaloniaFact]
	public void Game_Announcement_HonoursTheGameSwitchAndQuiet()
	{
		var h = new Harness();
		h.Settings.NotifyGame = false;
		Focus(h, "Returnal-Win64-Shipping.exe");
		Assert.Empty(h.Raised);

		var quiet = new Harness();
		quiet.Settings.QuietWhilePlaying = true;
		quiet.FullScreen = true;
		Focus(quiet, "Returnal-Win64-Shipping.exe");
		Assert.Empty(quiet.Raised);
	}

	// Collects this test's "toast: ..." log lines (other tests log in parallel: only lines naming game are kept).
	private static List<string> WatchToastLog(string game, Action action)
	{
		var lines = new List<string>();
		void Watch(string line)
		{
			if (line.StartsWith("toast: ", StringComparison.Ordinal) && line.Contains(game, StringComparison.Ordinal))
			{
				lock (lines)
				{
					lines.Add(line);
				}
			}
		}
		Kopru.Core.Diagnostics.AppLog.Written += Watch;
		try
		{
			action();
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= Watch;
		}
		lock (lines)
		{
			return lines.ToList();
		}
	}

	// A supported game started: announced once per launch with the controller type it sees, and logged.
	[AvaloniaFact]
	public void Game_Starting_IsAnnouncedOncePerLaunch_WithTheTypeItSees_AndLogged()
	{
		var h = new Harness();
		h.Fake.RaiseStateChanged();
		List<string> log = WatchToastLog("Returnal", () => Focus(h, "Returnal-Win64-Shipping.exe"));
		AppNotification shown = Assert.Single(h.Raised);
		Assert.Equal(NotificationType.GameDetected, shown.Type);
		Assert.Equal("Returnal", shown.Title);
		Assert.Equal(Text.GameSees("DualSense"), shown.Body);
		Assert.Contains("toast: game detected for Returnal shown", log);

		// Away and back in the same launch: no repeat (and no new log line on every state change).
		log = WatchToastLog("Returnal", () =>
		{
			Focus(h, "chrome.exe");
			Focus(h, "Returnal-Win64-Shipping.exe");
			h.Fake.RaiseStateChanged();
			h.Fake.RaiseStateChanged();
		});
		Assert.Single(h.Raised);
		Assert.Empty(log);

		// Closed and started again: a new launch, announced again.
		h.Fake.Watcher.Exit("Returnal-Win64-Shipping.exe");
		Focus(h, null);
		Focus(h, "Returnal-Win64-Shipping.exe");
		Assert.Equal(2, h.Raised.Count(n => n.Type == NotificationType.GameDetected));
	}

	// The bridge running with no game: the virtual controller type swaps at once.
	private static Harness Running()
	{
		var h = new Harness();
		h.Vm.Overview.ToggleBridgeCommand.Execute(null);
		Dispatcher.UIThread.RunJobs();
		h.Fake.Watcher.Exit("Cyberpunk2077.exe");
		h.Fake.Watcher.Exe = null;
		h.Fake.Games.Tick();
		h.Fake.RaiseStateChanged();
		h.Raised.Clear();
		h.Later();
		return h;
	}

	// The controller type toast shares the GameDetected type; its 20 s type cooldown used to swallow the game toast.
	[AvaloniaFact]
	public void Game_Announcement_IsNotSwallowedByTheControllerTypeToast()
	{
		Harness h = Running();
		h.Fake.Games.SetForcedPadType(VirtualPadType.DualShock4);
		// A listed game: its own choice, else Automatic would give it DualSense.
		h.Fake.Games.SetPadType("Returnal", VirtualPadType.DualShock4);
		h.Fake.Watcher.Exe = "Returnal-Win64-Shipping.exe";
		h.Fake.Games.Tick();
		h.Fake.RaiseStateChanged();
		Assert.Contains(h.Raised, n => n.Type == NotificationType.GameDetected && n.Title == Text.PadModeDualShock4Title());
		AppNotification game = Assert.Single(h.Raised, n => n.Title == "Returnal");
		Assert.Equal(Text.GameSees("DualShock 4"), game.Body);
	}

	// One toast per swap (not one per controller): the type as the title; with no game the default type's line, DualSense
	// back when no default is set; the bridge's own swap notices show nothing.
	[AvaloniaFact]
	public void PadType_Swap_NoGame_OneToastWithTheTypeTitle()
	{
		Harness h = Running();
		WithLanguage(Language.Turkish, () => h.Fake.Games.SetForcedPadType(VirtualPadType.Xbox360));
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.PadTypeChanged, nameof(VirtualPadType.Xbox360)));
		h.Fake.RaiseNotice(new ServiceNotice(ServiceNoticeKind.PadTypeChanged, nameof(VirtualPadType.Xbox360)));
		AppNotification toast = Assert.Single(h.Raised);
		Assert.Equal(("Xbox 360 Modu", "Hakkında bilgi olmayan oyunlarda kullanılır."), (toast.Title, toast.Body));
		Assert.Equal((NotificationType.GameDetected, AppPage.SupportedGames), (toast.Type, toast.Page));

		h.Later();
		h.Fake.Games.SetForcedPadType(null);
		Assert.Equal((Text.PadModeDualSenseTitle(), Text.PadModeDualSenseBody()), (h.Raised[^1].Title, h.Raised[^1].Body));
		Assert.Equal(2, h.Raised.Count);
	}

	// A swap while a game runs (Apply Now): "restart the game if it doesn't see the controller". A change held back for the
	// running game: "applies when you restart the game", once.
	[AvaloniaFact]
	public void PadType_WhileAGameRuns_PendingThenApplyNow()
	{
		var h = new Harness();
		h.Vm.Overview.ToggleBridgeCommand.Execute(null);
		Dispatcher.UIThread.RunJobs();
		h.Fake.RaiseStateChanged();
		h.Raised.Clear();
		h.Later();
		string key = h.Fake.Games.ActiveGame!.Key;
		WithLanguage(Language.Turkish, () => h.Fake.Games.SetPadType(key, VirtualPadType.DualShock4));
		AppNotification pending = Assert.Single(h.Raised);
		Assert.Equal(("DualShock 4 Modu", "Oyunu yeniden başlatınca geçerli olur."), (pending.Title, pending.Body));
		h.Fake.RaiseStateChanged();
		Assert.Single(h.Raised);

		h.Later();
		WithLanguage(Language.Turkish, () => h.Settings.ApplyPadTypeNowCommand.Execute(null));
		Assert.Equal(2, h.Raised.Count);
		Assert.Equal(("DualShock 4 Modu", "Oyun kumandayı görmüyorsa oyunu yeniden başlat."), (h.Raised[^1].Title, h.Raised[^1].Body));
	}

	// The virtual controller off (or no controller): no game sees a change, so no toast.
	[AvaloniaFact]
	public void PadType_BridgeOff_NoToast()
	{
		var h = new Harness();
		h.Fake.Watcher.Exit("Cyberpunk2077.exe");
		h.Fake.Games.Tick();
		h.Later();
		h.Fake.Games.SetForcedPadType(VirtualPadType.Xbox360);
		Assert.Empty(h.Raised);
	}

	// A game coming up with its own type: the type toast and the game toast of that moment are one (the game toast, which
	// says the type, takes the type toast's place).
	[AvaloniaFact]
	public void PadType_GameStartsWithItsOwnType_OneToastWithTheGame()
	{
		Harness h = Running();
		h.Fake.Games.SetPadType("Returnal", VirtualPadType.Xbox360);
		h.Raised.Clear();
		h.Fake.Watcher.Exe = "Returnal-Win64-Shipping.exe";
		h.Fake.Games.Tick();
		h.Fake.RaiseStateChanged();
		AppNotification game = h.Raised[^1];
		Assert.Equal(("Returnal", Text.GameSees("Xbox 360")), (game.Title, game.Body));
		if (h.Raised.Count > 1)
		{
			Assert.Equal(NotificationType.GameDetected, game.Replaces);
		}
	}

	[AvaloniaFact]
	public void Game_Announcement_Suppressed_LogsWhy()
	{
		var h = new Harness();
		h.Settings.NotificationsEnabled = false;
		List<string> log = WatchToastLog("Returnal", () => Focus(h, "Returnal-Win64-Shipping.exe"));
		Assert.Empty(h.Raised);
		Assert.Contains("toast: game detected for Returnal not shown (notifications off)", log);

		var quiet = new Harness();
		quiet.Settings.QuietWhilePlaying = true;
		quiet.FullScreen = true;
		log = WatchToastLog("Assetto", () => Focus(quiet, "AC2-Win64-Shipping.exe"));
		Assert.Empty(quiet.Raised);
		Assert.Contains("toast: game detected for Assetto Corsa Competizione not shown (quiet while playing)", log);
	}

	[AvaloniaFact]
	public void Game_UnknownApps_AreNeverAnnounced()
	{
		var h = new Harness();
		Focus(h, "claude.exe");
		Focus(h, "chrome.exe");
		Focus(h, "Tetris.exe");
		Assert.Empty(h.Raised);
		Assert.Equal("Cyberpunk 2077", h.Fake.Games.State.Name);
	}

	[AvaloniaFact]
	public void Game_PadTexts_InBothLanguages()
	{
		Assert.Equal("Oyunu yeniden başlatınca geçerli olur.", Text.PadToastPendingBody(Language.Turkish));
		Assert.Equal("Applies when you restart the game.", Text.PadToastPendingBody(Language.English));
		Assert.Equal("Tanınan bir oyun başlayınca ve kumanda türü değişince.", Text.NotifyGameHint(Language.Turkish));
		Assert.Equal("When a known game starts and when the controller type changes.", Text.NotifyGameHint(Language.English));
		WithLanguage(Language.Turkish, () => Assert.Equal("DualShock 4 Modu", NotificationCenter.PadTypeTitle(VirtualPadType.DualShock4)));
		WithLanguage(Language.English, () => Assert.Equal("Xbox 360 Mode", NotificationCenter.PadTypeTitle(VirtualPadType.Xbox360)));
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
		// (the game toast comes first, then the warning for the new game)
		Assert.Equal([NotificationType.SteamInput, NotificationType.GameDetected, NotificationType.SteamInput], h.Types);
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
		h.Vm.SettingsPage.SelectedTab = SettingsViewModel.AudioTab;
		h.Vm.Navigate(AppPage.SettingsShortcuts);
		Assert.Same(h.Vm.SettingsPage, h.Vm.CurrentPage);
		Assert.True(h.Vm.SettingsPage.IsShortcutsTab);
		h.Vm.Navigate(AppPage.SettingsGeneral);
		Assert.Same(h.Vm.SettingsPage, h.Vm.CurrentPage);
		Assert.True(h.Vm.SettingsPage.IsGeneralTab);
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
	public void StartWhenISignIn_StartsInTheBackground() =>
		Assert.Contains("Exec=\"/opt/ds5/Kopru.App\" --minimized", XdgAutostartRegistration.Entry("/opt/ds5/Kopru.App"));

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
		yield return ("game", NotificationCenter.GameDetected("Cyberpunk 2077", VirtualPadType.DualSense));
		// A long name: cut in the title, the line below stays whole.
		yield return ("game-long", NotificationCenter.GameDetected("Marvel's Spider-Man 2: Digital Deluxe Edition", VirtualPadType.DualShock4));
		// The controller type toasts: swapped while a game runs, the default type with no game, DualSense back, held back.
		yield return ("pad-swap-game", NotificationCenter.PadTypeNotice(VirtualPadType.Xbox360, PadTypeSource.Game, gameRunning: true));
		yield return ("pad-default", NotificationCenter.PadTypeNotice(VirtualPadType.DualShock4, PadTypeSource.DefaultSetting, gameRunning: false));
		yield return ("pad-dualsense", NotificationCenter.PadTypeNotice(VirtualPadType.DualSense, PadTypeSource.Default, gameRunning: false));
		yield return ("pad-pending", NotificationCenter.PadTypePending(VirtualPadType.DualShock4));
		yield return ("steam-input", new(NotificationType.SteamInput, NotificationKind.Warning, Text.NotifySteamInputTitle(l),
			Text.NotifySteamInputBody(l), AppPage.SupportedGames));
		yield return ("powered-off", new(NotificationType.PoweredOffIdle, NotificationKind.Info, Text.NotifyPoweredOffTitle(l),
			Text.NotifyPoweredOffBody(l), AppPage.SettingsGeneral));
		yield return ("bridge-error", new(NotificationType.BridgeError, NotificationKind.Error, Text.NotifyBridgeErrorTitle(l),
			Text.NotifyBridgeUsbIpMissing(l), AppPage.Overview));
		yield return ("hidhide", new(NotificationType.HidHideMissing, NotificationKind.Warning, Text.NotifyHidHideTitle(l),
			Text.NotifyHidHideBody(l), AppPage.Overview));
		yield return ("tray", new(NotificationType.TrayHint, NotificationKind.Info, Text.NotifyTrayTitle(l), Text.NotifyTrayBody(l), AppPage.SettingsGeneral));
		// Mouse Mode switched from the controller (touchpad double press, PS + Options).
		yield return ("mouse-on-controller", new(NotificationType.Shortcut, NotificationKind.Info, Text.DesktopOnTitle(l), Text.DesktopOnBody(l), AppPage.SettingsShortcuts));
		yield return ("mouse-off-controller", new(NotificationType.Shortcut, NotificationKind.Info, Text.DesktopOffTitle(l), Text.DesktopOffBody(l), AppPage.SettingsShortcuts));
		yield return ("mouse-off", NotificationCenter.MouseModeOff(null));
		yield return ("mouse-off-game", NotificationCenter.MouseModeOff("Uncharted: Legacy of Thieves Collection"));
		// "Game detected" and Mouse Mode off in the same moment: one toast.
		yield return ("mouse-off-merged", NotificationCenter.InGame(NotificationCenter.GameDetected("Uncharted: Legacy of Thieves Collection", VirtualPadType.DualSense, 78),
			NotificationType.GameDetected));
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
						bool gameTitle = text.Text == n.Title && (n.Type is NotificationType.GameDetected
							|| n.Type is NotificationType.MouseModeOff && n.Title != Text.DesktopAutoOffTitle(language));
						if (!gameTitle)
						{
							Assert.True(text.TextLayout.WidthIncludingTrailingWhitespace <= text.Bounds.Width + 0.5, $"{name}/{language}: '{text.Text}' does not fit");
							Assert.False(text.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{name}/{language}: '{text.Text}' is cut");
						}
					}
					window.Close();
				}
			});
		}
	}

	// Renders each toast kind, a compact one and a stack of three (the Settings tab: SettingsFitTests); PNGs go to docs/screens only with
	// KOPRU_RENDER_SCREENS=1.
	[AvaloniaFact]
	public void RenderToasts()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string outDir = Environment.GetEnvironmentVariable("KOPRU_RENDER_DIR") is { Length: > 0 } dir ? dir : Path.Combine(RepoRoot(), "docs", "screens");
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
