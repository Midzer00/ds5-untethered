// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the remembered controllers (their names, the Settings list) and the controller shortcuts tile.
public static partial class Text
{
	// Quick settings tile: the controller shortcuts switch and its button to the shortcuts list.
	public static string QuickShortcuts(Language? l = null) => Pick(l, $"Kumanda Kısayolları", $"Controller Shortcuts");

	public static string QuickShortcutsTip(Language? l = null) => Pick(l,
		$"PS tuşunu basılı tutup başka bir tuşa basarak profil, haptik gücü ve DualSense Modu gibi şeyleri kumandadan değiştirirsin.",
		$"Hold PS and press another button to change the profile, haptics strength, DualSense Mode and more from the controller.");

	public static string QuickEditShortcuts(Language? l = null) => Pick(l, $"Kısayolları Düzenle", $"Edit Shortcuts");

	// The Settings section listing every controller the app has seen.
	public static string ControllersSection(Language? l = null) => Pick(l, $"Kumandalar", $"Controllers");

	public static string ControllersHint(Language? l = null) => Pick(l,
		$"Bağladığın kumandalar burada kalır. İstediğine bir ad verebilirsin; ad her yerde görünür.",
		$"Controllers you connected stay here. You can give any of them a name; it shows everywhere.");

	public static string ControllersEmpty(Language? l = null) => Pick(l,
		$"Henüz kumanda bağlanmadı.",
		$"No controller has connected yet.");

	public static string ControllerRename(Language? l = null) => Pick(l, $"Adını Değiştir", $"Rename");

	public static string ControllerNameHint(Language? l = null) => Pick(l,
		$"Enter kaydeder, Esc vazgeçer.",
		$"Enter saves, Esc cancels.");

	public static string ControllerConnectedNow(Language? l = null) => Pick(l, $"Bağlı", $"Connected");

	public static string ControllerLastSeen(string ago, Language? l = null) => Pick(l, $"Son Görülme: {ago}", $"Last Seen: {ago}");

	public static string AgoJustNow(Language? l = null) => Pick(l, $"az önce", $"just now");

	public static string AgoMinutes(double minutes, Language? l = null) => Pick(l, $"{minutes:F0} dk önce", $"{minutes:F0} min ago");

	public static string AgoHours(double hours, Language? l = null) => Pick(l, $"{hours:F0} sa önce", $"{hours:F0} h ago");

	public static string AgoDays(double days, Language? l = null) => Pick(l, $"{days:F0} gün önce", $"{days:F0} days ago");

	public static string ControllerLastBattery(double percent, Language? l = null) => Pick(l, $"Son Pil: %{percent:F0}", $"Last Battery: {percent:F0}%");

	public static string ControllerProfileLine(string profile, Language? l = null) => Pick(l, $"Profil: {profile}", $"Profile: {profile}");

	public static string ControllerForget(Language? l = null) => Pick(l, $"Unut", $"Forget");

	public static string ControllerForgetCancel(Language? l = null) => Pick(l, $"Vazgeç", $"Cancel");

	public static string ControllerForgetTip(Language? l = null) => Pick(l,
		$"Bu kumandanın adını ve kendi profil seçimini siler; tekrar bağlanınca yeni gibi görünür.",
		$"Removes this controller's name and its own profile choice; it shows up as new when it connects again.");

	public static string ControllerForgetConnectedTip(Language? l = null) => Pick(l,
		$"Bağlı bir kumanda unutulamaz; önce kapat.",
		$"A connected controller can't be forgotten; turn it off first.");

	public static string ControllerForgetAsk(string name, Language? l = null) => Pick(l,
		$"{name} unutulsun mu?",
		$"Forget {name}?");
}
