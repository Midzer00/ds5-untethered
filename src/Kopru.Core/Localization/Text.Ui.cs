// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Strings of the desktop app (Kopru.App). Numeric parameters are double so the reflection tests can call every method.
public static partial class Text
{
	// Navigation
	public static string NavOverview(Language? l = null) => T(l, "Nav.Overview");
	public static string NavLight(Language? l = null) => T(l, "Nav.Light");
	public static string NavSettings(Language? l = null) => T(l, "Nav.Settings");

	// Device card
	public static string DeviceConnectedBluetooth(Language? l = null) => T(l, "Device.ConnectedBluetooth");
	public static string DeviceNotConnected(Language? l = null) => T(l, "Device.NotConnected");
	public static string DeviceNoController(Language? l = null) => T(l, "Device.NoController");
	public static string BatteryCharging(double percent, Language? l = null) => T(l, "Battery.ChargingPercent", A("percent", percent, "F0"));
	public static string BatteryUnknown(Language? l = null) => T(l, "Battery.Unknown");

	// Overview
	public static string OverviewSubtitle(Language? l = null) => T(l, "Overview.Subtitle");
	public static string CardConnection(Language? l = null) => T(l, "Card.Connection");
	public static string CardLatency(Language? l = null) => T(l, "Card.Latency");
	public static string CardBattery(Language? l = null) => T(l, "Card.Battery");
	public static string CardHaptics(Language? l = null) => T(l, "Card.Haptics");
	public static string ConnectionBluetooth(Language? l = null) => T(l, "Connection.Bluetooth");
	public static string InputRate(double rate, Language? l = null) => T(l, "Input.Rate", A("rate", rate, "F0"));
	public static string BatteryValue(double percent, Language? l = null) => T(l, "Battery.Value", A("percent", percent, "F0"));
	public static string Charging(Language? l = null) => T(l, "Common.Charging");
	public static string OnBattery(Language? l = null) => T(l, "On.Battery");
	public static string HapticsActive(Language? l = null) => T(l, "Haptics.Active");
	public static string HapticsIdle(Language? l = null) => T(l, "Haptics.Idle");
	public static string HapticsFromGame(Language? l = null) => T(l, "Haptics.FromGame");
	public static string BridgeStart(Language? l = null) => T(l, "Bridge.Start");
	public static string BridgeStop(Language? l = null) => T(l, "Bridge.Stop");
	public static string BridgeStatusRunning(Language? l = null) => T(l, "Bridge.StatusRunning");
	public static string BridgeStatusStopped(Language? l = null) => T(l, "Bridge.StatusStopped");
	public static string QuickTests(Language? l = null) => T(l, "Quick.Tests");
	public static string QuickSettings(Language? l = null) => T(l, "Quick.Settings");
	public static string TestHapticsLeft(Language? l = null) => T(l, "Test.HapticsLeft");
	public static string TestHapticsRight(Language? l = null) => T(l, "Test.HapticsRight");
	public static string TestTrigger(Language? l = null) => T(l, "Test.Trigger");
	public static string TestLight(Language? l = null) => T(l, "Test.Light");
	public static string GameHapticsLevel(Language? l = null) => T(l, "Game.HapticsLevel");
	public static string RumbleLevel(Language? l = null) => T(l, "Rumble.Level");
	public static string PercentValue(double percent, Language? l = null) => T(l, "Percent.Value", A("percent", percent, "F0"));
	public static string TestSent(Language? l = null) => T(l, "Test.Sent");
	public static string ComingSoon(Language? l = null) => T(l, "Coming.Soon");

	// Haptics & rumble
	public static string HapticsIntro(Language? l = null) => T(l, "Haptics.Intro");
	public static string GameHapticsHint(Language? l = null) => T(l, "Game.HapticsHint");
	public static string RumbleHint(Language? l = null) => T(l, "Rumble.Hint");
	public static string TestBoth(Language? l = null) => T(l, "Test.Both");
	public static string TestRumble(Language? l = null) => T(l, "Test.Rumble");

