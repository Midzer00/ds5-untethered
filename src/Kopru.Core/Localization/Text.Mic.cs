// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the controller microphone (experimental passthrough, mute button, mute LED).
public static partial class Text
{
	public static string GroupControllerMic(Language? l = null) => T(l, "Group.ControllerMic");

	public static string MicSendToGame(Language? l = null) => T(l, "Mic.SendToGame");

	public static string MicGain(Language? l = null) => T(l, "Mic.Gain");

	public static string MicNoiseGate(Language? l = null) => T(l, "Mic.NoiseGate");

	public static string MicNoiseGateHint(Language? l = null) => T(l, "Mic.NoiseGateHint");

	public static string MicLevel(Language? l = null) => T(l, "Mic.Level");
}
