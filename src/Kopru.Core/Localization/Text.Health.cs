// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the health chips, connection states, "why disabled" reasons, last-known battery and relative time.
public static partial class Text
{
	// Chip names (Title Case).
	public static string HealthChipDriver(Language? l = null) => Pick(l, $"Sürücü", $"Driver");
	public static string HealthChipHiding(Language? l = null) => Pick(l, $"Gizleme", $"Hiding");
	public static string HealthChipSteamInput(Language? l = null) => Pick(l, $"Steam Girişi", $"Steam Input");
	public static string HealthChipAdapter(Language? l = null) => Pick(l, $"Adaptör", $"Adapter");
	public static string HealthChipBridge(Language? l = null) => Pick(l, $"Sanal Kumanda", $"Virtual Controller");

	// Connection states (Title Case labels).
	public static string ConnectionBluetoothOff(Language? l = null) => Pick(l, $"Bluetooth Kapalı", $"Bluetooth Off");
	public static string ConnectionNoRadio(Language? l = null) => Pick(l, $"Bluetooth Yok", $"No Bluetooth");
	public static string ConnectionNeverPaired(Language? l = null) => Pick(l, $"Eşleşmemiş", $"Not Paired");
	public static string ConnectionPairedNotConnected(Language? l = null) => Pick(l, $"Bağlı Değil", $"Not Connected");
	public static string ConnectionOnBluetooth(Language? l = null) => Pick(l, $"Bluetooth ile Bağlı", $"Connected over Bluetooth");
	public static string ConnectionOnUsb(Language? l = null) => Pick(l, $"Kabloyla Bağlı", $"Connected by Cable");

	// Connection explanations (sentence case).
	public static string ConnectionBluetoothOffReason(Language? l = null) => Pick(l,
		$"Bluetooth kapalı. Windows ayarlarından aç.",
		$"Bluetooth is off. Turn it on in Windows settings.");

	public static string ConnectionNoRadioReason(Language? l = null) => Pick(l,
		$"Bu bilgisayarda Bluetooth adaptörü bulunamadı.",
		$"No Bluetooth adapter was found on this PC.");

	public static string ConnectionNeverPairedReason(Language? l = null) => Pick(l,
		$"Windows'ta eşleşmiş bir DualSense yok. Create ve PS tuşlarını ışık yanıp sönene kadar basılı tutup eşleştir.",
		$"No DualSense is paired with Windows. Hold Create and PS until the light blinks, then pair it.");

	public static string ConnectionPairedNotConnectedReason(Language? l = null) => Pick(l,
		$"Kumanda eşleşmiş ama kapalı ya da uykuda. Açmak için PS tuşuna bas.",
		$"The controller is paired but off or asleep. Press the PS button to turn it on.");

	public static string ConnectionBluetoothReason(Language? l = null) => Pick(l,
		$"Kumanda Bluetooth ile bağlı.",
		$"The controller is connected over Bluetooth.");

	public static string ConnectionUsbReason(Language? l = null) => Pick(l,
		$"Kumanda kabloyla bağlı; oyunlar onu zaten kablolu görüyor, sanal kumandaya gerek yok. Kablosuz oynamak için kabloyu çıkar.",
		$"The controller is connected by cable; games already see it as wired, so the virtual controller isn't needed. Unplug it to play wirelessly.");

	// Driver chip.
	public static string HealthDriverOk(string version, Language? l = null) => Pick(l,
		$"usbip-win2 {version} kurulu.",
		$"usbip-win2 {version} is installed.");

	public static string HealthDriverMissing(Language? l = null) => Pick(l,
		$"usbip-win2 kurulu değil; sanal kumanda onsuz çalışamaz.",
		$"usbip-win2 isn't installed; the virtual controller can't run without it.");

	public static string HealthDriverServiceMissing(Language? l = null) => Pick(l,
		$"usbip-win2 kurulu ama sürücü hizmeti yok. Yeniden kur.",
		$"usbip-win2 is installed but its driver service is missing. Reinstall it.");

	public static string HealthRestartPending(Language? l = null) => Pick(l,
		$"Kurulumu bitirmek için bilgisayarı yeniden başlat.",
		$"Restart the PC to finish the installation.");

