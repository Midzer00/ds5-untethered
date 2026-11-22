// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// The game pop-up's segments: Game, Vibration, Triggers, and what they show.
public static partial class Text
{
	public static string GamePanelTabGame(Language? l = null) => Pick(l, $"Oyun", $"Game");

	public static string GamePanelTabVibration(Language? l = null) => Pick(l, $"Titreşim", $"Vibration");

	public static string GamePanelTabTriggers(Language? l = null) => Pick(l, $"Tetikler", $"Triggers");

	// The segmented switch's name for screen readers.
	public static string GamePanelTabsLabel(Language? l = null) => Pick(l, $"Oyun Ayarları Bölümleri", $"Game Settings Sections");

	// The Game segment's row with Back To Default (while the game has a profile of its own).
	public static string GameResetRowLabel(Language? l = null) => Pick(l, $"Varsayılan Profil", $"Default Profile");

	// Under "Profile: X" in segments 2 and 3, when other games use the same profile (a caption: a sentence).
	public static string ProfileSharedByGames(Language? l = null) => Pick(l,
		$"Ayarlar bu profili kullanan diğer oyunlarla ortaktır.",
		$"Settings are shared with other games using this profile.");
}
