// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;

namespace Kopru.Core.Localization;

// User-facing texts of mouse mode's pointer stick choice and button mapping.
public static partial class Text
{
	public static string DesktopPointerStick(Language? l = null) => T(l, "Desktop.PointerStick");

	public static string DesktopPointerStickHint(Language? l = null) => T(l, "Desktop.PointerStickHint");

	public static string DesktopStickLeft(Language? l = null) => T(l, "Desktop.StickLeft");

	public static string DesktopStickRight(Language? l = null) => T(l, "Desktop.StickRight");

	public static string DesktopButtonsTitle(Language? l = null) => T(l, "Desktop.ButtonsTitle");

	public static string DesktopButtonsNote(Language? l = null) => T(l, "Desktop.ButtonsNote");

	public static string DesktopRightClick(Language? l = null) => T(l, "Desktop.RightClick");

	public static string DesktopRightClickHint(Language? l = null) => T(l, "Desktop.RightClickHint");

	public static string DesktopRightClickOption(TouchpadRightClick mode, Language? l = null) => mode switch
	{
		TouchpadRightClick.RightSide => T(l, "Desktop.RightClickOption.RightSide"),
		TouchpadRightClick.TwoFingers => T(l, "Desktop.RightClickOption.TwoFingers"),
		_ => T(l, "Desktop.RightClickOption.Off")
	};

	// The touchpad's gestures; the tap and press part follows the Right Click choice.
	public static string DesktopTouchpadHint(TouchpadRightClick mode = TouchpadRightClick.Off, Language? l = null)
	{
		string clicks = mode switch
		{
			TouchpadRightClick.RightSide => T(l, "Desktop.TouchpadHint.RightSide"),
			TouchpadRightClick.TwoFingers => T(l, "Desktop.TouchpadHint.TwoFingers"),
			_ => T(l, "Desktop.TouchpadHint.Off")
		};
		return T(l, "Desktop.TouchpadHint", A("clicks", clicks));
	}

	public static string DesktopActionName(MouseAction action, Language? l = null) => action switch
	{
		MouseAction.LeftClick => T(l, "Desktop.ActionName.LeftClick"),
		MouseAction.RightClick => T(l, "Desktop.ActionName.RightClick"),
		MouseAction.MiddleClick => T(l, "Desktop.ActionName.MiddleClick"),
		MouseAction.Enter => "Enter",
		MouseAction.Escape => "Esc",
		MouseAction.Tab => "Tab",
		MouseAction.Space => T(l, "Desktop.ActionName.Space"),
		MouseAction.Backspace => T(l, "Desktop.ActionName.Backspace"),
		MouseAction.Delete => "Delete",
		MouseAction.ArrowUp => T(l, "Desktop.ActionName.ArrowUp"),
		MouseAction.ArrowDown => T(l, "Desktop.ActionName.ArrowDown"),
		MouseAction.ArrowLeft => T(l, "Desktop.ActionName.ArrowLeft"),
		MouseAction.ArrowRight => T(l, "Desktop.ActionName.ArrowRight"),
		MouseAction.PageUp => "Page Up",
		MouseAction.PageDown => "Page Down",
		MouseAction.WindowsKey => T(l, "Desktop.ActionName.WindowsKey"),
		MouseAction.OnScreenKeyboard => T(l, "Desktop.ActionName.OnScreenKeyboard"),
		MouseAction.NextWindow => T(l, "Desktop.ActionName.NextWindow"),
		MouseAction.PreviousWindow => T(l, "Desktop.ActionName.PreviousWindow"),
		MouseAction.Back => T(l, "Desktop.ActionName.Back"),
		MouseAction.Forward => T(l, "Desktop.ActionName.Forward"),
		MouseAction.Copy => T(l, "Desktop.ActionName.Copy"),
		MouseAction.Paste => T(l, "Desktop.ActionName.Paste"),
		MouseAction.VolumeUp => T(l, "Desktop.ActionName.VolumeUp"),
		MouseAction.VolumeDown => T(l, "Desktop.ActionName.VolumeDown"),
		MouseAction.MuteSound => T(l, "Desktop.ActionName.MuteSound"),
		MouseAction.PlayPause => T(l, "Desktop.ActionName.PlayPause"),
		MouseAction.NextTrack => T(l, "Desktop.ActionName.NextTrack"),
		MouseAction.PreviousTrack => T(l, "Desktop.ActionName.PreviousTrack"),
		_ => T(l, "Desktop.ActionName.None")
	};
}
