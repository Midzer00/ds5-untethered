// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Lighting;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// Controller microphone settings: the switch (also the controller's mic button), gain (percent), noise gate and the
// Windows mute option.
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

	[ObservableProperty]
	private bool _micMuteWindows;

	public string MicGainText => Text.PercentValue(Math.Round(MicGain));

	public MicSettings ToMicSettings() => new(MicEnabled, Math.Clamp(MicGain / 100, 0, MaxMicGain / 100), MicNoiseGate, MicMuteWindows);

	// Turning the mic on by hand makes the mute LED follow it (unless the user picked "always on").
	partial void OnMicEnabledChanged(bool value)
	{
		Kopru.Core.Diagnostics.AppLog.Write($"settings: mic switch {(value ? "on" : "off")} ({ChangeSource})");
		if (value && !_loading && !_applyingProfile && MuteLed == MuteLedMode.Off)
		{
			MuteLed = MuteLedMode.FollowMic;
		}
	}

	private bool _micFromButton;

	// The controller's mic button (UI thread): sets the same switch as the tile and the Settings page and shows a toast.
	// Not while Low Power holds the mic off (the bridge does not handle the button then; this is the second guard).
	public void ApplyMicButton(bool on)
	{
		if (LowPowerOn)
		{
			return;
		}
		if (MicEnabled != on)
		{
			_micFromButton = true;
			try
			{
				MicEnabled = on;
			}
			finally
			{
				_micFromButton = false;
			}
		}
		ShortcutFeedback?.Invoke(Text.MicButtonToastTitle(on), Text.MicButtonToastBody(on));
	}

	private void LoadMic(AppSettings saved)
	{
		MicEnabled = saved.MicEnabled;
		MicGain = Math.Clamp(saved.MicGain, 0, MaxMicGain);
		MicNoiseGate = saved.MicNoiseGate;
		MicMuteWindows = saved.MicMuteWindows;
	}
}
