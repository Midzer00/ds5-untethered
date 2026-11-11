// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// Settings -> Controller Microphone: labels and the live level meter (polled while the feature is on and the bridge runs).
public sealed partial class SettingsViewModel
{
	public static readonly TimeSpan MicMeterInterval = TimeSpan.FromMilliseconds(100);

	private DispatcherTimer? _micTimer;

	public string MicGroupLabel => Text.GroupControllerMic();
	public string MicSendLabel => Text.MicSendToGame();
	public string MicSendHint => Text.MicButtonHint();
	public string MicGainLabel => Text.MicGain();
	public string MicNoiseGateLabel => Text.MicNoiseGate();
	public string MicNoiseGateHint => Text.MicNoiseGateHint();
	public string MicLevelLabel => Text.MicLevel();

	// The level meter shows while the feature is on and the bridge runs.
	public bool MicActive => Settings.MicEnabled && _service.IsBridgeRunning;

	// 0..100 for the meter.
	public double MicLevel { get; private set; }

	// Reads the level from the service (the timer calls it; tests call it directly).
	public void RefreshMic()
	{
		if (_service is IMicLevelSource source && MicActive)
		{
			MicLevel = Math.Round(Math.Clamp(source.MicLevel, 0, 1) * 100);
		}
		else
		{
			MicLevel = 0;
		}
		OnPropertyChanged(nameof(MicActive));
		OnPropertyChanged(nameof(MicLevel));
	}

	private void InitMic()
	{
		Settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.MicEnabled))
			{
				RunOnUi(UpdateMicTimer);
			}
			else if (e.PropertyName == nameof(SettingsState.MicButtonMode))
			{
				OnPropertyChanged(nameof(MicButtonModeIndex));
			}
		};
		_service.StateChanged += (_, _) => RunOnUi(UpdateMicTimer);
		UpdateMicTimer();
	}

	private void UpdateMicTimer()
	{
		bool run = MicActive && _service is IMicLevelSource;
		if (run && _micTimer == null)
		{
			_micTimer = new DispatcherTimer { Interval = MicMeterInterval };
			_micTimer.Tick += (_, _) => RefreshMic();
			_micTimer.Start();
		}
		else if (!run && _micTimer != null)
		{
			_micTimer.Stop();
			_micTimer = null;
		}
		RefreshMic();
	}
}
