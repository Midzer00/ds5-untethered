// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Audio;
using Kopru.Core.Localization;
using AudioHapticsState = Kopru.Core.Audio.AudioHapticsStatus;

namespace Kopru.App.ViewModels;

// "Turn sound into haptics" (Sesi Titreşime Çevir): part of the active profile (so it is per game: a game's profile is
// active while it runs). The switch, the strength (0-200 %) and the band go to the service with the other settings; the
// status line follows what the service's runner does (AudioHapticsRunner).
public sealed partial class SettingsState
{
	public const double AudioHapticsStrengthMax = 200;

	private static readonly HashSet<string> AudioHapticsStateNames = [nameof(AudioHapticsStatus), nameof(AudioHapticsAvailable)];

	private AudioHapticsState _audioHapticsState = AudioHapticsState.Off;
	private bool _audioHapticsWired;

	[ObservableProperty]
	private bool _audioHapticsOn;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(AudioHapticsStrengthText))]
	private double _audioHapticsStrength = 100;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsAudioHapticsBass))]
	[NotifyPropertyChangedFor(nameof(IsAudioHapticsFull))]
	private AudioHapticsBand _audioHapticsBand = AudioHapticsBand.Bass;

	public string AudioHapticsStrengthText => Text.PercentValue(Math.Round(AudioHapticsStrength));

	// Radio-button helpers for the band.
	public bool IsAudioHapticsBass
	{
		get => AudioHapticsBand == AudioHapticsBand.Bass;
		set
		{
			if (value)
			{
				AudioHapticsBand = AudioHapticsBand.Bass;
			}
		}
	}

	public bool IsAudioHapticsFull
	{
		get => AudioHapticsBand == AudioHapticsBand.Full;
		set
		{
			if (value)
			{
				AudioHapticsBand = AudioHapticsBand.Full;
			}
		}
	}

	// A short status line: off / listening to <game> / waiting for the game / the game's own haptics in use / not available.
	public string AudioHapticsStatus => StatusText(_audioHapticsState);

	// False once this Windows could not capture the game's sound.
	public bool AudioHapticsAvailable => _audioHapticsState.Kind != AudioHapticsStatusKind.Unavailable;

	public static string StatusText(AudioHapticsState status) => status.Kind switch
	{
		AudioHapticsStatusKind.Listening when status.Mode == AudioCaptureMode.SystemLoopback => Text.AudioHapticsStatusListeningSystem(status.Game ?? string.Empty),
		AudioHapticsStatusKind.Listening => Text.AudioHapticsStatusListening(status.Game ?? string.Empty),
		AudioHapticsStatusKind.WaitingForGame => Text.AudioHapticsStatusWaiting(),
		AudioHapticsStatusKind.NativeHaptics => Text.AudioHapticsStatusNative(),
		AudioHapticsStatusKind.Unavailable => Text.AudioHapticsStatusUnavailable(),
		_ => Text.AudioHapticsStatusOff()
	};

	partial void OnAudioHapticsStrengthChanged(double value)
	{
		double clamped = Math.Clamp(double.IsFinite(value) ? value : 100, 0, AudioHapticsStrengthMax);
		if (clamped != value)
		{
			AudioHapticsStrength = clamped;
		}
	}

	private void InitAudioHaptics()
	{
		if (_audioHapticsWired)
		{
			return;
		}
		_audioHapticsWired = true;
		_service.AudioHapticsStatusChanged += () => RunOnUi(RefreshAudioHapticsStatus);
		Localizer.LanguageChanged += _ => RunOnUi(() => OnPropertyChanged(nameof(AudioHapticsStatus)));
		RefreshAudioHapticsStatus();
	}

	private void RefreshAudioHapticsStatus()
	{
		AudioHapticsState next = _service.AudioHapticsStatus;
		if (next == _audioHapticsState)
		{
			return;
		}
		bool availableChanged = (next.Kind == AudioHapticsStatusKind.Unavailable) != (_audioHapticsState.Kind == AudioHapticsStatusKind.Unavailable);
		_audioHapticsState = next;
		OnPropertyChanged(nameof(AudioHapticsStatus));
		if (availableChanged)
		{
			OnPropertyChanged(nameof(AudioHapticsAvailable));
		}
	}
}
