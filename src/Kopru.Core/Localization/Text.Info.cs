// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the controller info card, reconnect and the USB/Bluetooth switch.
public static partial class Text
{
	// Card and field labels (Title Case).
	public static string InfoTitle(Language? l = null) => T(l, "Info.Title");
	public static string InfoModel(Language? l = null) => T(l, "Info.Model");
	public static string InfoConnection(Language? l = null) => T(l, "Info.Connection");
	public static string InfoFirmware(Language? l = null) => T(l, "Info.Firmware");
	public static string InfoHardware(Language? l = null) => T(l, "Info.Hardware");
	public static string InfoBuildDate(Language? l = null) => T(l, "Info.BuildDate");
	public static string InfoAddress(Language? l = null) => T(l, "Info.Address");
	public static string InfoConnectedFor(Language? l = null) => T(l, "Info.ConnectedFor");
	public static string InfoBattery(Language? l = null) => T(l, "Info.Battery");
	public static string InfoUnknown(Language? l = null) => T(l, "Info.Unknown");
	public static string InfoCopy(Language? l = null) => T(l, "Info.Copy");
	public static string InfoCopied(Language? l = null) => T(l, "Info.Copied");
	public static string ReconnectController(Language? l = null) => T(l, "Reconnect.Controller");

	// Descriptions (sentence case).
	public static string InfoHint(Language? l = null) => T(l, "Info.Hint");

	public static string InfoNotConnected(Language? l = null) => T(l, "Info.NotConnected");

	public static string ReconnectHint(Language? l = null) => T(l, "Reconnect.Hint");

	// Settings → About: "DualSense · Bluetooth · firmware 06.30 · connected 5 min 03 s".
	public static string InfoCompact(string model, string connection, string firmware, string duration, Language? l = null) => T(l, "Info.Compact", A("model", model), A("connection", connection), A("firmware", firmware), A("duration", duration));

	// Durations ("connected for").
	public static string DurationSeconds(int seconds, Language? l = null) => T(l, "Duration.Seconds", A("seconds", seconds));
	public static string DurationMinutesSeconds(int minutes, int seconds, Language? l = null) =>
		T(l, "Duration.MinutesSeconds", A("minutes", minutes), A("seconds", seconds, "00"));
	public static string DurationHoursMinutes(int hours, int minutes, Language? l = null) =>
		T(l, "Duration.HoursMinutes", A("hours", hours), A("minutes", minutes, "00"));
	public static string DurationDaysHours(int days, int hours, Language? l = null) => T(l, "Duration.DaysHours", A("days", days), A("hours", hours));

	// The toast when Bluetooth is lost while the controller is on a cable, and the stuck connection chip.
	public static string NotifyCableTitle(Language? l = null) => T(l, "Notify.CableTitle");

	public static string NotifyCableBody(Language? l = null) => T(l, "Notify.CableBody");

	// Connection value while the controller stays on Bluetooth with its charging cable plugged in.
	public static string ConnectionBluetoothCable(Language? l = null) => T(l, "Connection.BluetoothCable");

	public static string HealthBridgeStuck(Language? l = null) => T(l, "Health.BridgeStuck");
}
