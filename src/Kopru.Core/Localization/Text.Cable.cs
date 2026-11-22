// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the cable mode setting (Settings, Notifications tab, its own card) and the wired connection name.
public static partial class Text
{
	// The card's title and the setting row labels and choices (Title Case).
	public static string CardCable(Language? l = null) => Pick(l, $"Kablo", $"Cable");
	public static string CableModeLabel(Language? l = null) => Pick(l, $"Kablo Takılınca", $"When The Cable Is Plugged In");
	public static string CableModeWired(Language? l = null) => Pick(l, $"Kablo Üzerinden Çalış (Deneysel)", $"Use The Cable (Experimental)");
	public static string CableModeChargeOnly(Language? l = null) => Pick(l, $"Sadece Şarj Et", $"Charge Only");

	// Descriptions (sentence case).
	public static string CableModeShort(Language? l = null) => Pick(l,
		$"Kablo takılıyken kumandayla kablo üzerinden konuşulur; Bluetooth bağlı kalır.",
		$"While the cable is in, the app talks to the controller over it; Bluetooth stays connected.");

	public static string CableModeHint(Language? l = null) => Pick(l,
		$"Kablo Üzerinden Çalış: kablo takılıyken giriş, tetikler, ışık, haptik, hoparlör ve mikrofon USB üzerinden gider; kablo çıkınca Bluetooth'a döner. Sorun çıkarsa Sadece Şarj Et'i seç: kablo yalnızca şarj eder, her şey Bluetooth'ta kalır.",
		$"Use The Cable: while the cable is in, input, triggers, light, haptics, speaker and microphone go over USB; unplugging goes back to Bluetooth. If anything misbehaves choose Charge Only: the cable only charges and everything stays on Bluetooth.");

	// The connection name shown while the bridge talks to the controller over its USB cable.
	public static string ConnectionWired(Language? l = null) => Pick(l, $"USB Kablosu (Deneysel)", $"USB Cable (Experimental)");
}
