// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// In-app tips (the quiet bar at the top of the main window) and their switch in Settings > General.
public static partial class Text
{
	public static string TipMouseModeGesture(Language? l = null) => Pick(l,
		$"Fare Modu için touchpad'e iki kez bas.",
		$"Press the touchpad twice for Mouse Mode.");

	// The button in the sidebar (in the logo's place) while a game runs and the app is in front; the game's name is above it.
	public static string BackToGame(Language? l = null) => Pick(l, $"Oyuna Dön", $"Back To Game");

	public static string BackToGameTip(string game, Language? l = null) => Pick(l,
		$"Uygulamayı gizler, {game} öne gelir.",
		$"Hides the app and brings {game} to the front.");

	public static string ShowTips(Language? l = null) => Pick(l, $"İpuçlarını Göster", $"Show Tips");

	public static string ShowTipsHint(Language? l = null) => Pick(l,
		$"İpuçları pencerenin üstünde birkaç saniye görünür; öğrendiğin bir ipucu bir daha çıkmaz.",
		$"Tips show at the top of the window for a few seconds; a tip you have learned does not come back.");

	public static string CloseTip(Language? l = null) => Pick(l, $"İpucunu Kapat", $"Close Tip");
}