	// Triggers
	public static string TriggerStrength(Language? l = null) => T(l, "Trigger.Strength");
	public static string TriggerStrengthHint(Language? l = null) => T(l, "Trigger.StrengthHint");
	public static string EffectOff(Language? l = null) => T(l, "Effect.Off");
	public static string EffectFeedback(Language? l = null) => T(l, "Effect.Feedback");
	public static string EffectWeapon(Language? l = null) => T(l, "Effect.Weapon");
	public static string EffectVibration(Language? l = null) => T(l, "Effect.Vibration");

	// Light
	public static string LightIntro(Language? l = null) => T(l, "Light.Intro");
	public static string LightbarColour(Language? l = null) => T(l, "Lightbar.Colour");
	public static string Brightness(Language? l = null) => T(l, "Common.Brightness");
	public static string PlayerLeds(Language? l = null) => T(l, "Player.Leds");
	public static string PlayerLedsHint(Language? l = null) => T(l, "Player.LedsHint");
	public static string ColourBlue(Language? l = null) => T(l, "Colour.Blue");
	public static string ColourRed(Language? l = null) => T(l, "Colour.Red");
	public static string ColourGreen(Language? l = null) => T(l, "Colour.Green");
	public static string ColourPurple(Language? l = null) => T(l, "Colour.Purple");
	public static string ColourOrange(Language? l = null) => T(l, "Colour.Orange");
	public static string ColourWhite(Language? l = null) => T(l, "Colour.White");

	// Settings
	public static string LanguageLabel(Language? l = null) => T(l, "Language.Label");
	public static string LanguageFollowSystem(Language? l = null) => T(l, "Language.FollowSystem");
	public static string StartWithWindows(Language? l = null) => T(l, "Start.WithWindows");
	public static string StartWithWindowsHint(Language? l = null) => T(l, "Start.WithWindowsHint");
	public static string IdlePowerOff(Language? l = null) => T(l, "Idle.PowerOff");
	public static string IdlePowerOffHint(Language? l = null) => T(l, "Idle.PowerOffHint");
	public static string Minutes(double minutes, Language? l = null) => T(l, "Common.Minutes", A("minutes", minutes, "F0"));
	public static string Never(Language? l = null) => T(l, "Common.Never");
	public static string General(Language? l = null) => T(l, "Common.General");
	public static string Tools(Language? l = null) => T(l, "Common.Tools");
	public static string About(Language? l = null) => T(l, "Common.About");
	public static string AboutVersion(string version, Language? l = null) => T(l, "About.Version", A("version", version));
	public static string AboutTagline(Language? l = null) =>
		T(l, "About.Tagline");
	public static string RunSetupWizard(Language? l = null) => T(l, "Run.SetupWizard");
	public static string TestBluetoothAdapter(Language? l = null) => T(l, "Test.BluetoothAdapter");
	public static string ExportDiagnostics(Language? l = null) => T(l, "Export.Diagnostics");

