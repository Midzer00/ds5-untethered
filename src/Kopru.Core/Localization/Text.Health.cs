// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the health chips, connection states, "why disabled" reasons, last-known battery and relative time.
public static partial class Text
{
	// Chip names (Title Case).
	public static string HealthChipDriver(Language? l = null) => T(l, "Health.ChipDriver");
	public static string HealthChipHiding(Language? l = null) => T(l, "Health.ChipHiding");
	public static string HealthChipSteamInput(Language? l = null) => T(l, "Health.ChipSteamInput");
	public static string HealthChipAdapter(Language? l = null) => T(l, "Health.ChipAdapter");
	public static string HealthChipBridge(Language? l = null) => T(l, "Health.ChipBridge");

	// Connection states (Title Case labels).
	public static string ConnectionBluetoothOff(Language? l = null) => T(l, "Connection.BluetoothOff");
	public static string ConnectionNoRadio(Language? l = null) => T(l, "Connection.NoRadio");
	public static string ConnectionNeverPaired(Language? l = null) => T(l, "Connection.NeverPaired");
	public static string ConnectionPairedNotConnected(Language? l = null) => T(l, "Connection.PairedNotConnected");
	public static string ConnectionOnBluetooth(Language? l = null) => T(l, "Connection.OnBluetooth");
	public static string ConnectionOnUsb(Language? l = null) => T(l, "Connection.OnUsb");

	// Connection explanations (sentence case).
	public static string ConnectionBluetoothOffReason(Language? l = null) => T(l, "Connection.BluetoothOffReason");

	public static string ConnectionNoRadioReason(Language? l = null) => T(l, "Connection.NoRadioReason");

	public static string ConnectionNeverPairedReason(Language? l = null) => T(l, "Connection.NeverPairedReason");

	public static string ConnectionPairedNotConnectedReason(Language? l = null) => T(l, "Connection.PairedNotConnectedReason");

	public static string ConnectionBluetoothReason(Language? l = null) => T(l, "Connection.BluetoothReason");

	public static string ConnectionUsbReason(Language? l = null) => T(l, "Connection.UsbReason");

	// Driver chip.
	public static string HealthDriverOk(string version, Language? l = null) => T(l, "Health.DriverOk", A("version", version));

	public static string HealthDriverMissing(Language? l = null) => T(l, "Health.DriverMissing");

	public static string HealthDriverServiceMissing(Language? l = null) => T(l, "Health.DriverServiceMissing");

	public static string HealthRestartPending(Language? l = null) => T(l, "Health.RestartPending");

	public static string VersionUnknown(Language? l = null) => T(l, "Version.Unknown");

	// Hiding chip.
	public static string HealthHidingMissing(Language? l = null) => T(l, "Health.HidingMissing");

	public static string HealthHidingDriverMissing(Language? l = null) => T(l, "Health.HidingDriverMissing");

	public static string HealthHidingReady(Language? l = null) => T(l, "Health.HidingReady");

	public static string HealthHidingActive(Language? l = null) => T(l, "Health.HidingActive");

	public static string HealthHidingAppNotAllowed(Language? l = null) => T(l, "Health.HidingAppNotAllowed");

	public static string HealthHidingNotHidden(Language? l = null) => T(l, "Health.HidingNotHidden");

	// Steam Input chip.
	public static string HealthSteamUnknown(Language? l = null) => T(l, "Health.SteamUnknown");

	public static string HealthSteamNotChecked(Language? l = null) => T(l, "Health.SteamNotChecked");

	public static string HealthSteamNotInstalled(Language? l = null) => T(l, "Health.SteamNotInstalled");

	public static string HealthSteamOff(Language? l = null) => T(l, "Health.SteamOff");

	public static string HealthSteamOnGlobally(Language? l = null) => T(l, "Health.SteamOnGlobally");

	public static string HealthSteamOnForApp(Language? l = null) => T(l, "Health.SteamOnForApp");

	// Adapter chip.
	public static string HealthAdapterNeverTested(Language? l = null) => T(l, "Health.AdapterNeverTested");

	public static string HealthAdapterNoData(Language? l = null) => T(l, "Health.AdapterNoData");

	public static string HealthAdapterResult(string verdict, string when, Language? l = null) => T(l, "Health.AdapterResult", A("verdict", verdict), A("when", when));

	// Bridge chip.
	public static string HealthBridgeStopped(Language? l = null) => T(l, "Health.BridgeStopped");

	public static string HealthBridgeWaiting(Language? l = null) => T(l, "Health.BridgeWaiting");

	public static string HealthBridgeConnecting(Language? l = null) => T(l, "Health.BridgeConnecting");

	public static string HealthBridgeRunning(Language? l = null) => T(l, "Health.BridgeRunning");

	public static string HealthBridgeLastError(string reason, Language? l = null) => T(l, "Health.BridgeLastError", A("reason", reason));

	// "Why disabled".
	public static string CannotStartDriver(Language? l = null) => T(l, "Cannot.StartDriver");

	public static string CannotStartHidHide(Language? l = null) => T(l, "Cannot.StartHidHide");

	public static string CannotStartNoRadio(Language? l = null) => T(l, "Cannot.StartNoRadio");

	public static string CannotStartBluetoothOff(Language? l = null) => T(l, "Cannot.StartBluetoothOff");

	public static string CannotStartNotPaired(Language? l = null) => T(l, "Cannot.StartNotPaired");

	public static string CannotStartUsb(Language? l = null) => T(l, "Cannot.StartUsb");

	public static string CannotTestNoController(Language? l = null) => T(l, "Cannot.TestNoController");

	public static string CannotTestUsb(Language? l = null) => T(l, "Cannot.TestUsb");

	// Last-known battery, e.g. "Son: %62 · 2 sa önce".
	public static string LastKnownBattery(int percent, string when, Language? l = null) => T(l, "Last.KnownBattery", A("percent", percent), A("when", when));

	public static string LastKnownBatteryCharging(int percent, string when, Language? l = null) => T(l, "Last.KnownBatteryCharging", A("percent", percent), A("when", when));

	// Fix buttons (Title Case) and short chip states.
	public static string FixInstallDriver(Language? l = null) => T(l, "Fix.InstallDriver");
	public static string FixInstallHidHide(Language? l = null) => T(l, "Fix.InstallHidHide");
	public static string FixSteamInputHelp(Language? l = null) => T(l, "Fix.SteamInputHelp");
	public static string FixRunAdapterTest(Language? l = null) => T(l, "Fix.RunAdapterTest");
	public static string FixOpenBluetooth(Language? l = null) => T(l, "Fix.OpenBluetooth");
	public static string FixRestartBridge(Language? l = null) => T(l, "Fix.RestartBridge");
	public static string HealthLevelOk(Language? l = null) => T(l, "Health.LevelOk");
	public static string HealthLevelWarning(Language? l = null) => T(l, "Health.LevelWarning");
	public static string HealthLevelError(Language? l = null) => T(l, "Health.LevelError");
	public static string HealthLevelUnknown(Language? l = null) => T(l, "Health.LevelUnknown");

	// Relative time.
	public static string TimeJustNow(Language? l = null) => T(l, "Time.JustNow");
	public static string TimeMinutesAgo(int minutes, Language? l = null) => T(l, "Time.MinutesAgo", A("minutes", minutes));
	public static string TimeHoursAgo(int hours, Language? l = null) => T(l, "Time.HoursAgo", A("hours", hours));
	public static string TimeYesterday(Language? l = null) => T(l, "Time.Yesterday");
	public static string TimeDaysAgo(int days, Language? l = null) => T(l, "Time.DaysAgo", A("days", days));
}
