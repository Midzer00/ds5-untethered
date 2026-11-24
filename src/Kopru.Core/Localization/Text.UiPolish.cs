// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Labels of the setting rows added in the 0.2 layout pass: the try row of Xbox And DS4 Rumble, the fallback effect's
// strength line, and the rows of a game's pop-up (start file, sound range).
public static partial class Text
{
	// Haptics And Triggers: the row of try buttons at the end of the Xbox And DS4 Rumble card.
	public static string TryRowLabel(Language? l = null) => T(l, "Try.RowLabel");

	public static string TryRowHint(Language? l = null) => T(l, "Try.RowHint");

	// The try buttons in that row (the row's label says "try").
	public static string TryTriggerEffect(Language? l = null) => T(l, "Try.TriggerEffect");

	public static string FallbackStrengthHint(Language? l = null) => T(l, "Fallback.StrengthHint");

	// A game's pop-up: the start file row (its label, the button while none is chosen, Change while one is).
	public static string StartFileLabel(Language? l = null) => T(l, "Start.FileLabel");

	public static string StartFilePick(Language? l = null) => T(l, "Start.FilePick");

	public static string StartFilePickNone(Language? l = null) => T(l, "Start.FilePickNone");

	public static string StartFileChangeShort(Language? l = null) => T(l, "Start.FileChangeShort");

	// A game's pop-up: Bass or All Sound under Turn Sound Into Haptics.
	public static string AudioHapticsBandLabel(Language? l = null) => T(l, "Audio.HapticsBandLabel");

	// A game's pop-up: the light switch's group.
	public static string GamePanelLightGroup(Language? l = null) => T(l, "Game.PanelLightGroup");
}
