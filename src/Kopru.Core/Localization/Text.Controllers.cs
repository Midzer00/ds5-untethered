// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the remembered controllers (their names, the Settings list) and the controller shortcuts tile.
public static partial class Text
{
	// Quick settings tile: the controller shortcuts switch and its button to the shortcuts list.
	public static string QuickShortcuts(Language? l = null) => T(l, "Quick.Shortcuts");

	public static string QuickShortcutsTip(Language? l = null) => T(l, "Quick.ShortcutsTip");

	public static string QuickEditShortcuts(Language? l = null) => T(l, "Quick.EditShortcuts");

	// The Settings section listing every controller the app has seen.
	public static string ControllersSection(Language? l = null) => T(l, "Controllers.Section");

	public static string ControllersHint(Language? l = null) => T(l, "Controllers.Hint");

	public static string ControllersEmpty(Language? l = null) => T(l, "Controllers.Empty");

	public static string ControllerRename(Language? l = null) => T(l, "Controller.Rename");

	public static string ControllerNameHint(Language? l = null) => T(l, "Controller.NameHint");

	public static string ControllerConnectedNow(Language? l = null) => T(l, "Controller.ConnectedNow");

	public static string ControllerLastSeen(string ago, Language? l = null) => T(l, "Controller.LastSeen", A("ago", ago));

	public static string AgoJustNow(Language? l = null) => T(l, "Ago.JustNow");

	public static string AgoMinutes(double minutes, Language? l = null) => T(l, "Ago.Minutes", A("minutes", minutes, "F0"));

	public static string AgoHours(double hours, Language? l = null) => T(l, "Ago.Hours", A("hours", hours, "F0"));

	public static string AgoDays(double days, Language? l = null) => T(l, "Ago.Days", A("days", days, "F0"));

	public static string ControllerLastBattery(double percent, Language? l = null) => T(l, "Controller.LastBattery", A("percent", percent, "F0"));

	public static string ControllerProfileLine(string profile, Language? l = null) => T(l, "Controller.ProfileLine", A("profile", profile));

	public static string ControllerForget(Language? l = null) => T(l, "Controller.Forget");

	public static string ControllerForgetCancel(Language? l = null) => T(l, "Controller.ForgetCancel");

	public static string ControllerForgetTip(Language? l = null) => T(l, "Controller.ForgetTip");

	public static string ControllerForgetConnectedTip(Language? l = null) => T(l, "Controller.ForgetConnectedTip");

	public static string ControllerForgetAsk(string name, Language? l = null) => T(l, "Controller.ForgetAsk", A("name", name));

	public static string ControllerTabDeviceTests(Language? l = null) => T(l, "Controller.TabDeviceTests");

	public static string ControllerTabButtons(Language? l = null) => T(l, "Controller.TabButtons");

	public static string ControllerTabGyroSticks(Language? l = null) => T(l, "Controller.TabGyroSticks");
}
