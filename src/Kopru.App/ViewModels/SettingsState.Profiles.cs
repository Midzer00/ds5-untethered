// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Services;
using Kopru.Core.Input;
using Kopru.Core.Lighting;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;

namespace Kopru.App.ViewModels;

// Profiles: the settings below (ProfileValues) are the active profile's; changing one updates that profile. A known game
// with its own profile activates it when it is recognised, any other game (or none) the default profile; a profile chosen
// by hand or with a shortcut stays until the game in front changes. Controller shortcuts are handled here too.
public sealed partial class SettingsState
{
	public const double HapticsStep = 10;
	public const double HapticsMax = 300;

	private ProfileBook _profiles = new();
	private bool _applyingProfile;
	private string? _profileGameKey;
	private ProfilesViewModel? _profilesPanel;
	private ShortcutsViewModel? _shortcutsPanel;

	// Controller shortcuts with the PS button held.
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PsOpensAppInGamesAvailable))]
	private bool _shortcutsEnabled = true;

	// "PS: Open The App": a single PS tap with no game in front brings the window up (only with the shortcuts switch on).
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(PsOpensAppInGamesAvailable))]
	private bool _psOpensApp = true;

	// "Also In Games": the tap opens the window while a game is in front too (only with "PS: Open The App" on).
	[ObservableProperty]
	private bool _psOpensAppInGames = true;

	// Whether "Also In Games" can be changed (both switches above it on).
	public bool PsOpensAppInGamesAvailable => ShortcutsEnabled && PsOpensApp;

	public ProfileBook Profiles => _profiles;

	// The profiles and shortcuts groups on the Settings page.
	public ProfilesViewModel ProfilesPanel => _profilesPanel ??= new ProfilesViewModel(this);

	public ShortcutsViewModel ShortcutsPanel => _shortcutsPanel ??= new ShortcutsViewModel(this);

	// The active profile's name as shown (Default localized).
	public string ActiveProfileName => DisplayName(_profiles.Active);

	// "Profile · Racing" for the sidebar card.
	public string ActiveProfileLine => Text.ProfileLabel() + " · " + ActiveProfileName;

	// Raised (UI thread) after the profiles or the active one changed.
	public event Action? ProfilesChanged;

	// A toast after a shortcut: title, body.
	public event Action<string, string>? ShortcutFeedback;

	public static string DisplayName(ControllerProfile profile) => profile.IsDefault ? Text.ProfileDefault() : profile.Name;

	public ProfileValues CaptureValues() => new()
	{
		GameHapticsLevel = GameHapticsLevel,
		TriggerStrength = TriggerStrength,
		LightFromGame = LightFromGame,
		RumbleLevel = RumbleLevel,
		RumbleFeel = RumbleFeel,
		FallbackTrigger = FallbackTrigger,
		FallbackStrength = FallbackStrength,
		Tuning = Tuning,
		LightbarColour = LightbarColour,
		Brightness = Brightness,
		PlayerLeds = PlayerLeds,
		Remap = Remap,
		Gyro = Gyro,
		LightMode = LightMode,
		BreathingPeriod = BreathingPeriod,
		BreathingMin = BreathingMin,
		BreathingMax = BreathingMax,
		RainbowSpeed = RainbowSpeed,
		PlayerLedPattern = PlayerLedPattern,
		PlayerLedBrightness = PlayerLedBrightness,
		SpeakerEnabled = SpeakerEnabled,
		SpeakerVolume = SpeakerVolume,
		HeadphoneVolume = HeadphoneVolume,
		MicEnabled = MicEnabled,
		MicGain = MicGain,
		MicNoiseGate = MicNoiseGate
	};

	// Makes a profile the active one and loads its values. feedback: show its number on the player LEDs and a toast.
	public bool ActivateProfile(string id, bool feedback = false)
	{
		ControllerProfile? profile = _profiles.Find(id);
		if (profile == null)
		{
			return false;
		}
		_profiles.Activate(id);
		LoadValues(profile.Values);
		if (feedback)
		{
			int index = _profiles.IndexOf(id);
			_service.ShowProfileIndicator(index);
			ShortcutFeedback?.Invoke(Text.ShortcutProfileTitle(), Text.ShortcutProfileBody(index, DisplayName(profile)));
		}
		return true;
	}

	public void HandleShortcut(ShortcutAction action)
	{
		switch (action)
		{
			case ShortcutAction.NextProfile:
				ActivateProfile(_profiles.Step(1), feedback: true);
				break;
			case ShortcutAction.PreviousProfile:
				ActivateProfile(_profiles.Step(-1), feedback: true);
				break;
			case ShortcutAction.HapticsUp:
			case ShortcutAction.HapticsDown:
				double step = action == ShortcutAction.HapticsUp ? HapticsStep : -HapticsStep;
				GameHapticsLevel = Math.Clamp(Math.Round((GameHapticsLevel + step) / HapticsStep) * HapticsStep, 0, HapticsMax);
				ShortcutFeedback?.Invoke(Text.ShortcutHapticsTitle(), Text.ShortcutHapticsBody(GameHapticsLevel));
				break;
			case ShortcutAction.ToggleSupportedMode:
				bool on = !_service.Games.IsSupportedModeOn;
				_service.Games.SetSupportedMode(on);
				ShortcutFeedback?.Invoke(Text.ShortcutModeTitle(), Text.ShortcutModeBody(on));
				break;
			case ShortcutAction.ToggleDesktopMode:
				DesktopModeOn = !DesktopModeOn;
				break;
			case ShortcutAction.PowerOff:
				_ = _service.PowerOffAsync();
				break;
			case ShortcutAction.MicButton:
				PressMicButton();
				break;
		}
	}

	// Constructor: the saved profiles (or the loaded settings as the default profile), then the active one's values.
	private void InitProfiles(AppSettings? saved)
	{
		_profiles = ProfileBook.Load(saved?.Profiles, saved?.ActiveProfile, saved?.GameProfiles, CaptureValues());
		ProfileValues active = _profiles.Active.Values;
		if (active != CaptureValues())
		{
			_applyingProfile = true;
			try
			{
				SetValues(active);
			}
			finally
			{
				_applyingProfile = false;
			}
		}
		_profiles.Changed += () => RunOnUi(() =>
		{
			NotifyProfileName();
			ProfilesChanged?.Invoke();
			ScheduleSave();
		});
		_profileGameKey = _service.Games.ActiveGame?.Key;
		_service.Games.StateChanged += _ => RunOnUi(OnGameChanged);
		_service.Shortcut += (_, action) => RunOnUi(() => HandleShortcut(action));
		Localizer.LanguageChanged += _ => RunOnUi(() => NotifyProfileName());
	}

	// The known game in front changed: its profile, or the default one.
	private void OnGameChanged()
	{
		string? key = _service.Games.ActiveGame?.Key;
		if (string.Equals(key, _profileGameKey, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		_profileGameKey = key;
		string target = _profiles.ResolveForGame(key);
		if (target != _profiles.ActiveId)
		{
			ActivateProfile(target);
		}
	}

	// A user change of a profile setting goes into the active profile.
	private void CaptureProfileValue(string property)
	{
		if (property is nameof(GameHapticsLevel) or nameof(TriggerStrength) or nameof(LightFromGame) or nameof(RumbleLevel)
			or nameof(RumbleFeel) or nameof(FallbackTrigger) or nameof(FallbackStrength) or nameof(Tuning) or nameof(LightbarColour)
			or nameof(Brightness) or nameof(PlayerLeds) or nameof(Remap) or nameof(Gyro) or nameof(LightMode) or nameof(BreathingPeriod)
			or nameof(BreathingMin) or nameof(BreathingMax) or nameof(RainbowSpeed) or nameof(PlayerLedPattern) or nameof(PlayerLedBrightness)
			or nameof(SpeakerEnabled) or nameof(SpeakerVolume) or nameof(HeadphoneVolume)
			or nameof(MicEnabled) or nameof(MicGain) or nameof(MicNoiseGate))
		{
			_profiles.Update(_profiles.ActiveId, CaptureValues());
		}
	}

	private void LoadValues(ProfileValues values)
	{
		if (values == CaptureValues())
		{
			NotifyProfileName();
			return;
		}
		_applyingProfile = true;
		try
		{
			SetValues(values);
		}
		finally
		{
			_applyingProfile = false;
		}
		NotifyProfileName();
		Push();
		ScheduleSave();
	}

	private void NotifyProfileName()
	{
		OnPropertyChanged(nameof(ActiveProfileName));
		OnPropertyChanged(nameof(ActiveProfileLine));
	}

	private void SetValues(ProfileValues v)
	{
		GameHapticsLevel = v.GameHapticsLevel;
		TriggerStrength = Math.Clamp(v.TriggerStrength, 0, 100);
		LightFromGame = v.LightFromGame;
		RumbleLevel = v.RumbleLevel;
		RumbleFeel = v.RumbleFeel;
		FallbackTrigger = v.FallbackTrigger;
		FallbackStrength = v.FallbackStrength;
		Tuning = v.Tuning ?? InputTuning.Default;
		LightbarColour = v.LightbarColour;
		Brightness = v.Brightness;
		PlayerLeds = v.PlayerLeds;
		Remap = v.Remap ?? RemapSettings.Default;
		GyroSettings gyro = v.Gyro ?? GyroSettings.Default;
		Gyro = gyro with { BiasX = Gyro.BiasX, BiasY = Gyro.BiasY, BiasZ = Gyro.BiasZ };
		LightMode = Enum.IsDefined(v.LightMode) ? v.LightMode : LightMode.Static;
		BreathingPeriod = Math.Clamp(v.BreathingPeriod, 1, 10);
		BreathingMin = Math.Clamp(v.BreathingMin, 0, 100);
		BreathingMax = Math.Clamp(v.BreathingMax, 0, 100);
		RainbowSpeed = Math.Clamp(v.RainbowSpeed, 1, 10);
		// Last: the pattern and the PlayerLeds switch keep each other in step.
		PlayerLedPattern = v.PlayerLedPattern ?? (v.PlayerLeds ? PlayerLedPattern.One : PlayerLedPattern.Off);
		PlayerLedBrightness = v.PlayerLedBrightness;
		SpeakerEnabled = v.SpeakerEnabled;
		SpeakerVolume = Math.Clamp(v.SpeakerVolume, 0, 100);
		HeadphoneVolume = Math.Clamp(v.HeadphoneVolume, 0, 100);
		MicEnabled = v.MicEnabled;
		MicGain = Math.Clamp(v.MicGain, 0, MaxMicGain);
		MicNoiseGate = v.MicNoiseGate;
	}
}

public static class ProfileSettingsExtensions
{
	// Takes the profiles to save.
	public static AppSettings WithProfiles(this AppSettings settings, ProfileBook book)
	{
		settings.Profiles = book.Profiles.ToList();
		settings.ActiveProfile = book.ActiveId;
		settings.GameProfiles = new Dictionary<string, string>(book.GameProfiles, StringComparer.OrdinalIgnoreCase);
		return settings;
	}
}
