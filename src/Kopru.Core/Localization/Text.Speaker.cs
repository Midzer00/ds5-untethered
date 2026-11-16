// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the experimental controller speaker (game audio to the controller's speaker or headphone jack).
public static partial class Text
{
	public static string GroupControllerSpeaker(Language? l = null) => Pick(l, $"Kumanda Hoparlörü", $"Controller Speaker");

	public static string SpeakerSendGameAudio(Language? l = null) => Pick(l,
		$"Oyun Sesini Kumandaya Gönder (Deneysel)",
		$"Send Game Audio to Controller (Experimental)");

	public static string SpeakerSendGameAudioHint(Language? l = null) => Pick(l,
		$"Oyun sesi kumandanın hoparlörüne ya da kulaklık girişine gider; sadece DualSense türünde çalışır",
		$"Game audio goes to the controller's speaker or headphone jack; works only with the DualSense type");

	// The one speaker switch (SpeakerSwitch below is its label, in the GroupControllerSpeaker card).
	public static string SpeakerSwitchHint(Language? l = null) => Pick(l,
		$"Oyunun kumanda hoparlörüne gönderdiği sesleri çalar; sadece DualSense türünde çalışır.",
		$"Plays the sounds the game sends to the controller's speaker; works only with the DualSense type.");

	public static string SpeakerSwitch(Language? l = null) => Pick(l, $"Hoparlörü Kullan", $"Use The Speaker");

	// Why the speaker plays nothing now although its switch may be on (Low Power, or a type without a speaker).
	public static string SpeakerOffLowPower(Language? l = null) => Pick(l,
		$"Düşük Güç açıkken hoparlör kapalı.",
		$"The speaker is off while Low Power is on.");
	public static string SpeakerOffPadType(string type, Language? l = null) => Pick(l,
		$"{type} türünde hoparlör çalışmaz.",
		$"The speaker doesn't work with the {type} type.");

	public static string SpeakerVolume(Language? l = null) => Pick(l, $"Hoparlör Sesi", $"Speaker Volume");

	public static string HeadphoneVolume(Language? l = null) => Pick(l, $"Kulaklık Sesi", $"Headphone Volume");

	public static string HeadphonesPlugged(Language? l = null) => Pick(l, $"Kulaklık takılı", $"Headphones plugged in");
}
