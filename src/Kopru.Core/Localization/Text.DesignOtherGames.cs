// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the Other Games page layout: card titles and the try-it rows under each card.
public static partial class Text
{
	public static string OtherGamesRumbleCard(Language? l = null) => Pick(l, $"Titreşim ve Haptik", $"Rumble And Haptics");
	public static string OtherGamesTriggerCard(Language? l = null) => Pick(l, $"Tetikler", $"Triggers");
	public static string OtherGamesTryLabel(Language? l = null) => Pick(l, $"Kumandada Dene", $"Try On The Controller");

	public static string OtherGamesRumbleTryHint(Language? l = null) => Pick(l,
		$"Bu ayarlarla kısa bir titreşim çalar; Sol ve Sağ Haptik yalnızca o tarafı dener.",
		$"Plays a short rumble with these settings; Left and Right Haptics try one side only.");

	public static string OtherGamesTriggerTryHint(Language? l = null) => Pick(l,
		$"Seçili efekti kısa bir süre L2 ve R2'ye uygular.",
		$"Puts the chosen effect on L2 and R2 for a moment.");
}