	// Redesigned UI: bridge state, stats, cards and setting rows
	public static string BridgeStateStopped(Language? l = null) => T(l, "Bridge.StateStopped");
	public static string BridgeStateWaiting(Language? l = null) => T(l, "Bridge.StateWaiting");
	public static string BridgeStateRunning(Language? l = null) => T(l, "Bridge.StateRunning");
	public static string BridgeHeadlineStopped(Language? l = null) => T(l, "Bridge.HeadlineStopped");
	public static string BridgeHeadlineWaiting(Language? l = null) => T(l, "Bridge.HeadlineWaiting");
	public static string BridgeHeadlineRunning(Language? l = null) => T(l, "Bridge.HeadlineRunning");
	public static string BridgeDetailWaiting(Language? l = null) => T(l, "Bridge.DetailWaiting");
	public static string BridgeDetailRunning(Language? l = null) => T(l, "Bridge.DetailRunning");
	public static string StatLatency(Language? l = null) => T(l, "Stat.Latency");
	public static string StatInputRate(Language? l = null) => T(l, "Stat.InputRate");
	public static string LiveOutput(Language? l = null) => T(l, "Live.Output");
	public static string LeftShort(Language? l = null) => T(l, "Left.Short");
	public static string RightShort(Language? l = null) => T(l, "Right.Short");
	public static string EffectOffHint(Language? l = null) => T(l, "Effect.OffHint");
	public static string EffectFeedbackHint(Language? l = null) => T(l, "Effect.FeedbackHint");
	public static string EffectWeaponHint(Language? l = null) => T(l, "Effect.WeaponHint");
	public static string EffectVibrationHint(Language? l = null) => T(l, "Effect.VibrationHint");
	public static string LanguageHint(Language? l = null) => T(l, "Language.Hint");
	public static string RunSetupWizardHint(Language? l = null) => T(l, "Run.SetupWizardHint");
	public static string TestBluetoothAdapterHint(Language? l = null) => T(l, "Test.BluetoothAdapterHint");
	public static string ExportDiagnosticsHint(Language? l = null) => T(l, "Export.DiagnosticsHint");
	public static string ActionRun(Language? l = null) => T(l, "Action.Run");
	public static string ActionTest(Language? l = null) => T(l, "Action.Test");
	public static string ActionExport(Language? l = null) => T(l, "Action.Export");

	// Real controller service
	public static string TestNoController(Language? l = null) => T(l, "Test.NoController");
	public static string AdapterTestRunning(Language? l = null) => T(l, "Adapter.TestRunning");

	// Setup wizard page
	public static string SetupPageTitle(Language? l = null) => T(l, "Setup.PageTitle");
	public static string SetupPageIntro(Language? l = null) => T(l, "Setup.PageIntro");
	public static string SetupClose(Language? l = null) => T(l, "Setup.Close");

	// Overview dashboard
	public static string OverviewIntro(Language? l = null) => T(l, "Overview.Intro");
	public static string OverallOk(Language? l = null) => T(l, "Overall.Ok");
	public static string OverallAttention(Language? l = null) => T(l, "Overall.Attention");
	public static string OverallNoController(Language? l = null) => T(l, "Overall.NoController");
	public static string CardBridge(Language? l = null) => T(l, "Card.Bridge");
	public static string CardController(Language? l = null) => T(l, "Card.Controller");
	public static string RowLink(Language? l = null) => T(l, "Row.Link");
	public static string RowStatus(Language? l = null) => T(l, "Row.Status");
	public static string RowGame(Language? l = null) => T(l, "Row.Game");
	public static string RowMode(Language? l = null) => T(l, "Row.Mode");
	public static string RowVirtualPad(Language? l = null) => T(l, "Row.VirtualPad");
	public static string RowModel(Language? l = null) => T(l, "Row.Model");
	public static string RowPower(Language? l = null) => T(l, "Row.Power");
	public static string ValuePluggedIn(Language? l = null) => T(l, "Value.PluggedIn");
	public static string ValueUnplugged(Language? l = null) => T(l, "Value.Unplugged");
	public static string ValueGameConnected(Language? l = null) => T(l, "Value.GameConnected");
	public static string ValueNoGame(Language? l = null) => T(l, "Value.NoGame");
	public static string QuickActions(Language? l = null) => T(l, "Quick.Actions");
	public static string QuickControls(Language? l = null) => T(l, "Quick.Controls");

