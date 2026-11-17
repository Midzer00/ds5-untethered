// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Audio;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;

namespace Kopru.App.ViewModels;

// "Sesi Titreşime Çevir" of one profile, like ProfileFeelViewModel: the active profile goes through SettingsState (applied
// live, its status is what the engine does now), any other profile straight into the profile book (its status: off, or
// waiting for the game when switched on, since it runs once its game is in front).
public sealed class ProfileAudioHapticsViewModel : ObservableObject, IAudioHapticsSettings
{
	private static readonly HashSet<string> LiveNames =
	[
		nameof(SettingsState.AudioHapticsOn), nameof(SettingsState.AudioHapticsStrength), nameof(SettingsState.AudioHapticsBand),
		nameof(SettingsState.AudioHapticsStatus), nameof(SettingsState.AudioHapticsAvailable)
	];

	private readonly SettingsState _settings;
	private readonly Func<string>? _profileId;

	// profileId: the profile edited (read each time; null: the active profile).
	public ProfileAudioHapticsViewModel(SettingsState settings, Func<string>? profileId = null)
	{
		_settings = settings;
		_profileId = profileId;
		settings.PropertyChanged += OnSettingsChanged;
		settings.ProfilesChanged += Refresh;
		Localizer.LanguageChanged += _ => Refresh();
	}

	public string ProfileId => _profileId?.Invoke() ?? _settings.Profiles.ActiveId;

	public bool IsLive => ProfileId == _settings.Profiles.ActiveId;

	private ProfileValues Values => _settings.Profiles.Find(ProfileId)?.Values ?? _settings.Profiles.Default.Values;

	// Strength and band are greyed out only while the running engine could not capture the game's sound; the switch never
	// is (it must always turn off, and turning it on again tries once more).
	public bool Available => !IsLive || _settings.AudioHapticsAvailable;

	public bool On
	{
		get => IsLive ? _settings.AudioHapticsOn : Values.AudioHaptics;
		set => Set(v => v with { AudioHaptics = value }, s => s.AudioHapticsOn = value);
	}

	public double Strength
	{
		get => IsLive ? _settings.AudioHapticsStrength : Math.Clamp(Values.AudioHapticsStrength, 0, SettingsState.AudioHapticsStrengthMax);
		set
		{
			double clamped = Math.Clamp(double.IsFinite(value) ? value : 100, 0, SettingsState.AudioHapticsStrengthMax);
			Set(v => v with { AudioHapticsStrength = clamped }, s => s.AudioHapticsStrength = clamped);
		}
	}

	public AudioHapticsBand Band => IsLive ? _settings.AudioHapticsBand : Values.AudioHapticsBand;

	// Chip toggles: checking one picks it; unchecking the picked one leaves it picked.
	public bool IsBass { get => Band == AudioHapticsBand.Bass; set => SetBand(AudioHapticsBand.Bass); }
	public bool IsFull { get => Band == AudioHapticsBand.Full; set => SetBand(AudioHapticsBand.Full); }

	public string Status => IsLive ? _settings.AudioHapticsStatus
		: Values.AudioHaptics ? Text.AudioHapticsStatusWaiting() : Text.AudioHapticsStatusOff();

	private void SetBand(AudioHapticsBand band) => Set(v => v with { AudioHapticsBand = band }, s => s.AudioHapticsBand = band);

	private void Set(Func<ProfileValues, ProfileValues> change, Action<SettingsState> live)
	{
		if (IsLive)
		{
			live(_settings);
		}
		else
		{
			_settings.Profiles.Update(ProfileId, change(Values));
		}
		Refresh();
	}

	public void Refresh() => OnPropertyChanged(string.Empty);

	private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(SettingsState.AudioHapticsAvailable) || (e.PropertyName != null && LiveNames.Contains(e.PropertyName) && IsLive))
		{
			Refresh();
		}
	}
}
