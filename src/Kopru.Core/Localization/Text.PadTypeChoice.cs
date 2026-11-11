// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the global controller type ("Kumanda Türü" in Quick Settings and on the Supported Games page):
// Automatic (the per-game choice) or one type forced for every game. Product names stay the same in every language.
public static partial class Text
{
	public static string PadChoiceAuto(Language? l = null) => Pick(l, $"Otomatik", $"Automatic");

	public static string PadChoiceHint(Language? l = null) => Pick(l,
		$"Tüm oyunlar için. Otomatik'te her oyun aşağıdaki kendi seçimini kullanır.",
		$"For every game. On Automatic each game uses its own choice below.");

	public static string PadChoiceTip(Language? l = null) => Pick(l,
		$"Oyunların ve Windows'un gördüğü kumanda. Otomatik: oyun başına seçim, yoksa DualSense. DualShock 4: adaptif tetik, oyun haptikleri, hoparlör ve mikrofon yok; titreşim haptik olarak çalınır, ışık çalışır. Xbox 360: bunlara ek olarak oyun dokunmatik yüzeye dokunmayı (tıklaması Back olur) ve hareket sensörünü görmez.",
		$"The controller games and Windows see. Automatic: the per-game choice, else DualSense. DualShock 4: no adaptive triggers, game haptics, speaker or microphone; rumble is played as haptics and the light works. Xbox 360: on top of that, games don't see touchpad touches (its click becomes Back) or the motion sensor.");

	public static string PadForcedNote(Language? l = null) => Pick(l,
		$"Kumanda türü elle seçili; oyun başına seçimler Otomatik'te geçerli.",
		$"Set by hand for every game; per-game choices apply on Automatic.");

	// What games get while the virtual controller is not a DualSense (Overview, sidebar tooltip).
	public static string PadShownAsDualShock4(Language? l = null) => Pick(l,
		$"DualShock 4 olarak görünüyor: titreşim ve ışık var; adaptif tetik, oyun haptiği, hoparlör ve mikrofon yok",
		$"Shown as a DualShock 4: rumble and light work; no adaptive triggers, game haptics, speaker or microphone");

	public static string PadShownAsXbox(Language? l = null) => Pick(l,
		$"Xbox 360 olarak görünüyor: titreşim var; adaptif tetik, oyun haptiği, hoparlör, mikrofon, dokunmatik yüzey ve hareket sensörü yok",
		$"Shown as an Xbox 360 controller: rumble works; no adaptive triggers, game haptics, speaker, microphone, touchpad or motion sensor");

	// The toast when the type was forced by hand (not by the game in front).
	public static string PadForcedXboxBody(Language? l = null) => Pick(l,
		$"Kumanda türü elle seçildi: kumanda tüm oyunlarda Xbox 360 olarak görünüyor.",
		$"Controller type set by hand: the controller appears as an Xbox 360 controller in every game.");

	public static string PadForcedDualShock4Body(Language? l = null) => Pick(l,
		$"Kumanda türü elle seçildi: kumanda tüm oyunlarda DualShock 4 olarak görünüyor.",
		$"Controller type set by hand: the controller appears as a DualShock 4 in every game.");

	public static string PadForcedDualSenseBody(Language? l = null) => Pick(l,
		$"Kumanda türü elle seçildi: kumanda tüm oyunlarda DualSense olarak görünüyor.",
		$"Controller type set by hand: the controller appears as a DualSense in every game.");
}