	// Overview by state: the status pill, the one main action, the hero, the live panel and the health chips.
	public static string OverviewReady(Language? l = null) => T(l, "Overview.Ready");
	public static string OverviewWaitingController(Language? l = null) => T(l, "Overview.WaitingController");
	public static string OverviewPlaying(string game, Language? l = null) => T(l, "Overview.Playing", A("game", game));
	public static string PairController(Language? l = null) => T(l, "Pair.Controller");
	public static string FinishSetup(Language? l = null) => T(l, "Finish.Setup");
	public static string SetupNeeded(Language? l = null) => T(l, "Setup.Needed");
	public static string HeroGame(Language? l = null) => T(l, "Hero.Game");
	public static string HeroNoController(Language? l = null) => T(l, "Hero.NoController");
	public static string ReasonRadioOff(Language? l = null) => T(l, "Reason.RadioOff");
	public static string ReasonRadioMissing(Language? l = null) => T(l, "Reason.RadioMissing");
	public static string ReasonUsbOnly(Language? l = null) => T(l, "Reason.UsbOnly");
	public static string ReasonControllerOff(Language? l = null) => T(l, "Reason.ControllerOff");
	public static string HintPressPs(Language? l = null) => T(l, "Hint.PressPs");
	public static string LiveTitle(Language? l = null) => T(l, "Live.Title");
	public static string LiveIdle(Language? l = null) => T(l, "Live.Idle");
	// The live card's two views (the header's switch), the delay headline's caption and the chart's span.
	public static string LiveViewLink(Language? l = null) => T(l, "Live.ViewLink");
	public static string LiveViewSignals(Language? l = null) => T(l, "Live.ViewSignals");
	public static string LiveTypicalDelay(Language? l = null) => T(l, "Live.TypicalDelay");
	public static string LiveLast60s(Language? l = null) => T(l, "Live.Last60s");
	public static string LiveLast10s(Language? l = null) => T(l, "Live.Last10s");
	public static string LiveMicShort(Language? l = null) => T(l, "Live.MicShort");
	public static string LiveSignalsTip(Language? l = null) => T(l, "Live.SignalsTip");
	public static string DelayMs(double ms, Language? l = null) => T(l, "Delay.Ms", A("ms", ms, "F1"));
	public static string LiveTriggers(Language? l = null) => T(l, "Live.Triggers");
	public static string LiveBridgeUptime(string duration, Language? l = null) => T(l, "Live.BridgeUptime", A("duration", duration));
	public static string LiveConnectedFor(string duration, Language? l = null) => T(l, "Live.ConnectedFor", A("duration", duration));
	public static string LiveSpeaker(Language? l = null) => T(l, "Live.Speaker");
	public static string LiveMic(Language? l = null) => T(l, "Live.Mic");
	public static string LiveMuted(Language? l = null) => T(l, "Live.Muted");
	public static string LiveOnVolume(double percent, Language? l = null) => T(l, "Live.OnVolume", A("percent", percent, "F0"));
	public static string ControlsNeedController(Language? l = null) => T(l, "Controls.NeedController");
	public static string TryAction(Language? l = null) => T(l, "Try.Action");
	public static string HealthDriver(Language? l = null) => T(l, "Health.Driver");
	public static string HealthAdapter(Language? l = null) => T(l, "Health.Adapter");
	public static string HealthInstalled(Language? l = null) => T(l, "Health.Installed");
	public static string HealthMissing(Language? l = null) => T(l, "Health.Missing");
	public static string HealthUnknown(Language? l = null) => T(l, "Health.Unknown");
	public static string HealthNoIssue(Language? l = null) => T(l, "Health.NoIssue");
	public static string HealthOn(Language? l = null) => T(l, "Health.On");
	public static string HealthOff(Language? l = null) => T(l, "Health.Off");
	public static string HealthSuitable(Language? l = null) => T(l, "Health.Suitable");
	public static string HealthBorderline(Language? l = null) => T(l, "Health.Borderline");
	public static string HealthUnsuitable(Language? l = null) => T(l, "Health.Unsuitable");
	public static string HealthNotTested(Language? l = null) => T(l, "Health.NotTested");
	public static string HealthError(Language? l = null) => T(l, "Health.Error");
	public static string HealthFix(Language? l = null) => T(l, "Health.Fix");
	public static string HealthStart(Language? l = null) => T(l, "Health.Start");
	public static string HealthRestart(Language? l = null) => T(l, "Health.Restart");
	public static string GroupStartup(Language? l = null) => T(l, "Group.Startup");
	public static string GroupPowerSaving(Language? l = null) => T(l, "Group.PowerSaving");

