// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the Games and Haptics And Triggers pages: the profile's cards, the try-it tooltips and the pages.
public static partial class Text
{

	public static string OtherGamesRumbleTryHint(Language? l = null) => T(l, "Other.GamesRumbleTryHint");

	public static string OtherGamesTriggerTryHint(Language? l = null) => T(l, "Other.GamesTriggerTryHint");

	// The one game profile's cards (FallbackTriggerLabel/Hint in Text.Ui are the fallback row) and the pages.
	public static string GameEffectsCard(Language? l = null) => T(l, "Game.EffectsCard");

	public static string GameEffectsCardHint(Language? l = null) => T(l, "Game.EffectsCardHint");

	public static string ClassicRumbleCard(Language? l = null) => T(l, "Classic.RumbleCard");

	public static string ClassicRumbleCardHint(Language? l = null) => T(l, "Classic.RumbleCardHint");

	public static string PageGames(Language? l = null) => T(l, "Page.Games");

	public static string PageHapticsTriggers(Language? l = null) => T(l, "Page.HapticsTriggers");

	public static string PageHapticsTriggersIntro(Language? l = null) => T(l, "Page.HapticsTriggersIntro");

	// The games not in the supported list: the Games page's second view, and the link to it on Haptics And Triggers.
	public static string UnsupportedGamesTitle(Language? l = null) => T(l, "Unsupported.GamesTitle");

	public static string UnsupportedGamesHint(Language? l = null) => T(l, "Unsupported.GamesHint");

	// The Games page's main switch.
	public static string SupportedView(Language? l = null) => T(l, "Supported.View");

	public static string UnsupportedViewCount(int count, Language? l = null) => T(l, "Unsupported.ViewCount", A("count", count));

	// Starting a game from the list.
	public static string PlayGame(Language? l = null) => T(l, "Play.Game");

	public static string PlayGameTip(Language? l = null) => T(l, "Play.GameTip");

	public static string GameLaunchFailedTitle(Language? l = null) => T(l, "Game.LaunchFailedTitle");

	public static string GameLaunchFailedBody(string game, Language? l = null) => T(l, "Game.LaunchFailedBody", A("game", game));

	// The tooltip of "Profil: …" on the Haptics And Triggers page.
	public static string EditingProfileTip(Language? l = null) => T(l, "Editing.ProfileTip");
}
