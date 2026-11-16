// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;

namespace Kopru.App.ViewModels;

// The feel settings of one profile: the game's own effects (haptics, trigger strength), classic rumble (level, feel), the
// fallback trigger effect (and its strength) and the game's light. The profile book is the one source of truth: the active
// profile is edited through SettingsState (which applies it live and saves it into the book), any other profile in the book
// directly. Haptics And Triggers edits the active profile; a game's panel on the Games page edits that game's profile.
public sealed class ProfileFeelViewModel : ObservableObject
{
	private static readonly HashSet<string> FeelNames =
	[
		nameof(SettingsState.GameHapticsLevel), nameof(SettingsState.TriggerStrength), nameof(SettingsState.LightFromGame),
		nameof(SettingsState.RumbleLevel), nameof(SettingsState.RumbleFeel), nameof(SettingsState.FallbackTrigger),
		nameof(SettingsState.FallbackStrength)
	];

	private readonly SettingsState _settings;
	private readonly Func<string>? _profileId;

	// profileId: the profile edited (read each time; null: the active profile).
	public ProfileFeelViewModel(SettingsState settings, Func<string>? profileId = null)
	{
		_settings = settings;
		_profileId = profileId;
		settings.PropertyChanged += OnSettingsChanged;
		settings.ProfilesChanged += Refresh;
	}

	public string ProfileId => _profileId?.Invoke() ?? _settings.Profiles.ActiveId;

	// The edited profile is the active one: its changes apply to the controller now.
	public bool IsLive => ProfileId == _settings.Profiles.ActiveId;

	private ProfileValues Values => IsLive ? _settings.CaptureValues() : _settings.Profiles.Find(ProfileId)?.Values ?? _settings.Profiles.Default.Values;

	// Labels (shared by every page that shows these settings).
	public string GameCardTitle => Text.GameEffectsCard();
	public string GameCardHint => Text.GameEffectsCardHint();
	public string GameHapticsLabel => Text.GameHapticsLevel();
	public string GameHapticsHint => Text.GameHapticsHint();
	public string TriggerLabel => Text.TriggerStrength();
	public string TriggerHint => Text.TriggerStrengthHint();
	public string RumbleCardTitle => Text.ClassicRumbleCard();
	public string RumbleCardHint => Text.ClassicRumbleCardHint();
	public string RumbleLabel => Text.RumbleLevel();
	public string RumbleHint => Text.RumbleHint();
	public string FeelLabel => Text.RumbleFeelLabel();
	public string FeelHint => Text.RumbleFeelHint();
	public string SoftText => Text.RumbleFeelSoft();
	public string PunchyText => Text.RumbleFeelPunchy();
	public string FallbackLabel => Text.FallbackTriggerLabel();
	public string FallbackHint => Text.FallbackTriggerHint();
	public string FallbackStrengthLabel => Text.FallbackStrength();
	public string OffText => Text.EffectOff();
	public string FeedbackText => Text.EffectFeedback();
	public string WeaponText => Text.EffectWeapon();
	public string VibrationText => Text.EffectVibration();
	public string LightLabel => Text.LightFromGame();
	public string LightHint => Text.LightFromGameHint();

	public double GameHapticsLevel
	{
		get => Values.GameHapticsLevel;
		set => Set(v => v with { GameHapticsLevel = value }, s => s.GameHapticsLevel = value);
	}

	public double TriggerStrength
	{
		get => Values.TriggerStrength;
		set => Set(v => v with { TriggerStrength = value }, s => s.TriggerStrength = value);
	}

	public double RumbleLevel
	{
		get => Values.RumbleLevel;
		set => Set(v => v with { RumbleLevel = value }, s => s.RumbleLevel = value);
	}

	public double FallbackStrength
	{
		get => Values.FallbackStrength;
		set => Set(v => v with { FallbackStrength = value }, s => s.FallbackStrength = value);
	}

	public bool LightFromGame
	{
		get => Values.LightFromGame;
		set => Set(v => v with { LightFromGame = value }, s => s.LightFromGame = value);
	}

	public string GameHapticsText => Text.PercentValue(Math.Round(GameHapticsLevel));
	public string TriggerStrengthText => Text.PercentValue(Math.Round(TriggerStrength));
	public string RumbleText => Text.PercentValue(Math.Round(RumbleLevel));
	public string FallbackStrengthText => Text.PercentValue(Math.Round(FallbackStrength));
	public bool HasFallback => Values.FallbackTrigger != FallbackTrigger.Off;

	// Chip toggles: checking one picks it; unchecking the picked one leaves it picked.
	public bool IsSoft { get => Values.RumbleFeel == RumbleFeel.Soft; set => SetFeel(RumbleFeel.Soft); }
	public bool IsPunchy { get => Values.RumbleFeel == RumbleFeel.Punchy; set => SetFeel(RumbleFeel.Punchy); }
	public bool IsFallbackOff { get => Values.FallbackTrigger == FallbackTrigger.Off; set => SetFallback(FallbackTrigger.Off); }
	public bool IsFallbackFeedback { get => Values.FallbackTrigger == FallbackTrigger.Feedback; set => SetFallback(FallbackTrigger.Feedback); }
	public bool IsFallbackWeapon { get => Values.FallbackTrigger == FallbackTrigger.Weapon; set => SetFallback(FallbackTrigger.Weapon); }
	public bool IsFallbackVibration { get => Values.FallbackTrigger == FallbackTrigger.Vibration; set => SetFallback(FallbackTrigger.Vibration); }

	private void SetFeel(RumbleFeel feel) => Set(v => v with { RumbleFeel = feel }, s => s.RumbleFeel = feel);

	private void SetFallback(FallbackTrigger trigger) => Set(v => v with { FallbackTrigger = trigger }, s => s.FallbackTrigger = trigger);

	// The active profile through SettingsState (live, saved into the book); another profile straight into the book.
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
		if (e.PropertyName != null && FeelNames.Contains(e.PropertyName) && IsLive)
		{
			Refresh();
		}
	}
}

// "Sesi Titreşime Çevir" for the game panel: the switch, its strength (0-200 %), Bass or Full Sound, and a status line.
// ProfileAudioHapticsViewModel forwards these to SettingsState for the live profile and to ProfileValues for another one.
public interface IAudioHapticsSettings : INotifyPropertyChanged
{
	bool Available { get; }
	bool On { get; set; }
	double Strength { get; set; }
	bool IsBass { get; set; }
	bool IsFull { get; set; }
	string Status { get; }
}
