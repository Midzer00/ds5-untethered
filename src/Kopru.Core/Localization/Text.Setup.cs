// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// First-run setup wizard texts.
public static partial class Text
{
	public static string SetupLanguageTitle(Language? l = null) => Pick(l, $"Dil", $"Language");
	public static string SetupLanguageInstructions(Language? l = null) => Pick(l,
		$"Uygulamanın dilini seçin. Daha sonra ayarlardan değiştirebilirsiniz.",
		$"Choose the app language. You can change it later in the settings.");

	public static string SetupDriversTitle(Language? l = null) => Pick(l, $"Sürücüler", $"Drivers");
	public static string SetupDriversInstructions(Language? l = null) => Pick(l,
		$"DS5 Untethered iki ücretsiz sürücüye ihtiyaç duyar: sanal kablolu kumanda için usbip-win2, gerçek kumandayı oyunlardan gizlemek için HidHide. İkisini de resmi sayfalarından indirip kurun, ardından yeniden kontrol edin.",
		$"DS5 Untethered needs two free drivers: usbip-win2 for the virtual wired controller and HidHide to hide the real controller from games. Download both from their official pages, install them, then check again.");
	public static string SetupUsbIpMissing(Language? l = null) => Pick(l,
		$"usbip-win2 kurulu değil (ya da sürücüsü eksik).",
		$"usbip-win2 is not installed (or its driver is missing).");
	public static string SetupHidHideMissing(Language? l = null) => Pick(l,
		$"HidHide kurulu değil (ya da sürücüsü eksik).",
		$"HidHide is not installed (or its driver is missing).");
	public static string SetupRestartPending(Language? l = null) => Pick(l,
		$"Sürücülerin çalışması için bilgisayarı yeniden başlatmanız gerekebilir.",
		$"You may need to restart the computer for the drivers to work.");

	public static string SetupPairTitle(Language? l = null) => Pick(l, $"Kumandayı eşleştir", $"Pair the controller");
	public static string SetupPairInstructions(Language? l = null) => Pick(l,
		$"Kumanda kapalıyken PS ve Create tuşlarına birlikte basılı tutun; ışık çubuğu hızlı hızlı yanıp sönmeye başlayınca bırakın. Sonra Windows Bluetooth ayarlarında \"Cihaz ekle\" ile kumandayı ekleyin.",
		$"With the controller off, hold PS and Create together until the light bar flashes quickly, then let go. Then add the controller with \"Add device\" in the Windows Bluetooth settings.");
	public static string SetupRadioMissing(Language? l = null) => Pick(l,
		$"Bu bilgisayarda Bluetooth adaptörü bulunamadı.",
		$"No Bluetooth adapter was found on this computer.");
	public static string SetupRadioOff(Language? l = null) => Pick(l,
		$"Bluetooth kapalı. Windows ayarlarından açın.",
		$"Bluetooth is off. Turn it on in the Windows settings.");
	public static string SetupControllerMissing(Language? l = null) => Pick(l,
		$"Bluetooth ile bağlı bir DualSense bulunamadı.",
		$"No DualSense is connected over Bluetooth.");
	public static string SetupControllerOnUsb(Language? l = null) => Pick(l,
		$"Kumanda USB kablosuyla bağlı. Kabloyu çıkarıp Bluetooth ile bağlayın.",
		$"The controller is connected with a USB cable. Unplug it and connect over Bluetooth.");
	public static string SetupEdgeConnected(Language? l = null) => Pick(l,
		$"DualSense Edge bağlı.",
		$"DualSense Edge connected.");

	public static string SetupAdapterTitle(Language? l = null) => Pick(l, $"Bluetooth adaptör testi", $"Bluetooth adapter test");
	public static string SetupAdapterInstructions(Language? l = null) => Pick(l,
		$"Kısa bir test, Bluetooth adaptörünüzün titreşim ve tetik verisini yeterince hızlı taşıyıp taşımadığını ölçer. Test sırasında kumandayı yakında tutun.",
		$"A short test measures whether your Bluetooth adapter carries haptics and trigger data fast enough. Keep the controller close during the test.");
	public static string SetupAdapterWeak(Language? l = null) => Pick(l,
		$"Adaptör zayıf; titreşim kesik kesik gelebilir. Kumandayı yaklaştırmayı ya da başka bir adaptör denemeyi düşünün.",
		$"The adapter is weak; haptics may stutter. Try moving the controller closer or using another adapter.");

	public static string SetupDoneTitle(Language? l = null) => Pick(l, $"Hazır", $"Done");
	public static string SetupDoneInstructions(Language? l = null) => Pick(l,
		$"Kurulum tamamlandı. DS5 Untethered'ı başlatıp oynayabilirsiniz.",
		$"Setup is complete. Start DS5 Untethered and play.");
	public static string SetupNotDoneInstructions(Language? l = null) => Pick(l,
		$"Bitirmek için önceki adımları tamamlayın.",
		$"Complete the previous steps to finish.");

	public static string SetupActionRecheck(Language? l = null) => Pick(l, $"Yeniden kontrol et", $"Check again");
	public static string SetupActionDownloadUsbIp(Language? l = null) => Pick(l, $"usbip-win2'yi indir", $"Download usbip-win2");
	public static string SetupActionDownloadHidHide(Language? l = null) => Pick(l, $"HidHide'ı indir", $"Download HidHide");
	public static string SetupActionOpenBluetooth(Language? l = null) => Pick(l, $"Bluetooth ayarlarını aç", $"Open Bluetooth settings");
	public static string SetupActionRunAdapterTest(Language? l = null) => Pick(l, $"Testi başlat", $"Start test");
}
