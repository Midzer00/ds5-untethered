// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// "Turn sound into haptics": the game's sound felt on the controller, for games without DualSense support.
public static partial class Text
{
	public static string AudioHapticsLabel(Language? l = null) => Pick(l, $"Sesi Titreşime Çevir", $"Turn Sound Into Haptics");

	// The section title above it in a game's pop-up (Vibration tab).
	public static string AudioHapticsSection(Language? l = null) => Pick(l, $"Ses Tabanlı Titreşim", $"Sound To Haptics");

	public static string AudioHapticsHint(Language? l = null) => Pick(l,
		$"Oyunun sesinden titreşim üretir; DualSense'i desteklemeyen oyunlar için.",
		$"Makes haptics from the game's sound; for games without DualSense support.");

	// Under the switch: the game's own (Xbox) rumble is a separate setting this switch does not turn off; it is beside it,
	// in the left column of the pop-up's Vibration tab.
	public static string AudioHapticsRumbleNote(Language? l = null) => Pick(l,
		$"Oyunun kendi titreşimi bu ayardan etkilenmez; soldan ayarlanır.",
		$"The game's own rumble still comes through; it is set on the left.");

	public static string AudioHapticsStrengthLabel(Language? l = null) => Pick(l, $"Güç", $"Strength");

	// How quiet a sound can be and still become haptics (the noise gate).
	public static string AudioHapticsSensitivityLabel(Language? l = null) => Pick(l, $"Hassasiyet", $"Sensitivity");

	public static string AudioHapticsSensitivityHint(Language? l = null) => Pick(l,
		$"Titreşime dönüşebilecek en düşük ses düzeyi. Arka plan sesleri kumandayı sürekli titretiyorsa düşürün.",
		$"How quiet a sound can be and still become haptics. Lower it if background sound keeps the controller rumbling.");

	public static string AudioHapticsBandBass(Language? l = null) => Pick(l, $"Bas", $"Bass");

	public static string AudioHapticsBandFull(Language? l = null) => Pick(l, $"Tüm Ses", $"Full Sound");

	// Status line.
	public static string AudioHapticsStatusOff(Language? l = null) => Pick(l, $"Kapalı", $"Off");

	public static string AudioHapticsStatusWaiting(Language? l = null) => Pick(l, $"Oyun Bekleniyor", $"Waiting For The Game");

	public static string AudioHapticsStatusListening(string game, Language? l = null) => Pick(l, $"{game} Dinleniyor", $"Listening To {game}");

	// The fallback capture: everything the default output plays, not only the game.
	public static string AudioHapticsStatusListeningSystem(string game, Language? l = null) => Pick(l,
		$"{game} Dinleniyor (Tüm Sistem Sesi)",
		$"Listening To {game} (All System Sound)");

	public static string AudioHapticsStatusNative(Language? l = null) => Pick(l, $"Oyunun Kendi Titreşimi Kullanılıyor", $"The Game's Own Haptics In Use");

	public static string AudioHapticsStatusUnavailable(Language? l = null) => Pick(l, $"Bu Windows'ta Kullanılamıyor", $"Not Available On This Windows");
}
