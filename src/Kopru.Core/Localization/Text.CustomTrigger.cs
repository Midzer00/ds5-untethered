// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Custom Trigger in a game's pop-up: an effect per trigger over whatever the game sends (EffectWeapon, EffectVibration,
// FallbackFrequency and TryAction are shared with other places).
public static partial class Text
{
	public static string CustomTriggerSection(Language? l = null) => T(l, "Custom.TriggerSection");

	public static string CustomTriggerLeft(Language? l = null) => T(l, "Custom.TriggerLeft");

	public static string CustomTriggerRight(Language? l = null) => T(l, "Custom.TriggerRight");

	// The picker's first choice: nothing over the game's own effect.
	public static string CustomTriggerGameEffect(Language? l = null) => T(l, "Custom.TriggerGameEffect");

	public static string CustomTriggerResistance(Language? l = null) => T(l, "Custom.TriggerResistance");

	public static string CustomTriggerStart(Language? l = null) => T(l, "Custom.TriggerStart");

	public static string CustomTriggerEnd(Language? l = null) => T(l, "Custom.TriggerEnd");

	public static string CustomTriggerForce(Language? l = null) => T(l, "Custom.TriggerForce");

	// The effect picker's label inside a trigger's own card (the card is already named after the trigger).
	public static string CustomTriggerEffectLabel(Language? l = null) => T(l, "Custom.TriggerEffectLabel");

	public static string CustomTriggerHint(Language? l = null) => T(l, "Custom.TriggerHint");

	public static string CustomTriggerTryHint(Language? l = null) => T(l, "Custom.TriggerTryHint");
}
