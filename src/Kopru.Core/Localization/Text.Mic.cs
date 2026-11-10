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
		$"Konuşmadığınızda hafif arka plan seslerini keser",
		$"Cuts faint background noise while you are not speaking");

	public static string MicMuteWindows(Language? l = null) => Pick(l, $"Windows Mikrofonunu da Sessize Al", $"Also Mute the Windows Microphone");

	public static string MicLevel(Language? l = null) => Pick(l, $"Mikrofon Seviyesi", $"Microphone Level");

	public static string MuteLedFollowMic(Language? l = null) => Pick(l, $"Mikrofonu İzle", $"Follow Microphone");
}