	public static string VersionUnknown(Language? l = null) => Pick(l, $"(sürüm bilinmiyor)", $"(version unknown)");

	// Hiding chip.
	public static string HealthHidingMissing(Language? l = null) => Pick(l,
		$"HidHide kurulu değil; sanal kumanda onsuz başlamaz, yoksa oyunlar iki kumanda görürdü.",
		$"HidHide isn't installed; the virtual controller won't start without it, or games would see two controllers.");

	public static string HealthHidingDriverMissing(Language? l = null) => Pick(l,
		$"HidHide kurulu ama sürücüsü yok. Yeniden kur.",
		$"HidHide is installed but its driver is missing. Reinstall it.");

	public static string HealthHidingReady(Language? l = null) => Pick(l,
		$"HidHide hazır; sanal kumanda çalışınca gerçek kumanda gizlenir.",
		$"HidHide is ready; the real controller is hidden while the virtual controller runs.");

	public static string HealthHidingActive(Language? l = null) => Pick(l,
		$"Gerçek kumanda diğer uygulamalardan gizli.",
		$"The real controller is hidden from other apps.");

	public static string HealthHidingAppNotAllowed(Language? l = null) => Pick(l,
		$"Uygulama HidHide izin listesine eklenemedi. Sanal kumandayı yeniden başlat.",
		$"The app couldn't be added to the HidHide allow list. Restart the virtual controller.");

	public static string HealthHidingNotHidden(Language? l = null) => Pick(l,
		$"Gerçek kumanda gizlenemedi; oyunlar iki kumanda görebilir.",
		$"The real controller couldn't be hidden; games may see two controllers.");

	// Steam Input chip.
	public static string HealthSteamUnknown(Language? l = null) => Pick(l,
		$"Steam Girişi durumu okunamadı.",
		$"The Steam Input setting couldn't be read.");

	public static string HealthSteamNotChecked(Language? l = null) => Pick(l,
		$"Steam çalışmıyor; kontrol edilmedi.",
		$"Steam isn't running; not checked.");

	public static string HealthSteamNotInstalled(Language? l = null) => Pick(l,
		$"Steam kurulu değil.",
		$"Steam isn't installed.");

	public static string HealthSteamOff(Language? l = null) => Pick(l,
		$"Steam Girişi kapalı; titreşim ve tetikler oyuna ulaşır.",
		$"Steam Input is off; haptics and triggers reach the game.");

	public static string HealthSteamOnGlobally(Language? l = null) => Pick(l,
		$"Steam Girişi PlayStation kumandaları için açık; yerel titreşimi engelleyebilir.",
		$"Steam Input is on for PlayStation controllers; it can block native haptics.");

	public static string HealthSteamOnForApp(Language? l = null) => Pick(l,
		$"Steam Girişi bu oyun için açık; yerel titreşimi engelleyebilir.",
		$"Steam Input is on for this game; it can block native haptics.");

	// Adapter chip.
	public static string HealthAdapterNeverTested(Language? l = null) => Pick(l,
		$"Adaptör henüz test edilmedi.",
		$"The adapter hasn't been tested yet.");

	public static string HealthAdapterNoData(Language? l = null) => Pick(l,
		$"Son testte ölçüm alınamadı. Tekrar dene.",
		$"The last test couldn't measure anything. Try again.");

	public static string HealthAdapterResult(string verdict, string when, Language? l = null) => Pick(l,
		$"Son test: {verdict} · {when}",
		$"Last test: {verdict} · {when}");

	// Bridge chip.
	public static string HealthBridgeStopped(Language? l = null) => Pick(l,
		$"Sanal kumanda durduruldu.",
		$"The virtual controller is stopped.");

	public static string HealthBridgeWaiting(Language? l = null) => Pick(l,
		$"Sanal kumanda, gerçek kumandayı bekliyor.",
		$"The virtual controller is waiting for the real controller.");

	public static string HealthBridgeConnecting(Language? l = null) => Pick(l,
		$"Sanal kumanda bağlanıyor.",
		$"The virtual controller is connecting.");

	public static string HealthBridgeRunning(Language? l = null) => Pick(l,
		$"Sanal kumanda çalışıyor.",
		$"The virtual controller is running.");

