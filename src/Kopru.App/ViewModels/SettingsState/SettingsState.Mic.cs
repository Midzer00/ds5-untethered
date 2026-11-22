// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// Controller microphone settings: the feature switch (the Bluetooth mic stream), gain (percent) and noise gate. What the
// mic button does is in SettingsState.MicButton.
public sealed partial class SettingsState
{
	public const double MaxMicGain = 300;

	[ObservableProperty]
	private bool _micEnabled;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(MicGainText))]
	private double _micGain = 100;

	[ObservableProperty]
	private bool _micNoiseGate = true;

	public string MicGainText => Text.PercentValue(Math.Round(MicGain));

	// Muted only while the feature is on; Live (the mute LED) follows a selected PC microphone instead.
	public MicSettings ToMicSettings() => new(MicEnabled, Math.Clamp(MicGain / 100, 0, MaxMicGain / 100), MicNoiseGate, MicEnabled && MicMuted)
	{
		Live = ControllerMicSelected ? null : PcMicMuted != true
	};

	// Turning the mic off ends a mute of the button's "Mute" mode.
	partial void OnMicEnabledChanged(bool value)
	{
		Kopru.Core.Diagnostics.AppLog.Write($"settings: mic switch {(value ? "on" : "off")} ({ChangeSource})");
		if (!value)
		{
			MicMuted = false;
		}
		NotifyMicState();
	}

	private bool _micFromButton;

	private void LoadMic(AppSettings saved)
	{
		MicEnabled = saved.MicEnabled;
		MicGain = Math.Clamp(saved.MicGain, 0, MaxMicGain);
		MicNoiseGate = saved.MicNoiseGate;
		MicButtonMode = saved.MicButtonMode;
	}
}
