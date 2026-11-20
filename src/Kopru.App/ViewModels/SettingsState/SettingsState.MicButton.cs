// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Audio;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// The mic button ("Mikrofon Tuşu", global, not per profile) and the microphone it acts on: always the Windows default
// recording device chosen in the "Mikrofon" picker (Core MicButtonRules). The controller's button and the Quick Settings
// Microphone tile do the same thing (PressMicButton); the Settings "pass microphone to games" switch stays the controller
// microphone feature switch (MicEnabled). MicMuted (the "Mute" mode's state) lasts for this run only.
public sealed partial class SettingsState
{
	[ObservableProperty]
	private MicButtonMode _micButtonMode = MicButtonMode.TurnOff;

	[ObservableProperty]
	private bool _micMuted;

	private AudioDeviceSnapshot _audio = AudioDeviceSnapshot.Empty;
	private bool? _pcMicMuted;

	// The device pickers (Quick Settings and Settings share them).
	public AudioDevicesViewModel Audio { get; private set; } = null!;

	// The default recording device is the controller's microphone (or there is none).
	public bool ControllerMicSelected => _audio.ControllerMicSelected;

	// The selected PC microphone's mute switch as last read (or just set); null: unknown.
	public bool? PcMicMuted => _pcMicMuted;

	public MicState MicState => MicButtonRules.State(ControllerMicSelected, MicEnabled, MicMuted, PcMicMuted, LowPowerOn);

	public bool MicLive => MicState == MicState.On;

	public string MicStateText => MicState switch
	{
		MicState.On => Text.QuickStateOn(),
		MicState.Muted => Text.MicStateMuted(),
		_ => Text.QuickStateOff()
	};

	// Low Power holds the controller microphone off; a PC microphone can still be muted.
	public bool CanPressMic => !(LowPowerOn && ControllerMicSelected);

	// The names of the values above (not settings: no save, no push of their own).
	private static readonly HashSet<string> MicStateNames =
		[nameof(MicState), nameof(MicLive), nameof(MicStateText), nameof(CanPressMic), nameof(ControllerMicSelected), nameof(PcMicMuted)];

	partial void OnMicButtonModeChanged(MicButtonMode value) =>
		Kopru.Core.Diagnostics.AppLog.Write($"settings: mic button mode {(value == MicButtonMode.Mute ? "mute" : "turn off")} ({ChangeSource})");

	partial void OnMicMutedChanged(bool value) => NotifyMicState();

	// The controller's mic button (fromButton: a toast says what happened) or the Quick Settings tile.
	public void PressMicButton(bool fromButton = true)
	{
		bool controller = ControllerMicSelected;
		MicPress press = MicButtonRules.Press(controller, MicButtonMode, MicEnabled, MicMuted, PcMicMuted, LowPowerOn);
		Kopru.Core.Diagnostics.AppLog.Write($"mic button: {press} ({(fromButton ? "button" : "tile")}, {(MicButtonMode == MicButtonMode.Mute ? "mute" : "turn off")} mode, {(controller ? "controller" : "PC")} microphone selected{(LowPowerOn ? ", Low Power" : string.Empty)})");
		_micFromButton = fromButton;
		try
		{
			switch (press)
			{
				case MicPress.Ignored:
					// Low Power holds the controller microphone off: nothing changes (the toast says why).
					break;
				case MicPress.ControllerOn:
					MicMuted = false;
					MicEnabled = true;
					break;
				case MicPress.ControllerOff:
					MicEnabled = false;
					break;
				case MicPress.ControllerMuted:
				case MicPress.ControllerUnmuted:
					MicMuted = press == MicPress.ControllerMuted;
					break;
				default:
					bool mute = press == MicPress.PcMuted;
					_pcMicMuted = mute;
					Audio.SetMicMute(mute);
					NotifyMicState();
					Push();
					break;
			}
		}
		finally
		{
			_micFromButton = false;
		}
		if (fromButton)
		{
			ShortcutFeedback?.Invoke(Text.MicToastTitle(press, MicButtonMode), Text.MicToastBody(press));
		}
	}

	// Constructor: the pickers, and the selected microphone's state from them.
	private void InitAudioDevices()
	{
		Audio = new AudioDevicesViewModel(_service);
		Audio.SnapshotChanged += OnAudioSnapshot;
		OnAudioSnapshot(Audio.Snapshot);
	}

	private void OnAudioSnapshot(AudioDeviceSnapshot snapshot)
	{
		_audio = snapshot;
		_pcMicMuted = snapshot.CaptureMuted;
		NotifyMicState();
		// The mute LED follows a selected PC microphone (MicSettings.Live).
		Push();
	}

	private void NotifyMicState()
	{
		foreach (string name in MicStateNames)
		{
			OnPropertyChanged(name);
		}
	}
}
