// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Audio;

// What the controller's mic button does (setting "Mikrofon Tuşu"): TurnOff switches the controller microphone feature
// (the Bluetooth mic stream) on and off; Mute keeps the stream and silences what games get.
public enum MicButtonMode
{
	TurnOff,
	Mute
}

// The microphone the button acts on, as the Quick Settings tile shows it.
public enum MicState
{
	On,
	Muted,
	Off
}

public enum MicPress
{
	// Low Power holds the controller microphone off.
	Ignored,
	ControllerOn,
	ControllerOff,
	ControllerMuted,
	ControllerUnmuted,
	PcMuted,
	PcUnmuted
}

// The button (and the Quick Settings tile, which mirrors it) always acts on the Windows default recording device:
//  - the controller's microphone (or no default at all): TurnOff switches the feature, Mute mutes / unmutes the audio we
//    feed (a first press while the feature is off turns it on, unmuted); Low Power: no change;
//  - another PC microphone: mutes / unmutes that endpoint, in either mode, also under Low Power.
public static class MicButtonRules
{
	public static MicPress Press(bool controllerSelected, MicButtonMode mode, bool micEnabled, bool micMuted, bool? pcMuted, bool lowPower)
	{
		if (!controllerSelected)
		{
			return pcMuted == true ? MicPress.PcUnmuted : MicPress.PcMuted;
		}
		if (lowPower)
		{
			return MicPress.Ignored;
		}
		if (!micEnabled)
		{
			return MicPress.ControllerOn;
		}
		if (mode == MicButtonMode.TurnOff)
		{
			return MicPress.ControllerOff;
		}
		return micMuted ? MicPress.ControllerUnmuted : MicPress.ControllerMuted;
	}

	public static MicState State(bool controllerSelected, bool micEnabled, bool micMuted, bool? pcMuted, bool lowPower)
	{
		if (!controllerSelected)
		{
			return pcMuted == true ? MicState.Muted : MicState.On;
		}
		if (!micEnabled || lowPower)
		{
			return MicState.Off;
		}
		return micMuted ? MicState.Muted : MicState.On;
	}

	// What a press leads to (for the toast).
	public static MicState After(MicPress press) => press switch
	{
		MicPress.ControllerOn or MicPress.ControllerUnmuted or MicPress.PcUnmuted => MicState.On,
		MicPress.ControllerMuted or MicPress.PcMuted => MicState.Muted,
		_ => MicState.Off
	};
}
