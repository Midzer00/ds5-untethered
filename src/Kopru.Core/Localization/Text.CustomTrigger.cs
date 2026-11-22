// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Custom Trigger in a game's pop-up: an effect per trigger over whatever the game sends (EffectWeapon, EffectVibration,
// FallbackFrequency and TryAction are shared with other places).
public static partial class Text
{
	public static string CustomTriggerSection(Language? l = null) => Pick(l, $"Özel Tetik", $"Custom Trigger");

	public static string CustomTriggerLeft(Language? l = null) => Pick(l, $"Sol Tetik", $"Left Trigger");

	public static string CustomTriggerRight(Language? l = null) => Pick(l, $"Sağ Tetik", $"Right Trigger");

	// The picker's first choice: nothing over the game's own effect.
	public static string CustomTriggerGameEffect(Language? l = null) => Pick(l, $"Oyunun Efekti", $"Game's Effect");

	public static string CustomTriggerResistance(Language? l = null) => Pick(l, $"Direnç", $"Resistance");

	public static string CustomTriggerStart(Language? l = null) => Pick(l, $"Başlangıç", $"Start");

	public static string CustomTriggerEnd(Language? l = null) => Pick(l, $"Bitiş", $"End");

	public static string CustomTriggerForce(Language? l = null) => Pick(l, $"Güç", $"Force");

	public static string CustomTriggerHint(Language? l = null) => Pick(l,
		$"Oyun ne gönderirse göndersin seçilen efekt bu tetikte kalır. Oyunun Efekti seçilirse oyunun kendi tetik efekti kullanılır.",
		$"The chosen effect stays on this trigger whatever the game sends. Game's Effect leaves the game's own effect.");

	public static string CustomTriggerTryHint(Language? l = null) => Pick(l,
		$"Kumandanın iki tetiğindeki özel efektleri kısa süreliğine çalıştırır.",
		$"Plays both triggers' custom effects for a moment.");
}
