// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// "Back To Game": the way back to a running game from the app.
public static partial class Text
{
	// The button in the sidebar (in the logo's place) while a game runs and the app is in front; the game's name is above it.
	public static string BackToGame(Language? l = null) => Pick(l, $"Oyuna Dön", $"Back To Game");

	public static string BackToGameTip(string game, Language? l = null) => Pick(l,
		$"Uygulamayı gizler, {game} öne gelir.",
		$"Hides the app and brings {game} to the front.");
}
