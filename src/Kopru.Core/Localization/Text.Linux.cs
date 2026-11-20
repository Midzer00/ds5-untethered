// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Linux (experimental): the setup page's system step, the health chips and the notes for what is not there yet.
public static partial class Text
{
	public static string SetupLinuxTitle(Language? l = null) => Pick(l, $"Sistem Hazırlığı", $"System Setup");

	public static string SetupLinuxInstructions(Language? l = null) => Pick(l,
		$"Linux'ta DS5 Untethered sistemin USB/IP istemcisini (usbip ve vhci_hcd modülü) kullanır. Sanal kumandayı takarken şifreni pkexec ile sorar. Aşağıdakileri düzelt, sonra yeniden kontrol et.",
		$"On Linux, DS5 Untethered uses the system's USB/IP client (usbip and the vhci_hcd module). It asks for your password through pkexec when it plugs in the virtual controller. Fix what is listed below, then check again.");

	public static string SetupLinuxVhciMissing(Language? l = null) => Pick(l,
		$"vhci_hcd modülü yüklü değil. Çalıştır: sudo modprobe vhci-hcd. Her açılışta yüklenmesi için: echo vhci-hcd | sudo tee /etc/modules-load.d/vhci-hcd.conf",
		$"The vhci_hcd kernel module isn't loaded. Run: sudo modprobe vhci-hcd. To load it at every boot: echo vhci-hcd | sudo tee /etc/modules-load.d/vhci-hcd.conf");

	public static string SetupLinuxUsbIpMissing(Language? l = null) => Pick(l,
		$"usbip kurulu değil. Dağıtımının usbip paketini kur (Ubuntu'da linux-tools-generic).",
		$"usbip isn't installed. Install your distribution's usbip package (on Ubuntu: linux-tools-generic).");

	public static string SetupLinuxPkexecMissing(Language? l = null) => Pick(l,
		$"pkexec kurulu değil. polkit paketini kur (adı genelde polkit ya da pkexec).",
		$"pkexec isn't installed. Install polkit (the package is usually called polkit or pkexec).");

	public static string SetupLinuxHidrawDenied(Language? l = null) => Pick(l,
		$"Kumandanın hidraw dosyası senin kullanıcınla açılamıyor. Linux rehberindeki udev kuralını ekle, sudo udevadm control --reload çalıştır, kumandayı yeniden bağla.",
		$"Your user can't open the controller's hidraw node. Add the udev rule from the Linux guide, run sudo udevadm control --reload, then reconnect the controller.");

	public static string SetupLinuxHidingNote(Language? l = null) => Pick(l,
		$"Linux'ta gerçek kumanda oyunlardan henüz gizlenmiyor; oyun iki kumanda görebilir.",
		$"On Linux the real controller isn't hidden from games yet; a game may see two controllers.");

	public static string SetupLinuxPairInstructions(Language? l = null) => Pick(l,
		$"Kumanda kapalıyken ø ve Create tuşlarına birlikte basılı tut; ışık çubuğu hızlı hızlı yanıp sönünce bırak. Sonra masaüstünün Bluetooth ayarlarından (ya da bluetoothctl ile) eşleştir.",
		$"With the controller off, hold the ø button and Create together until the light bar flashes quickly, then let go. Then pair it in your desktop's Bluetooth settings (or with bluetoothctl).");

	public static string SetupLinuxRadioOff(Language? l = null) => Pick(l,
		$"Bluetooth kapalı. Masaüstünün Bluetooth ayarlarından aç.",
		$"Bluetooth is off. Turn it on in your desktop's Bluetooth settings.");

	public static string SetupActionCopyCommands(Language? l = null) => Pick(l, $"Komutları Kopyala", $"Copy Commands");

	// The file pickers' "any file" choice (Linux programs have no extension).
	public static string AllFilesType(Language? l = null) => Pick(l, $"Tüm Dosyalar", $"All Files");

	public static string HealthLinuxSystemOk(Language? l = null) => Pick(l,
		$"usbip, vhci_hcd ve pkexec hazır.",
		$"usbip, vhci_hcd and pkexec are ready.");

	public static string HealthHidingLinux(Language? l = null) => Pick(l,
		$"Linux'ta gerçek kumanda henüz gizlenmiyor; oyunlar iki kumanda görebilir.",
		$"The real controller isn't hidden on Linux yet; games may see two controllers.");

	// Settings > General on Linux: what the app can't do there yet, in one line under the app group.
	public static string LinuxExperimentalNote(Language? l = null) => Pick(l,
		$"Linux sürümü deneysel: gerçek kumanda gizlenmiyor; sesten titreşim, ses aygıtı seçimi, Fare Modu ve güncellemeler henüz yok.",
		$"The Linux version is experimental: the real controller isn't hidden; sound to haptics, sound device choice, Mouse Mode and updates aren't there yet.");
}
