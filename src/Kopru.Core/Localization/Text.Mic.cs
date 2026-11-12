// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the controller microphone (experimental passthrough, mute button, mute LED).
public static partial class Text
{
	public static string GroupControllerMic(Language? l = null) => Pick(l, $"Kumanda Mikrofonu", $"Controller Microphone");

	public static string MicSendToGame(Language? l = null) => Pick(l,
		$"Mikrofonu Oyuna Aktar (Deneysel)",
		$"Pass Microphone to Games (Experimental)");

	public static string MicGain(Language? l = null) => Pick(l, $"Mikrofon Kazancı", $"Microphone Gain");

	public static string MicNoiseGate(Language? l = null) => Pick(l, $"Gürültü Kapısı", $"Noise Gate");

	public static string MicNoiseGateHint(Language? l = null) => Pick(l,
		$"Sessizken arka plan sesini keser.",
		$"Cuts background noise while you're quiet.");

	public static string MicLevel(Language? l = null) => Pick(l, $"Mikrofon Seviyesi", $"Microphone Level");
}
