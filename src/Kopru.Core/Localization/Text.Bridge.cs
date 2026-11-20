// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Strings of the bridge service (state, errors, battery, idle).
public static partial class Text
{
	public static string BridgeStopped(Language? l = null) => Pick(l, $"Durduruldu", $"Stopped");

	public static string BridgeWaiting(Language? l = null) => Pick(l, $"Kumanda bekleniyor", $"Waiting for the controller");

	public static string BridgeConnecting(Language? l = null) => Pick(l, $"Bağlanıyor", $"Connecting");

	public static string BridgeRunning(Language? l = null) => Pick(l, $"Çalışıyor", $"Running");

	public static string BridgeError(Language? l = null) => Pick(l, $"Hata", $"Error");

	public static string BridgeNoCapture(Language? l = null) =>
		Pick(l, $"Kumanda kimliği yok: kumandayı kabloyla bağlayıp bir kez yakalama yapın", $"No controller identity: connect the controller by cable and capture it once");

	public static string BridgeCaptureInvalid(Language? l = null) =>
		Pick(l, $"Kumanda kimliği dosyası okunamadı", $"The controller identity file could not be read");

	public static string BridgeUsbIpMissing(Language? l = null) =>
		Pick(l, $"usbip bulunamadı: dağıtımının usbip paketini kur (Kurulum sayfasına bak)", $"usbip not found: install your distribution's usbip package (see the Setup page)");

	public static string BridgePkexecMissing(Language? l = null) =>
		Pick(l, $"pkexec bulunamadı: sanal kumandayı takmak için polkit'i kur", $"pkexec not found: install polkit to plug in the virtual controller");

	public static string BridgeAttachNotAuthorized(Language? l = null) =>
		Pick(l, $"Şifre penceresi kapatıldı; sanal kumanda takılmadı. Yeniden başlatınca tekrar sorulur.", $"The password prompt was closed; the virtual controller was not plugged in. Start it again to be asked again.");

	public static string BridgeAttachFailed(Language? l = null) =>
		Pick(l, $"Sanal kumanda takılamadı (usbip attach)", $"The virtual controller could not be plugged in (usbip attach)");

	public static string BridgeControllerOpenFailed(Language? l = null) =>
		Pick(l, $"Kumanda açılamadı", $"The controller could not be opened");

	public static string BridgeControllerLost(Language? l = null) =>
		Pick(l, $"Kumanda bağlantısı koptu; yeniden bağlanması bekleniyor", $"The controller disconnected; waiting for it to come back");

	public static string BatteryPercent(double percent, Language? l = null) => Pick(l, $"Pil %{percent:F0}", $"Battery {percent:F0}%");

	public static string BatteryCharging(Language? l = null) => Pick(l, $"Şarj oluyor", $"Charging");

	public static string BatteryFull(Language? l = null) => Pick(l, $"Şarj dolu", $"Fully charged");

	public static string BatteryLow(double percent, Language? l = null) =>
		Pick(l, $"Kumandanın pili azaldı (%{percent:F0})", $"The controller's battery is low ({percent:F0}%)");

	public static string IdleTimeout(double minutes, Language? l = null) =>
		Pick(l, $"Kumanda {minutes:F0} dakikadır kullanılmıyor", $"The controller has not been used for {minutes:F0} {En(minutes, "minute", "minutes")}");

	public static string HidingUnavailable(Language? l = null) => Pick(l,
		$"Sanal kumanda başlamadı: HidHide kurulu değil. Kurulum sayfasından kur.",
		$"The virtual controller didn't start: HidHide isn't installed. Install it from the Setup page.");

	public static string HidingFailed(string detail, Language? l = null) => Pick(l,
		$"Kumanda gizlenemedi: {detail}",
		$"The controller could not be hidden: {detail}");
}
