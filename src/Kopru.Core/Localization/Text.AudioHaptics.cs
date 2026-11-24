// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// "Turn sound into haptics": the game's sound felt on the controller, for games without DualSense support.
public static partial class Text
{
	public static string AudioHapticsLabel(Language? l = null) => T(l, "Audio.HapticsLabel");

	// The section title above it in a game's pop-up (Vibration tab).
	public static string AudioHapticsSection(Language? l = null) => T(l, "Audio.HapticsSection");

	public static string AudioHapticsHint(Language? l = null) => T(l, "Audio.HapticsHint");

	// Under the switch: the game's own (Xbox) rumble is a separate setting this switch does not turn off; it is beside it,
	// in the left column of the pop-up's Vibration tab.
	public static string AudioHapticsRumbleNote(Language? l = null) => T(l, "Audio.HapticsRumbleNote");

	public static string AudioHapticsStrengthLabel(Language? l = null) => T(l, "Audio.HapticsStrengthLabel");

	// How quiet a sound can be and still become haptics (the noise gate).
	public static string AudioHapticsSensitivityLabel(Language? l = null) => T(l, "Audio.HapticsSensitivityLabel");

	public static string AudioHapticsSensitivityHint(Language? l = null) => T(l, "Audio.HapticsSensitivityHint");

	public static string AudioHapticsBandBass(Language? l = null) => T(l, "Audio.HapticsBandBass");

	public static string AudioHapticsBandFull(Language? l = null) => T(l, "Audio.HapticsBandFull");

	// Status line.
	public static string AudioHapticsStatusOff(Language? l = null) => T(l, "Audio.HapticsStatusOff");

	public static string AudioHapticsStatusWaiting(Language? l = null) => T(l, "Audio.HapticsStatusWaiting");

	public static string AudioHapticsStatusListening(string game, Language? l = null) => T(l, "Audio.HapticsStatusListening", A("game", game));

	// The fallback capture: everything the default output plays, not only the game.
	public static string AudioHapticsStatusListeningSystem(string game, Language? l = null) => T(l, "Audio.HapticsStatusListeningSystem", A("game", game));

	public static string AudioHapticsStatusNative(Language? l = null) => T(l, "Audio.HapticsStatusNative");

	public static string AudioHapticsStatusUnavailable(Language? l = null) => T(l, "Audio.HapticsStatusUnavailable");
}
