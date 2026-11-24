// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the Xbox Series controller type: its line, its fallback toast, the Trigger Rumble setting and the
// Games page's Xbox Trigger Rumble view. The product name "Xbox Series" is the same in every language (SettingsState).
public static partial class Text
{
	public static string PadTypeXboxSeriesLine(Language? l = null) => T(l, "Pad.TypeXboxSeriesLine");

	public static string PadModeXboxSeriesTitle(Language? l = null) => T(l, "Pad.ModeXboxSeriesTitle");

	// The Xbox Series pad could not be used (Windows too old, or Windows did not start it).
	public static string XboxSeriesFallbackBody(Language? l = null) => T(l, "Xbox.SeriesFallbackBody");

	// The Trigger Rumble row (Xbox And DS4 Rumble card, a game's pop-up).
	public static string TriggerRumbleLabel(Language? l = null) => T(l, "Trigger.RumbleLabel");

	public static string TriggerRumbleHint(Language? l = null) => T(l, "Trigger.RumbleHint");

	public static string TriggerRumbleXboxLike(Language? l = null) => T(l, "Trigger.RumbleXboxLike");

	public static string TriggerRumbleSoft(Language? l = null) => T(l, "Trigger.RumbleSoft");

	public static string TriggerRumbleSharp(Language? l = null) => T(l, "Trigger.RumbleSharp");

	// The Games page's third view: games that send Xbox trigger rumble.
	public static string XboxTriggerView(Language? l = null) => T(l, "Xbox.TriggerView");

	public static string XboxTriggerGamesHint(Language? l = null) => T(l, "Xbox.TriggerGamesHint");

	// The experimental switch (Settings > Controllers, the Triggers card): off by default; on, the game's trigger rumble
	// plays on the DualSense triggers with the Xbox Series type and Automatic gives Xbox trigger rumble games Xbox Series. The card's title with a muted Experimental tag beside it; the full name (with the tag) is the
	// switch's accessible name.
	public static string TriggersGroup(Language? l = null) => T(l, "Triggers.Group");

	public static string ExperimentalTag(Language? l = null) => T(l, "Experimental.Tag");

	public static string XboxTriggerRumbleTitle(Language? l = null) => T(l, "Xbox.TriggerRumbleTitle");

	public static string XboxTriggerRumbleSetting(Language? l = null) => T(l, "Xbox.TriggerRumbleSetting");

	// The row's line under the title; the hint below is its tooltip and help text.
	public static string XboxTriggerRumbleSettingShort(Language? l = null) => T(l, "Xbox.TriggerRumbleSettingShort");

	public static string XboxTriggerRumbleSettingHint(Language? l = null) => T(l, "Xbox.TriggerRumbleSettingHint");
}
