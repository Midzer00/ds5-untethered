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
		$"Oyun sesi kumandanın hoparlörüne ya da kulaklık girişine gider; DualSense Modu açıkken kendiliğinden açılır",
		$"Game audio goes to the controller's speaker or headphone jack; turns on by itself in DualSense Mode");

	public static string SpeakerVolume(Language? l = null) => Pick(l, $"Hoparlör Sesi", $"Speaker Volume");

	public static string HeadphoneVolume(Language? l = null) => Pick(l, $"Kulaklık Sesi", $"Headphone Volume");

	public static string HeadphonesPlugged(Language? l = null) => Pick(l, $"Kulaklık takılı", $"Headphones plugged in");
}
