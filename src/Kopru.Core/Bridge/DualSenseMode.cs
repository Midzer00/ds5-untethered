// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Bridge;

// What the user turned off by hand from the DualSense Mode set while the mode was on. It lasts until the mode is switched
// (on or off, by the user or by a game); the next time the mode is on, the whole set is on again.
public sealed record DualSenseModeChoices(bool SpeakerOff = false)
{
	public static DualSenseModeChoices None { get; } = new();
}

// DualSense Mode: a runtime overlay on top of the user's settings, like Low Power. With the mode on, everything a game
// needs to treat the controller like a wired DualSense is active. Game haptics, the game's trigger effects and the game's
// light already follow the mode through the supported games profile (GameProfiles.For); the controller speaker is added
// here, because the game's sound effects for the controller's speaker would otherwise be lost while the user's own speaker
// switch is off. It never changes the stored values: with the mode off the user's own speaker switch counts again.
// Strength values (haptics, triggers) and the microphone stay the user's.
public static class DualSenseMode
{
	public static ControllerSettings Apply(ControllerSettings s, bool on, DualSenseModeChoices? choices = null)
	{
		if (!on)
		{
			return s;
		}
		bool speaker = !(choices ?? DualSenseModeChoices.None).SpeakerOff;
		return s.Speaker.Enabled == speaker ? s : s with { Speaker = s.Speaker with { Enabled = speaker } };
	}

	// What the controller gets: the mode first, then Low Power on top (Low Power always wins: it mutes the speaker).
	public static ControllerSettings Effective(ControllerSettings s, bool modeOn, DualSenseModeChoices? choices, bool lowPower) =>
		LowPower.Apply(Apply(s, modeOn, choices), lowPower);

	// The speaker switch as shown: on with the mode (unless turned off by hand since), else the user's own switch.
	public static bool SpeakerShown(bool userSpeaker, bool modeOn, DualSenseModeChoices? choices) =>
		modeOn ? !(choices ?? DualSenseModeChoices.None).SpeakerOff : userSpeaker;
}
