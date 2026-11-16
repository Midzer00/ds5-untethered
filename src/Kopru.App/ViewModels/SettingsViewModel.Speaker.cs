// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.ViewModels;

// Settings -> Controller Speaker: the one speaker switch (SettingsState.SpeakerEnabled) and why it plays nothing now.
public sealed partial class SettingsViewModel
{
	public string SpeakerSwitchLabel => Text.SpeakerSwitch();
	public string SpeakerSwitchHint => Text.SpeakerSwitchHint();
	public string SpeakerSwitchTip => Text.SpeakerSendGameAudioHint();

	// Empty while the speaker can play; otherwise Low Power, or a controller type without a speaker (shown whether the
	// switch is on or off, so the user knows before turning it on).
	public string SpeakerOffReason => Settings.LowPowerOn ? Text.SpeakerOffLowPower()
		: Settings.PadStatus.Current != VirtualPadType.DualSense ? Text.SpeakerOffPadType(SettingsState.PadTypeName(Settings.PadStatus.Current))
		: string.Empty;
	public bool HasSpeakerOffReason => SpeakerOffReason.Length > 0;

	// Called from the Settings change handler (InitMic).
	private void OnSpeakerStateChanged(string? property)
	{
		if (property is nameof(SettingsState.LowPowerOn) or nameof(SettingsState.PadStatus) or nameof(SettingsState.SpeakerWorks))
		{
			OnPropertyChanged(nameof(SpeakerOffReason));
			OnPropertyChanged(nameof(HasSpeakerOffReason));
		}
	}
}
