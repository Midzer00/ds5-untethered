// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the experimental controller speaker (game audio to the controller's speaker or headphone jack).
public static partial class Text
{
	public static string GroupControllerSpeaker(Language? l = null) => T(l, "Group.ControllerSpeaker");

	public static string SpeakerSendGameAudio(Language? l = null) => T(l, "Speaker.SendGameAudio");

	public static string SpeakerSendGameAudioHint(Language? l = null) => T(l, "Speaker.SendGameAudioHint");

	// The one speaker switch (SpeakerSwitch below is its label, in the GroupControllerSpeaker card).
	public static string SpeakerSwitchHint(Language? l = null) => T(l, "Speaker.SwitchHint");

	public static string SpeakerSwitch(Language? l = null) => T(l, "Speaker.Switch");

	// Why the speaker plays nothing now although its switch may be on (Low Power, or a type without a speaker).
	public static string SpeakerOffLowPower(Language? l = null) => T(l, "Speaker.OffLowPower");
	public static string SpeakerOffPadType(string type, Language? l = null) => T(l, "Speaker.OffPadType", A("type", type));

	public static string SpeakerVolume(Language? l = null) => T(l, "Speaker.Volume");

	public static string HeadphoneVolume(Language? l = null) => T(l, "Headphone.Volume");

	public static string HeadphonesPlugged(Language? l = null) => T(l, "Headphones.Plugged");
}
