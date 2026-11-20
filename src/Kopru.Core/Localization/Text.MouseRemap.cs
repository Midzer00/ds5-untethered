// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;

namespace Kopru.Core.Localization;

// User-facing texts of mouse mode's pointer stick choice and button mapping.
public static partial class Text
{
	public static string DesktopPointerStick(Language? l = null) => Pick(l, $"İmleç Analoğu", $"Pointer Stick");

	public static string DesktopPointerStickHint(Language? l = null) => Pick(l,
		$"Seçilen analog imleci taşır, diğeri sayfayı kaydırır.",
		$"The chosen stick moves the pointer; the other one scrolls.");

	public static string DesktopStickLeft(Language? l = null) => Pick(l, $"Sol Analog", $"Left Stick");

	public static string DesktopStickRight(Language? l = null) => Pick(l, $"Sağ Analog", $"Right Stick");

	public static string DesktopButtonsTitle(Language? l = null) => Pick(l, $"Tuş Atamaları", $"Button Mapping");

	public static string DesktopButtonsNote(Language? l = null) => Pick(l,
		$"Dokunmatik yüzey, ø tuşu ve mikrofon tuşu değişmez.",
		$"Touchpad, the ø button and Mute stay as they are.");

	public static string DesktopRightClick(Language? l = null) => Pick(l, $"Sağ Tık", $"Right Click");

	public static string DesktopRightClickHint(Language? l = null) => Pick(l,
		$"Dokunmatik yüzeye hangi basışın sağ tık olacağı. Kapalıyken her basış sol tık.",
		$"Which touchpad press right clicks. Off: every press is a left click.");

	public static string DesktopRightClickOption(TouchpadRightClick mode, Language? l = null) => mode switch
	{
		TouchpadRightClick.RightSide => Pick(l, $"Sağ Taraf", $"Right Side"),
		TouchpadRightClick.TwoFingers => Pick(l, $"İki Parmak", $"Two Fingers"),
		_ => Pick(l, $"Kapalı", $"Off")
	};

	// The touchpad's gestures; the tap and press part follows the Right Click choice.
	public static string DesktopTouchpadHint(TouchpadRightClick mode = TouchpadRightClick.Off, Language? l = null)
	{
		string clicks = mode switch
		{
			TouchpadRightClick.RightSide => Pick(l, $"dokunmak sol tık; sol yarıya basmak sol tık, sağ yarıya basmak sağ tık", $"tap to left click; press the left half to left click, the right half to right click"),
			TouchpadRightClick.TwoFingers => Pick(l, $"tek parmakla dokunmak ya da basmak sol tık, iki parmakla sağ tık", $"tap or press with one finger to left click, with two to right click"),
			_ => Pick(l, $"dokunmak ya da basmak sol tık", $"tap or press to left click")
		};
		return Pick(l,
			$"Dokunmatik yüzey: tek parmak imleci taşır, iki parmak yukarı/aşağı kaydırır; {clicks}. ø + Seçenekler fare modunu açıp kapatır.",
			$"Touchpad: one finger moves the pointer, two fingers up/down scroll; {clicks}. ø + Options turns mouse mode on and off.");
	}

	public static string DesktopActionName(MouseAction action, Language? l = null) => action switch
	{
		MouseAction.LeftClick => Pick(l, $"Sol Tık", $"Left Click"),
		MouseAction.RightClick => Pick(l, $"Sağ Tık", $"Right Click"),
		MouseAction.MiddleClick => Pick(l, $"Orta Tık", $"Middle Click"),
		MouseAction.Enter => "Enter",
		MouseAction.Escape => "Esc",
		MouseAction.Tab => "Tab",
		MouseAction.Space => Pick(l, $"Boşluk", $"Space"),
		MouseAction.Backspace => Pick(l, $"Geri Sil", $"Backspace"),
		MouseAction.Delete => "Delete",
		MouseAction.ArrowUp => Pick(l, $"Yukarı Ok", $"Arrow Up"),
		MouseAction.ArrowDown => Pick(l, $"Aşağı Ok", $"Arrow Down"),
		MouseAction.ArrowLeft => Pick(l, $"Sol Ok", $"Arrow Left"),
		MouseAction.ArrowRight => Pick(l, $"Sağ Ok", $"Arrow Right"),
		MouseAction.PageUp => "Page Up",
		MouseAction.PageDown => "Page Down",
		MouseAction.WindowsKey => Pick(l, $"Super Tuşu", $"Super Key"),
		MouseAction.OnScreenKeyboard => Pick(l, $"Ekran Klavyesi", $"Screen Keyboard"),
		MouseAction.NextWindow => Pick(l, $"Sonraki Pencere", $"Next Window"),
		MouseAction.PreviousWindow => Pick(l, $"Önceki Pencere", $"Previous Window"),
		MouseAction.Back => Pick(l, $"Geri", $"Back"),
		MouseAction.Forward => Pick(l, $"İleri", $"Forward"),
		MouseAction.Copy => Pick(l, $"Kopyala", $"Copy"),
		MouseAction.Paste => Pick(l, $"Yapıştır", $"Paste"),
		MouseAction.VolumeUp => Pick(l, $"Sesi Artır", $"Volume Up"),
		MouseAction.VolumeDown => Pick(l, $"Sesi Azalt", $"Volume Down"),
		MouseAction.MuteSound => Pick(l, $"Sesi Kapat", $"Mute Sound"),
		MouseAction.PlayPause => Pick(l, $"Oynat/Duraklat", $"Play/Pause"),
		MouseAction.NextTrack => Pick(l, $"Sonraki Parça", $"Next Track"),
		MouseAction.PreviousTrack => Pick(l, $"Önceki Parça", $"Previous Track"),
		_ => Pick(l, $"Yok", $"None")
	};
}