	public static string HealthBridgeLastError(string reason, Language? l = null) => Pick(l,
		$"Son hata: {reason}",
		$"Last error: {reason}");

	// "Why disabled".
	public static string CannotStartDriver(Language? l = null) => Pick(l,
		$"Önce usbip-win2 sürücüsünü kur.",
		$"Install the usbip-win2 driver first.");

	public static string CannotStartHidHide(Language? l = null) => Pick(l,
		$"Önce HidHide'ı kur; o olmadan oyunlar iki kumanda görür.",
		$"Install HidHide first; without it games see two controllers.");

	public static string CannotStartNoRadio(Language? l = null) => Pick(l,
		$"Sanal kumanda için bir Bluetooth adaptörü gerekli.",
		$"The virtual controller needs a Bluetooth adapter.");

	public static string CannotStartBluetoothOff(Language? l = null) => Pick(l,
		$"Önce Bluetooth'u aç.",
		$"Turn on Bluetooth first.");

	public static string CannotStartNotPaired(Language? l = null) => Pick(l,
		$"Önce kumandayı Windows'ta eşleştir.",
		$"Pair the controller with Windows first.");

	public static string CannotStartUsb(Language? l = null) => Pick(l,
		$"Kumanda kabloyla bağlıyken sanal kumandaya gerek yok.",
		$"The virtual controller isn't needed while the controller is on a cable.");

	public static string CannotTestNoController(Language? l = null) => Pick(l,
		$"Test için kumandayı Bluetooth ile bağla.",
		$"Connect the controller over Bluetooth to test it.");

	public static string CannotTestUsb(Language? l = null) => Pick(l,
		$"Testler yalnızca Bluetooth bağlantısında çalışır.",
		$"Tests only work over Bluetooth.");

	// Last-known battery, e.g. "Son: %62 · 2 sa önce".
	public static string LastKnownBattery(int percent, string when, Language? l = null) => Pick(l,
		$"Son: %{percent} · {when}",
		$"Last: {percent}% · {when}");

	public static string LastKnownBatteryCharging(int percent, string when, Language? l = null) => Pick(l,
		$"Son: %{percent} (şarj oluyordu) · {when}",
		$"Last: {percent}% (was charging) · {when}");

	// Fix buttons (Title Case) and short chip states.
	public static string FixInstallDriver(Language? l = null) => Pick(l, $"Sürücüyü Kur", $"Install Driver");
	public static string FixInstallHidHide(Language? l = null) => Pick(l, $"HidHide'ı Kur", $"Install HidHide");
	public static string FixSteamInputHelp(Language? l = null) => Pick(l, $"Steam Girişi Yardımı", $"Steam Input Help");
	public static string FixRunAdapterTest(Language? l = null) => Pick(l, $"Adaptörü Test Et", $"Test Adapter");
	public static string FixOpenBluetooth(Language? l = null) => Pick(l, $"Bluetooth'u Aç", $"Turn On Bluetooth");
	public static string FixRestartBridge(Language? l = null) => Pick(l, $"Sanal Kumandayı Yeniden Başlat", $"Restart Virtual Controller");
	public static string HealthLevelOk(Language? l = null) => Pick(l, $"Sorun Yok", $"No Issue");
	public static string HealthLevelWarning(Language? l = null) => Pick(l, $"Dikkat", $"Attention");
	public static string HealthLevelError(Language? l = null) => Pick(l, $"Sorun Var", $"Problem");
	public static string HealthLevelUnknown(Language? l = null) => Pick(l, $"Belirsiz", $"Unknown");

	// Relative time.
	public static string TimeJustNow(Language? l = null) => Pick(l, $"az önce", $"just now");
	public static string TimeMinutesAgo(int minutes, Language? l = null) => Pick(l, $"{minutes} dk önce", $"{minutes} min ago");
	public static string TimeHoursAgo(int hours, Language? l = null) => Pick(l, $"{hours} sa önce", $"{hours} h ago");
	public static string TimeYesterday(Language? l = null) => Pick(l, $"dün", $"yesterday");
	public static string TimeDaysAgo(int days, Language? l = null) => Pick(l, $"{days} gün önce", $"{days} days ago");
}
