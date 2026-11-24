// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// The Windows playback / recording device pickers (Quick Settings and Settings) and the mic button setting.
public static partial class Text
{
	public static string AudioOutput(Language? l = null) => T(l, "Audio.Output");

	public static string AudioInput(Language? l = null) => T(l, "Audio.Input");

	public static string AudioOutputHint(Language? l = null) => T(l, "Audio.OutputHint");

	public static string AudioInputHint(Language? l = null) => T(l, "Audio.InputHint");

	// The virtual controller's own endpoints; slot: shown when several controllers run.
	public static string ControllerSpeakerDevice(int? slot = null, Language? l = null) => slot is int s
		? T(l, "Controller.SpeakerDevice.Slot", A("slot", s))
		: T(l, "Controller.SpeakerDevice");

	public static string ControllerMicDevice(int? slot = null, Language? l = null) => slot is int s
		? T(l, "Controller.MicDevice.Slot", A("slot", s))
		: T(l, "Controller.MicDevice");

	public static string AudioDevicesUnavailable(Language? l = null) => T(l, "Audio.DevicesUnavailable");

	// The Quick Settings card has one short line (the tooltip has the full sentence).
	public static string AudioDevicesUnavailableShort(Language? l = null) => T(l, "Audio.DevicesUnavailableShort");

	public static string AudioDeviceNotChanged(Language? l = null) => T(l, "Audio.DeviceNotChanged");

	public static string MicButtonModeLabel(Language? l = null) => T(l, "Mic.ButtonModeLabel");

	public static string MicButtonModeHint(Language? l = null) => T(l, "Mic.ButtonModeHint");

	public static string MicButtonMute(Language? l = null) => T(l, "Mic.ButtonMute");

	public static string MicButtonTurnOff(Language? l = null) => T(l, "Mic.ButtonTurnOff");

	// The Quick Settings row has room for a short word only (the tooltip explains).
	public static string MicButtonTurnOffShort(Language? l = null) => T(l, "Mic.ButtonTurnOffShort");

	public static string MicStateMuted(Language? l = null) => T(l, "Mic.StateMuted");

	// The toast after a press of the mic button: the title says what happened in the button's mode ("Kapat": on / off,
	// "Sessize Al": on / muted; a PC microphone is muted in both, shown as "off" in the "Kapat" mode), the body which
	// microphone it was (or why nothing changed).
	public static string MicToastTitle(Audio.MicPress press, Audio.MicButtonMode mode, Language? l = null) => press switch
	{
		Audio.MicPress.ControllerOn or Audio.MicPress.ControllerUnmuted or Audio.MicPress.PcUnmuted => T(l, "Mic.ToastTitle.On"),
		Audio.MicPress.ControllerMuted => T(l, "Mic.ToastTitle.Muted"),
		Audio.MicPress.PcMuted when mode == Audio.MicButtonMode.Mute => T(l, "Mic.ToastTitle.Muted"),
		_ => T(l, "Mic.ToastTitle.Off")
	};

	public static string MicToastBody(Audio.MicPress press, Language? l = null) => press switch
	{
		Audio.MicPress.PcMuted or Audio.MicPress.PcUnmuted => T(l, "Mic.ToastBody.Pc"),
		Audio.MicPress.Ignored => T(l, "Mic.ToastBody.LowPower"),
		_ => T(l, "Mic.ToastBody.Controller")
	};

	// Quick Settings: the shortcuts tile is half as wide now.
	public static string QuickShortcutsShort(Language? l = null) => T(l, "Quick.ShortcutsShort");

	public static string QuickOutputTip(Language? l = null) => T(l, "Quick.OutputTip");

	public static string QuickInputTip(Language? l = null) => T(l, "Quick.InputTip");

	public static string QuickMicButtonTip(Language? l = null) => T(l, "Quick.MicButtonTip");

	public static string QuickMicTileTip(Language? l = null) => T(l, "Quick.MicTileTip");

	public static string QuickMicOffInSettings(Language? l = null) => T(l, "Quick.MicOffInSettings");

	public static string QuickMicOffInSettingsTip(Language? l = null) => T(l, "Quick.MicOffInSettingsTip");
}