	// Game modes: supported (native DualSense) and other games
	public static string LightFromGame(Language? l = null) => T(l, "Light.FromGame");
	public static string LightFromGameHint(Language? l = null) => T(l, "Light.FromGameHint");
	public static string SupportedGameList(Language? l = null) => T(l, "Supported.GameList");
	// The "Installed" view's list title: everything on this PC, supported or not.
	public static string SearchGames(Language? l = null) => T(l, "Search.Games");
	public static string GameCount(double count, Language? l = null) => T(l, "Game.Count", A("count", count, "F0"));
	public static string NoMatchingGames(Language? l = null) => T(l, "No.MatchingGames");

	// Online update of the supported games list.
	public static string UpdateGameList(Language? l = null) => T(l, "Update.GameList");
	public static string UpdatingGameList(Language? l = null) => T(l, "Updating.GameList");
	public static string GameListUpToDate(Language? l = null) => T(l, "Game.ListUpToDate");
	public static string GameListAdded(double added, Language? l = null) => T(l, "Game.ListAdded", A("added", added, "F0"));
	public static string GameListFailed(string reason, Language? l = null) => T(l, "Game.ListFailed", A("reason", reason));
	public static string GameListOffline(Language? l = null) => T(l, "Game.ListOffline");
	public static string GameListTimeout(Language? l = null) => T(l, "Game.ListTimeout");
	public static string GameListInvalid(Language? l = null) => T(l, "Game.ListInvalid");
	public static string GameListUnsupported(Language? l = null) => T(l, "Game.ListUnsupported");
	public static string GameListSaveFailed(Language? l = null) => T(l, "Game.ListSaveFailed");
	public static string AutoUpdateGameList(Language? l = null) => T(l, "Auto.UpdateGameList");
	public static string NotifyGameListTitle(Language? l = null) => T(l, "Notify.GameListTitle");
	public static string NotifyGameListBody(double added, Language? l = null) => T(l, "Notify.GameListBody", A("added", added, "F0"));

	// "72 oyun · güncellendi 9 Kas 2026"; without the date when updated is not an ISO date.
	public static string GameListInfo(double count, string updated, Language? l = null) =>
		DateTime.TryParseExact(updated, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime date)
			? T(l, "Game.ListInfo.Updated", A("count", count, "F0"), A("date", date, "d MMM yyyy"))
			: T(l, "Game.ListInfo", A("count", count, "F0"));
	public static string BadgeHaptics(Language? l = null) => T(l, "Badge.Haptics");
	public static string BadgeTriggers(Language? l = null) => T(l, "Badge.Triggers");
	public static string MarkUserAdded(Language? l = null) => T(l, "Mark.UserAdded");
	public static string AddGame(Language? l = null) => T(l, "Add.Game");
	public static string AddGameFromFile(Language? l = null) => T(l, "Add.GameFromFile");
	public static string AddGameRunningHeader(Language? l = null) => T(l, "Add.GameRunningHeader");
	public static string AddGameNoRunning(Language? l = null) => T(l, "Add.GameNoRunning");
	public static string RemoveGame(Language? l = null) => T(l, "Remove.Game");
	public static string GameListHint(Language? l = null) => T(l, "Game.ListHint");

