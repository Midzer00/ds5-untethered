// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the Xbox Series controller type: its line, its fallback toast, the Trigger Rumble setting and the
// Games page's Xbox Trigger Rumble view. The product name "Xbox Series" is the same in every language (SettingsState).
public static partial class Text
{
	public static string PadTypeXboxSeriesLine(Language? l = null) => Pick(l,
		$"Xbox kumandasını bekleyen oyunlar için: titreşim var; dokunmatik yüzey ve hareket sensörü yok.",
		$"For games made for Xbox controllers: rumble works; no touchpad or motion sensor.");

	public static string PadModeXboxSeriesTitle(Language? l = null) => Pick(l, $"Xbox Series Modu", $"Xbox Series Mode");

	// The Xbox Series pad could not be used (Windows too old, or Windows did not start it).
	public static string XboxSeriesFallbackBody(Language? l = null) => Pick(l,
		$"Xbox Series açılamadı, Xbox 360 kullanılıyor.",
		$"Xbox Series couldn't start, using Xbox 360.");

	// The Trigger Rumble row (Xbox And DS4 Rumble card, a game's pop-up).
	public static string TriggerRumbleLabel(Language? l = null) => Pick(l, $"Tetik Titreşimi", $"Trigger Rumble");

	public static string TriggerRumbleHint(Language? l = null) => Pick(l,
		$"Oyunun tetik titreşimi; yalnızca Xbox Series türünde çalışır.",
		$"The game's trigger rumble; works only with the Xbox Series type.");

	public static string TriggerRumbleXboxLike(Language? l = null) => Pick(l, $"Xbox Gibi", $"Xbox-Like");

	public static string TriggerRumbleSoft(Language? l = null) => Pick(l, $"Yumuşak", $"Soft");

	public static string TriggerRumbleSharp(Language? l = null) => Pick(l, $"Keskin", $"Sharp");

	// The Games page's third view: games that send Xbox trigger rumble.
	public static string XboxTriggerView(Language? l = null) => Pick(l, $"Xbox Tetik Titreşimi", $"Xbox Trigger Rumble");

	public static string XboxTriggerGamesHint(Language? l = null) => Pick(l,
		$"Bu oyunlar Xbox Series türünde tetiklere titreşim gönderir.",
		$"These games send trigger rumble with the Xbox Series type.");

	// The experimental switch (Settings > Controllers, the Triggers card): off by default; on, the game's trigger rumble
	// plays on the DualSense triggers with the Xbox Series type and Automatic gives Xbox trigger rumble games Xbox Series. The card's title with a muted Experimental tag beside it; the full name (with the tag) is the
	// switch's accessible name.
	public static string TriggersGroup(Language? l = null) => Pick(l, $"Tetikler", $"Triggers");

	public static string ExperimentalTag(Language? l = null) => Pick(l, $"Deneysel", $"Experimental");

	public static string XboxTriggerRumbleTitle(Language? l = null) => Pick(l, $"Xbox Tetik Titreşimi", $"Xbox Trigger Rumble");

	public static string XboxTriggerRumbleSetting(Language? l = null) => Pick(l,
		$"Xbox Tetik Titreşimi (Deneysel)", $"Xbox Trigger Rumble (Experimental)");

	// The row's line under the title; the hint below is its tooltip and help text.
	public static string XboxTriggerRumbleSettingShort(Language? l = null) => Pick(l,
		$"Xbox tetik titreşimi olan oyunlarda titreşimi DualSense tetiklerine taşır.",
		$"Plays Xbox trigger rumble on the DualSense triggers in games that use it.");

	public static string XboxTriggerRumbleSettingHint(Language? l = null) => Pick(l,
		$"Açıkken Xbox tetik titreşimi olan oyunlara Xbox Series kumanda gösterilir; Xbox Series türünde oyunun tetik titreşimi DualSense tetiklerinde hissedilir. Kapalıyken oyunun tetik titreşimi yok sayılır.",
		$"When on, games with Xbox trigger rumble see an Xbox Series controller, and with the Xbox Series type the game's trigger rumble plays on the DualSense triggers. When off, the game's trigger rumble is ignored.");
}
