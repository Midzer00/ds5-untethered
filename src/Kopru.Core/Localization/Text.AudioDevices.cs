// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// The Windows playback / recording device pickers (Quick Settings and Settings) and the mic button setting.
public static partial class Text
{
	public static string AudioOutput(Language? l = null) => Pick(l, $"Ses Çıkışı", $"Sound Output");

	public static string AudioInput(Language? l = null) => Pick(l, $"Mikrofon", $"Microphone");

	public static string AudioOutputHint(Language? l = null) => Pick(l,
		$"Bilgisayarın sesinin çaldığı aygıt (Windows'un varsayılan çıkışı).",
		$"Where your PC's sound plays (the Windows default output).");

	public static string AudioInputHint(Language? l = null) => Pick(l,
		$"Oyunların ve sohbet uygulamalarının duyduğu mikrofon (Windows'un varsayılan mikrofonu). Kumandanın mikrofon tuşu bu mikrofonu açıp kapatır.",
		$"The microphone games and chat apps hear (the Windows default microphone). The controller's mic button switches this microphone.");

	// The virtual controller's own endpoints; slot: shown when several controllers run.
	public static string ControllerSpeakerDevice(int? slot = null, Language? l = null) => slot is int s
		? Pick(l, $"Kumanda Hoparlörü {s}", $"Controller Speaker {s}")
		: Pick(l, $"Kumanda Hoparlörü", $"Controller Speaker");

	public static string ControllerMicDevice(int? slot = null, Language? l = null) => slot is int s
		? Pick(l, $"Kumanda Mikrofonu {s}", $"Controller Microphone {s}")
		: Pick(l, $"Kumanda Mikrofonu", $"Controller Microphone");

	public static string AudioDevicesUnavailable(Language? l = null) => Pick(l,
		$"Bu Windows'ta varsayılan aygıt buradan değiştirilemiyor; Windows ses ayarlarını kullan.",
		$"The default device can't be changed from here on this Windows; use the Windows sound settings.");

	// The Quick Settings card has one short line (the tooltip has the full sentence).
	public static string AudioDevicesUnavailableShort(Language? l = null) => Pick(l,
		$"Ses aygıtı buradan seçilemiyor.",
		$"Sound devices can't be picked here.");

	public static string AudioDeviceNotChanged(Language? l = null) => Pick(l,
		$"Windows varsayılan aygıtı değiştirmedi.",
		$"Windows did not change the default device.");

	public static string MicButtonModeLabel(Language? l = null) => Pick(l, $"Mikrofon Tuşu", $"Mic Button");

	public static string MicButtonModeHint(Language? l = null) => Pick(l,
		$"Kumandanın mikrofon tuşu ve Hızlı Ayarlar'daki Mikrofon kutucuğu ne yapsın. Sessize Al: kumanda mikrofonu açık kalır, yalnızca sesin kesilir (hemen geri gelir). Tamamen Kapat: kumanda mikrofonu kapanır (pil tasarrufu). Seçili mikrofon bilgisayarınki ise tuş onu sessize alır.",
		$"What the controller's mic button and the Microphone tile in Quick Settings do. Mute: the controller microphone stays on and only your voice is cut (back at once). Turn Off: the controller microphone turns off (saves battery). If a PC microphone is selected, the button mutes it.");

	public static string MicButtonMute(Language? l = null) => Pick(l, $"Sessize Al", $"Mute");

	public static string MicButtonTurnOff(Language? l = null) => Pick(l, $"Tamamen Kapat", $"Turn Off");

	// The Quick Settings row has room for a short word only (the tooltip explains).
	public static string MicButtonTurnOffShort(Language? l = null) => Pick(l, $"Kapat", $"Turn Off");

	public static string MicStateMuted(Language? l = null) => Pick(l, $"Sessizde", $"Muted");

	// The toast after a press of the mic button: the title says what happened in the button's mode ("Kapat": on / off,
	// "Sessize Al": on / muted; a PC microphone is muted in both, shown as "off" in the "Kapat" mode), the body which
	// microphone it was (or why nothing changed).
	public static string MicToastTitle(Audio.MicPress press, Audio.MicButtonMode mode, Language? l = null) => press switch
	{
		Audio.MicPress.ControllerOn or Audio.MicPress.ControllerUnmuted or Audio.MicPress.PcUnmuted => Pick(l, $"Mikrofon Açık", $"Microphone On"),
		Audio.MicPress.ControllerMuted => Pick(l, $"Mikrofon Sessizde", $"Microphone Muted"),
		Audio.MicPress.PcMuted when mode == Audio.MicButtonMode.Mute => Pick(l, $"Mikrofon Sessizde", $"Microphone Muted"),
		_ => Pick(l, $"Mikrofon Kapalı", $"Microphone Off")
	};

	public static string MicToastBody(Audio.MicPress press, Language? l = null) => press switch
	{
		Audio.MicPress.PcMuted or Audio.MicPress.PcUnmuted => Pick(l, $"Bilgisayar Mikrofonu", $"PC Microphone"),
		Audio.MicPress.Ignored => Pick(l, $"Düşük Güç Açık", $"Low Power On"),
		_ => Pick(l, $"Kumanda Mikrofonu", $"Controller Microphone")
	};

	// Quick Settings: the shortcuts tile is half as wide now.
	public static string QuickShortcutsShort(Language? l = null) => Pick(l, $"Kısayollar", $"Shortcuts");

	public static string QuickOutputTip(Language? l = null) => Pick(l,
		$"Bilgisayarın sesi nereden çalsın. Kumanda Hoparlörü'nü seçersen tüm ses kumandaya gider.",
		$"Where your PC's sound plays. Pick the Controller Speaker to send all sound to the controller.");

	public static string QuickInputTip(Language? l = null) => Pick(l,
		$"Oyunların ve sohbet uygulamalarının duyduğu mikrofon. Mikrofon tuşu ve Mikrofon kutucuğu bu mikrofonu açıp kapatır.",
		$"The microphone games and chat apps hear. The mic button and the Microphone tile switch this microphone.");

	public static string QuickMicButtonTip(Language? l = null) => Pick(l,
		$"Sessize Al: kumanda mikrofonu açık kalır, sesin hemen kesilip geri gelir. Tamamen Kapat: kumanda mikrofonu kapanır.",
		$"Mute: the controller microphone stays on, your voice is cut and back at once. Turn Off: the controller microphone turns off.");

	public static string QuickMicTileTip(Language? l = null) => Pick(l,
		$"Kumandanın mikrofon tuşuyla aynı işi yapar: seçili mikrofonu açar, sessize alır ya da kapatır.",
		$"Does the same as the controller's mic button: turns the selected microphone on, mutes it or turns it off.");
}