	// The removed DualSense Mode switch (obsolete: GameModeText.Mode on the games pages still reads these).
	public static string ModeSupported(Language? l = null) => T(l, "Mode.Supported");
	public static string ModeUnsupported(Language? l = null) => T(l, "Mode.Unsupported");
	public static string NoGameYet(Language? l = null) => T(l, "No.GameYet");
	public static string RumbleFeelLabel(Language? l = null) => T(l, "Rumble.FeelLabel");
	public static string RumbleFeelSoft(Language? l = null) => T(l, "Rumble.FeelSoft");
	public static string RumbleFeelPunchy(Language? l = null) => T(l, "Rumble.FeelPunchy");
	public static string RumbleFeelHint(Language? l = null) => T(l, "Rumble.FeelHint");
	public static string FallbackTriggerLabel(Language? l = null) => T(l, "Fallback.TriggerLabel");
	public static string FallbackTriggerHint(Language? l = null) => T(l, "Fallback.TriggerHint");
	public static string FallbackStrength(Language? l = null) => T(l, "Fallback.Strength");
	public static string TestFallbackTrigger(Language? l = null) => T(l, "Test.FallbackTrigger");

	// The controller page: test, sticks, triggers, remap & gyro
	public static string NavController(Language? l = null) => T(l, "Nav.Controller");
	public static string ControllerIntro(Language? l = null) => T(l, "Controller.Intro");
	public static string LivePreviewNeedsBridge(Language? l = null) => T(l, "Live.PreviewNeedsBridge");
	public static string DriftCalibration(Language? l = null) => T(l, "Drift.Calibration");
	public static string CalibrateAction(Language? l = null) => T(l, "Calibrate.Action");
	public static string ResetToDefault(Language? l = null) => T(l, "Reset.ToDefault");

	// Power, latency, diagnostics
	public static string PowerOffWhenIdleHint(Language? l = null) => T(l, "Power.OffWhenIdleHint");
	public static string TurnOffController(Language? l = null) => T(l, "Turn.OffController");
	public static string PowerOffRunning(Language? l = null) => T(l, "Power.OffRunning");
	public static string LatencyHint(Language? l = null) => T(l, "Latency.Hint");
	public static string DiagnosticsRunning(Language? l = null) => T(l, "Diagnostics.Running");

