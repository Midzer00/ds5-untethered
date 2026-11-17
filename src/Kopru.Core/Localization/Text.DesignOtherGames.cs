// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the Games and Haptics And Triggers pages: the profile's cards, the try-it tooltips and the pages.
public static partial class Text
{

	public static string OtherGamesRumbleTryHint(Language? l = null) => Pick(l,
		$"Bu ayarlarla kısa bir titreşim çalar; Sol ve Sağ Haptik yalnızca o tarafı dener.",
		$"Plays a short rumble with these settings; Left and Right Haptics try one side only.");

	public static string OtherGamesTriggerTryHint(Language? l = null) => Pick(l,
		$"Seçili efekti kısa bir süre L2 ve R2'ye uygular.",
		$"Puts the chosen effect on L2 and R2 for a moment.");

	// The one game profile's cards (FallbackTriggerLabel/Hint in Text.Ui are the fallback row) and the pages.
	public static string GameEffectsCard(Language? l = null) => Pick(l, $"Oyunun Kendi Efektleri", $"The Game's Own Effects");

	public static string GameEffectsCardHint(Language? l = null) => Pick(l,
		$"DualSense'i destekleyen oyunların gönderdiği haptik ve tetik efektlerinin gücü.",
		$"How strong the haptics and trigger effects sent by DualSense games feel.");

	public static string ClassicRumbleCard(Language? l = null) => Pick(l, $"Xbox ve DS4 Titreşimi", $"Xbox And DS4 Rumble");

	public static string ClassicRumbleCardHint(Language? l = null) => Pick(l,
		$"Genel profil: Xbox ve DS4 türünde, ya da oyun yalnızca klasik titreşim gönderdiğinde bu ayarlar kullanılır.",
		$"General profile: used with the Xbox and DS4 types, or when a game sends only classic rumble.");

	public static string PageGames(Language? l = null) => Pick(l, $"Oyunlar", $"Games");

	public static string PageHapticsTriggers(Language? l = null) => Pick(l, $"Titreşim ve Tetikler", $"Haptics And Triggers");

	public static string PageHapticsTriggersIntro(Language? l = null) => Pick(l,
		$"Oyunun gönderdiği efektler olduğu gibi geçer. Oyun bir şey göndermezse Xbox ve DS4 Titreşimi ayarları kullanılır.",
		$"What the game sends passes through. Where it sends nothing, the Xbox And DS4 Rumble settings are used.");

	// The games not in the supported list: the Games page's second view, and the link to it on Haptics And Triggers.
	public static string UnsupportedGamesTitle(Language? l = null) => Pick(l, $"Desteklenmeyen Oyunlar", $"Unsupported Games");

	public static string UnsupportedGamesHint(Language? l = null) => Pick(l,
		$"Bu oyunlar seçtiğin profilin Xbox ve DS4 Titreşimi ayarlarını kullanır.",
		$"These games use the Xbox And DS4 Rumble settings of the profile you pick.");

	// The Games page's main switch.
	public static string SupportedView(Language? l = null) => Pick(l, $"Desteklenen", $"Supported");

	public static string UnsupportedViewCount(int count, Language? l = null) => Pick(l, $"Desteklenmeyen ({count})", $"Unsupported ({count})");

	// Starting a game from the list.
	public static string PlayGame(Language? l = null) => Pick(l, $"Oyna", $"Play");

	public static string PlayGameTip(Language? l = null) => Pick(l, $"Oyunu başlatır.", $"Starts the game.");

	public static string GameLaunchFailedTitle(Language? l = null) => Pick(l, $"Oyun Başlatılamadı", $"Couldn't Start The Game");

	public static string GameLaunchFailedBody(string game, Language? l = null) => Pick(l, $"{game} başlatılamadı.", $"{game} couldn't be started.");

	// The tooltip of "Profil: …" on the Haptics And Triggers page.
	public static string EditingProfileTip(Language? l = null) => Pick(l,
		$"Buradaki ayarlar bu profile kaydedilir. Oyunlar sayfasında her oyuna ayrı profil seçebilirsin.",
		$"These settings are saved to this profile. On the Games page you can pick a profile for each game.");
}
