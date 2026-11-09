// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the HidHide integration.
public static partial class Text
{
	public static string HidHideNotInstalled(Language? l = null) => Pick(l,
		$"HidHide kurulu değil. Oyunlar gerçek Bluetooth kolu da görebilir (çift giriş).",
		$"HidHide is not installed. Games may also see the real Bluetooth controller (double input).");

	public static string HidHideCommandFailed(string command, string detail, Language? l = null) => Pick(l,
		$"HidHide komutu başarısız ({command}): {detail}",
		$"HidHide command failed ({command}): {detail}");

	public static string HidHideHideFailed(string detail, Language? l = null) => Pick(l,
		$"Gerçek kol gizlenemedi: {detail}",
		$"Could not hide the real controller: {detail}");

	public static string HidHideRestoreFailed(string detail, Language? l = null) => Pick(l,
		$"HidHide ayarları geri alınamadı, sonraki açılışta tekrar denenecek: {detail}",
		$"Could not restore HidHide settings; will retry on next start: {detail}");

	public static string HidHideNoAppPath(Language? l = null) => Pick(l,
		$"Uygulamanın dosya yolu bulunamadı.",
		$"Could not determine the application's file path.");

	public static string HidHideActive(Language? l = null) => Pick(l,
		$"Gerçek kol oyunlardan gizlendi.",
		$"The real controller is hidden from games.");

	public static string HidHideRestored(Language? l = null) => Pick(l,
		$"Gizleme kaldırıldı.",
		$"Hiding removed.");
}