	// Notifications (toasts) and the tray icon
	public static string NotifyConnectedTitle(Language? l = null) => T(l, "Notify.ConnectedTitle");
	public static string NotifyConnectedBody(string name, Language? l = null) => T(l, "Notify.ConnectedBody", A("name", name));
	public static string NotifyDisconnectedTitle(Language? l = null) => T(l, "Notify.DisconnectedTitle");
	public static string NotifyDisconnectedBody(Language? l = null) => T(l, "Notify.DisconnectedBody");
	public static string NotifyBatteryLowTitle(Language? l = null) => T(l, "Notify.BatteryLowTitle");
	public static string NotifyBatteryLowBody(double percent, Language? l = null) => T(l, "Notify.BatteryLowBody", A("percent", percent, "F0"));
	public static string NotifyBatteryCriticalTitle(Language? l = null) => T(l, "Notify.BatteryCriticalTitle");
	public static string NotifyBatteryCriticalBody(double percent, Language? l = null) => T(l, "Notify.BatteryCriticalBody", A("percent", percent, "F0"));
	public static string NotifyChargedTitle(Language? l = null) => T(l, "Notify.ChargedTitle");
	public static string NotifyChargedBody(Language? l = null) => T(l, "Notify.ChargedBody");
	public static string NotifySteamInputTitle(Language? l = null) => T(l, "Notify.SteamInputTitle");
	public static string NotifySteamInputBody(Language? l = null) => T(l, "Notify.SteamInputBody");
	public static string NotifyPoweredOffTitle(Language? l = null) => T(l, "Notify.PoweredOffTitle");
	public static string NotifyPoweredOffBody(Language? l = null) => T(l, "Notify.PoweredOffBody");
	public static string NotifyBridgeErrorTitle(Language? l = null) => T(l, "Notify.BridgeErrorTitle");
	// Short reasons for the bridge error toast (the full text is on the Overview page).
	public static string NotifyBridgeNoIdentity(Language? l = null) => T(l, "Notify.BridgeNoIdentity");
	public static string NotifyBridgeIdentityUnreadable(Language? l = null) => T(l, "Notify.BridgeIdentityUnreadable");
	public static string NotifyBridgeUsbIpMissing(Language? l = null) => T(l, "Notify.BridgeUsbIpMissing");
	public static string NotifyBridgeAttachFailed(Language? l = null) => T(l, "Notify.BridgeAttachFailed");
	public static string NotifyBridgeOpenFailed(Language? l = null) => T(l, "Notify.BridgeOpenFailed");
	public static string NotifyBridgeLost(Language? l = null) => T(l, "Notify.BridgeLost");
	public static string NotifyBridgeOther(Language? l = null) => T(l, "Notify.BridgeOther");
	public static string NotifyHidHideTitle(Language? l = null) => T(l, "Notify.HidHideTitle");
	public static string NotifyHidHideBody(Language? l = null) => T(l, "Notify.HidHideBody");
	public static string NotifyTrayTitle(Language? l = null) => T(l, "Notify.TrayTitle");
	public static string NotifyTrayBody(Language? l = null) => T(l, "Notify.TrayBody");
	public static string NotifyClose(Language? l = null) => T(l, "Notify.Close");
	public static string GroupNotifications(Language? l = null) => T(l, "Group.Notifications");
	public static string NotificationsShow(Language? l = null) => T(l, "Notifications.Show");
	public static string NotificationsShowHint(Language? l = null) => T(l, "Notifications.ShowHint");
	public static string NotifyConnectionSetting(Language? l = null) => T(l, "Notify.ConnectionSetting");
	public static string NotifyConnectionHint(Language? l = null) => T(l, "Notify.ConnectionHint");
	public static string NotifyBatterySetting(Language? l = null) => T(l, "Notify.BatterySetting");
	public static string NotifyBatteryHint(Language? l = null) => T(l, "Notify.BatteryHint");
	public static string NotifyGameSetting(Language? l = null) => T(l, "Notify.GameSetting");
	public static string NotifyGameHint(Language? l = null) => T(l, "Notify.GameHint");
	public static string NotifySteamInputSetting(Language? l = null) => T(l, "Notify.SteamInputSetting");
	public static string NotifySteamInputHint(Language? l = null) => T(l, "Notify.SteamInputHint");
	public static string NotifyPowerOffSetting(Language? l = null) => T(l, "Notify.PowerOffSetting");
	public static string NotifyPowerOffHint(Language? l = null) => T(l, "Notify.PowerOffHint");
	public static string NotifyErrorsSetting(Language? l = null) => T(l, "Notify.ErrorsSetting");
	public static string NotifyErrorsHint(Language? l = null) => T(l, "Notify.ErrorsHint");
	public static string NotifyHidHideSetting(Language? l = null) => T(l, "Notify.HidHideSetting");
	public static string NotifyHidHideHint(Language? l = null) => T(l, "Notify.HidHideHint");
	public static string QuietWhilePlaying(Language? l = null) => T(l, "Quiet.WhilePlaying");
	public static string QuietWhilePlayingHint(Language? l = null) => T(l, "Quiet.WhilePlayingHint");
	public static string KeepInTray(Language? l = null) => T(l, "Keep.InTray");
	public static string KeepInTrayHint(Language? l = null) => T(l, "Keep.InTrayHint");
	public static string TrayOpen(Language? l = null) => T(l, "Tray.Open");
	public static string TrayNotificationsOff(Language? l = null) => T(l, "Tray.NotificationsOff");
	public static string TrayNotificationsOn(Language? l = null) => T(l, "Tray.NotificationsOn");
	public static string TrayExit(Language? l = null) => T(l, "Tray.Exit");
	public static string TrayNoController(Language? l = null) => T(l, "Tray.NoController");
	public static string TrayBattery(double percent, Language? l = null) => T(l, "Tray.Battery", A("percent", percent, "F0"));
}
