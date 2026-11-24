// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;

namespace Kopru.Core.Localization;

// User-facing texts of desktop mode (the controller drives Windows) and gyro to mouse.
public static partial class Text
{
	public static string DesktopModeSection(Language? l = null) => T(l, "Desktop.ModeSection");

	public static string DesktopModeSwitch(Language? l = null) => T(l, "Desktop.ModeSwitch");

	public static string DesktopModeHint(Language? l = null) => T(l, "Desktop.ModeHint");

	public static string DesktopAutoOff(Language? l = null) => T(l, "Desktop.AutoOff");

	public static string DesktopAutoOffHint(Language? l = null) => T(l, "Desktop.AutoOffHint");

	public static string DesktopPointerSpeed(Language? l = null) => T(l, "Desktop.PointerSpeed");

	public static string DesktopPointerAcceleration(Language? l = null) => T(l, "Desktop.PointerAcceleration");

	public static string DesktopTouchpadSpeed(Language? l = null) => T(l, "Desktop.TouchpadSpeed");

	public static string DesktopScrollSpeed(Language? l = null) => T(l, "Desktop.ScrollSpeed");

	public static string DesktopOnTitle(Language? l = null) => T(l, "Desktop.OnTitle");

	public static string DesktopOffTitle(Language? l = null) => T(l, "Desktop.OffTitle");

	public static string DesktopOnBody(Language? l = null) => T(l, "Desktop.OnBody");

	public static string DesktopOffBody(Language? l = null) => T(l, "Desktop.OffBody");

	// Mouse mode switched itself off (the toast's title when the game's name is not known).
	public static string DesktopAutoOffTitle(Language? l = null) => T(l, "Desktop.AutoOffTitle");

	// The toast under the game's name: Mouse Mode switched itself off for that game.
	public static string DesktopOffGameBody(Language? l = null) => T(l, "Desktop.OffGameBody");

	// Mouse mode switched itself off for a game that started using the controller (its name not known).
	public static string DesktopOffGameStartedBody(Language? l = null) => T(l, "Desktop.OffGameStartedBody");

	public static string TrayDesktopMode(Language? l = null) => T(l, "Tray.DesktopMode");

	public static string ShortcutKeysDesktop(Language? l = null) => T(l, "Shortcut.KeysDesktop");

	public static string ShortcutDoesDesktop(Language? l = null) => T(l, "Shortcut.DoesDesktop");

	public static string GyroTargetLabel(Language? l = null) => T(l, "Gyro.TargetLabel");

	public static string GyroTargetHint(Language? l = null) => T(l, "Gyro.TargetHint");

	public static string GyroTargetName(GyroTarget target, Language? l = null) => target switch
	{
		GyroTarget.Mouse => T(l, "Gyro.TargetName.Mouse"),
		_ => T(l, "Gyro.TargetName.RightStick")
	};
}
