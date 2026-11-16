// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the per-game controller type (DualSense, or an emulated Xbox 360 or DualShock 4 controller).
public static partial class Text
{
	public static string PadTypeLabel(Language? l = null) => Pick(l, $"Kumanda Türü", $"Controller Type");

	public static string PadModeXboxTitle(Language? l = null) => Pick(l, $"Xbox 360 Modu", $"Xbox 360 Mode");

	public static string PadModeDualShock4Title(Language? l = null) => Pick(l, $"DualShock 4 Modu", $"DualShock 4 Mode");

	public static string PadModeDualSenseTitle(Language? l = null) => Pick(l, $"DualSense Modu", $"DualSense Mode");

	public static string PadModeDualSenseBody(Language? l = null) => Pick(l,
		$"Kumanda yeniden DualSense olarak görünüyor.",
		$"The controller appears as a DualSense again.");

	// The controller type model (the one per-game choice; PadTypeLabel above is its label).
	public static string PadTypeGameHint(Language? l = null) => Pick(l,
		$"Bu oyunun gördüğü kumanda. Emin değilsen DualSense'te bırak.",
		$"The controller this game sees. If unsure, leave it on DualSense.");

	public static string PadTypeDefaultLabel(Language? l = null) => Pick(l, $"Varsayılan Kumanda Türü", $"Default Controller Type");

	public static string PadTypeDefaultHint(Language? l = null) => Pick(l,
		$"Ayrı seçim yapmadığın oyunlar bu kumandayı görür.",
		$"Games you have not set separately see this controller.");

	public static string PadChoiceAutoPerGame(Language? l = null) => Pick(l, $"Otomatik (Oyuna Göre)", $"Automatic (Per Game)");

	// What Automatic means: on a game's picker (it follows the default type) and on the default picker.
	public static string PadTypeAutoLine(Language? l = null) => Pick(l,
		$"Varsayılan Kumanda Türü'nü kullanır.",
		$"Uses the Default Controller Type.");

	public static string PadChoiceAutoLine(Language? l = null) => Pick(l,
		$"Her oyun kendi seçimini kullanır; seçim yoksa DualSense.",
		$"Each game uses its own choice; without one, DualSense.");

	public static string PadTypeDualSenseLine(Language? l = null) => Pick(l,
		$"Her şey çalışır: oyunun haptiği, tetikleri, ışığı, hoparlörü ve mikrofonu.",
		$"Everything works: the game's haptics, triggers, light, speaker and microphone.");

	public static string PadTypeDualShock4Line(Language? l = null) => Pick(l,
		$"Sadece PS4 kumandasını tanıyan eski oyunlar için: titreşim ve ışık var; adaptif tetik, oyun haptiği ve hoparlör yok.",
		$"For older games that only know the PS4 controller: rumble and light work; no adaptive triggers, game haptics or speaker.");

	public static string PadTypeXboxLine(Language? l = null) => Pick(l,
		$"PlayStation kumandasını hiç tanımayan oyunlar için: sadece titreşim var; dokunmatik yüzey ve hareket sensörü de yok.",
		$"For games that don't know PlayStation controllers at all: rumble only; no touchpad or motion sensor either.");

	// type: the product name (DualSense, DualShock 4, Xbox 360).
	public static string GameSees(string type, Language? l = null) => Pick(l, $"Oyun Şunu Görüyor: {type}", $"The Game Sees: {type}");

	// A controller type change while the game runs waits for the game to restart.
	public static string PadTypeDeferred(Language? l = null) => Pick(l, $"Oyunu Yeniden Başlatınca Geçerli Olur", $"Applies When You Restart The Game");

	public static string PadTypeDeferredHint(Language? l = null) => Pick(l,
		$"Oyun açıkken kumanda türü değişirse oyun kumandayı kaybedebilir.",
		$"If the controller type changes while the game is open, the game may lose the controller.");

	public static string PadTypeApplyNow(Language? l = null) => Pick(l, $"Şimdi Uygula", $"Apply Now");

	// The controller type toast while a type waits for the running game to restart.
	public static string PadToastPendingBody(Language? l = null) => Pick(l,
		$"Oyunu yeniden başlatınca geçerli olur.",
		$"Applies when you restart the game.");

	// After a live swap of the virtual controller.
	public static string PadSwapRestartHint(Language? l = null) => Pick(l,
		$"Oyun kumandayı görmüyorsa oyunu yeniden başlat.",
		$"If the game does not see the controller, restart the game.");
}
