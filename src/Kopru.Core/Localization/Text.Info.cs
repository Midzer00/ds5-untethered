// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the controller info card, reconnect and the USB/Bluetooth switch.
public static partial class Text
{
	// Card and field labels (Title Case).
	public static string InfoTitle(Language? l = null) => Pick(l, $"Kumanda Bilgisi", $"Controller Info");
	public static string InfoModel(Language? l = null) => Pick(l, $"Kumanda Modeli", $"Model");
	public static string InfoConnection(Language? l = null) => Pick(l, $"Bağlantı", $"Connection");
	public static string InfoFirmware(Language? l = null) => Pick(l, $"Yazılım Sürümü", $"Firmware Version");
	public static string InfoHardware(Language? l = null) => Pick(l, $"Donanım Sürümü", $"Hardware Version");
	public static string InfoBuildDate(Language? l = null) => Pick(l, $"Derleme Tarihi", $"Build Date");
	public static string InfoAddress(Language? l = null) => Pick(l, $"Bluetooth Adresi", $"Bluetooth Address");
	public static string InfoConnectedFor(Language? l = null) => Pick(l, $"Bağlantı Süresi", $"Connected For");
	public static string InfoBattery(Language? l = null) => Pick(l, $"Pil", $"Battery");
	public static string InfoUnknown(Language? l = null) => Pick(l, $"Bilinmiyor", $"Unknown");
	public static string InfoCopy(Language? l = null) => Pick(l, $"Ayrıntıları Kopyala", $"Copy Details");
	public static string InfoCopied(Language? l = null) => Pick(l, $"Kopyalandı", $"Copied");
	public static string ReconnectController(Language? l = null) => Pick(l, $"Kumandayı Yeniden Bağla", $"Reconnect Controller");

	// Descriptions (sentence case).
	public static string InfoHint(Language? l = null) => Pick(l,
		$"Kumandanın kendisinden okunur; adres gizlilik için kısaltılır.",
		$"Read from the controller itself; the address is shortened for privacy.");

	public static string InfoNotConnected(Language? l = null) => Pick(l,
		$"Kumanda bağlı değil.",
		$"No controller is connected.");

	public static string ReconnectHint(Language? l = null) => Pick(l,
		$"Kumandayla bağlantıyı kapatıp yeniden açar; Bluetooth bağlantısı kopmaz.",
		$"Closes the connection to the controller and opens it again; Bluetooth stays connected.");

	// Settings → About: "DualSense · Bluetooth · firmware 06.30 · connected 5 min 03 s".
	public static string InfoCompact(string model, string connection, string firmware, string duration, Language? l = null) => Pick(l,
		$"{model} · {connection} · yazılım {firmware} · {duration} bağlı",
		$"{model} · {connection} · firmware {firmware} · connected {duration}");

	// Durations ("connected for").
	public static string DurationSeconds(int seconds, Language? l = null) => Pick(l, $"{seconds} sn", $"{seconds} s");
	public static string DurationMinutesSeconds(int minutes, int seconds, Language? l = null) =>
		Pick(l, $"{minutes} dk {seconds:00} sn", $"{minutes} min {seconds:00} s");
	public static string DurationHoursMinutes(int hours, int minutes, Language? l = null) =>
		Pick(l, $"{hours} sa {minutes:00} dk", $"{hours} h {minutes:00} min");
	public static string DurationDaysHours(int days, int hours, Language? l = null) => Pick(l, $"{days} g {hours} sa", $"{days} d {hours} h");

	// USB/Bluetooth switch toast and the stuck connection chip.
	public static string NotifyCableTitle(Language? l = null) => Pick(l, $"Kablo Bağlandı", $"Cable Connected");

	public static string NotifyCableBody(Language? l = null) => Pick(l,
		$"Sanal kumanda durduruldu. Kabloyu çıkarınca Bluetooth'ta kendiliğinden yeniden başlar.",
		$"The virtual controller was stopped. Unplug the cable and it starts again by itself on Bluetooth.");

	public static string HealthBridgeStuck(Language? l = null) => Pick(l,
		$"Gerçek kumanda görünüyor ama sanal kumanda ona bağlanamıyor. Yeniden bağlamayı dene.",
		$"The controller is there, but the virtual controller can't reach it. Try reconnecting.");
}
