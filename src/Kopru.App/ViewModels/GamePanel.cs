// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;

namespace Kopru.App.ViewModels;

// The panel of one game on the Games page (opened by clicking its row): its picture, name and where it comes from, Play,
// Hide or Remove, its controller type and profile, and its feel. "Settings For This Game" gives the game a profile of its own
// (named after it, made once) and shows that profile's settings here; "Back To Default" puts it back on the default profile.
// One instance per page: it shows the game in Game (null: closed).
public sealed partial class GamePanelViewModel : ViewModelBase
{
	private readonly SettingsState _settings;
	private readonly Func<string?> _runningGameKey;

	public GamePanelViewModel(SettingsState settings, Func<string?> runningGameKey)
	{
		_settings = settings;
		_runningGameKey = runningGameKey;
		Feel = new ProfileFeelViewModel(settings, () => ProfileId);
		AudioHaptics = new ProfileAudioHapticsViewModel(settings, () => ProfileId);
		settings.ProfilesChanged += () => RunOnUi(RefreshProfile);
	}

	// The game shown (null: the panel is closed).
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOpen))]
	private GameItemViewModel? _game;

	partial void OnGameChanged(GameItemViewModel? value) => RefreshProfile();

	public bool IsOpen => Game != null;

	// The file "Oyna" starts, chosen by the user (wins over the way the app finds itself): choose, change, remove.
	public bool HasStartFile => Game != null && _settings.StartFileFor(Game.Key) != null;
	public bool HasNoStartFile => Game != null && !HasStartFile;
	public string StartFileLine => Game != null && _settings.StartFileFor(Game.Key) is { } path ? Text.StartFileLine(Path.GetFileName(path)) : string.Empty;
	public string StartFileTip => Game != null ? _settings.StartFileFor(Game.Key) ?? Text.StartFileHint() : string.Empty;
	public string ChooseStartFileText => Text.StartFileChoose();
	public string ChangeStartFileText => Text.StartFileChange();
	public string RemoveStartFileText => Text.StartFileRemove();
	public string StartFileHint => Text.StartFileHint();

	// Asks for the file (local path out; null when cancelled). Set by the view.
	public Func<Task<string?>>? PickStartFile { get; set; }

	[RelayCommand]
	private async Task ChooseStartFile()
	{
		GameItemViewModel? game = Game;
		if (game == null || PickStartFile == null || await PickStartFile() is not { Length: > 0 } path)
		{
			return;
		}
		if (_settings.SetStartFile(game.Key, path))
		{
			Kopru.Core.Diagnostics.AppLog.Write($"games: start file set for {game.Name} ({Path.GetFileName(path)})");
		}
		RefreshStartFile();
	}

	[RelayCommand]
	private void RemoveStartFile()
	{
		if (Game != null && _settings.SetStartFile(Game.Key, null))
		{
			Kopru.Core.Diagnostics.AppLog.Write($"games: start file cleared for {Game.Name}");
		}
		RefreshStartFile();
	}

	private void RefreshStartFile()
	{
		OnPropertyChanged(nameof(HasStartFile));
		OnPropertyChanged(nameof(HasNoStartFile));
		OnPropertyChanged(nameof(StartFileLine));
		OnPropertyChanged(nameof(StartFileTip));
		Game?.RefreshLaunch();
	}

	// The edited profile: the game's own, else the default one (the feel settings show only for the game's own).
	public ProfileFeelViewModel Feel { get; }

	// Sound to haptics of the edited profile (live through SettingsState when it is the active one).
	public IAudioHapticsSettings AudioHaptics { get; }

	private string ProfileId => Game == null ? ProfileBook.DefaultId : _settings.Profiles.ResolveForGame(Game.Key);

	// The game has a profile other than the default one: its settings are shown and edited here.
	public bool HasOwnProfile => Game != null && _settings.Profiles.ProfileForGame(Game.Key) is { } id && id != ProfileBook.DefaultId;
	public bool UsesDefault => Game != null && !HasOwnProfile;

	// A game in the supported list: the game's own effects come first; for the others classic rumble and sound to haptics.
	public bool IsSupported => Game is { IsUnsupported: false };
	public bool IsUnsupported => Game is { IsUnsupported: true };

	// "Steam", "Epic", "Eklendi", or "Listede" (with "Yüklü" when found here).
	public string SourceText => Game switch
	{
		null => string.Empty,
		{ IsFound: true } g => g.FoundText,
		{ IsUserAdded: true } => Text.MarkUserAdded(),
		{ IsInstalled: true } => GameModeText.Join(Text.GameListed(), Text.InstalledMark()),
		_ => Text.GameListed()
	};

	public string ProfileLine => Game == null ? string.Empty : Text.ControllerProfileLine(SettingsState.DisplayName(_settings.Profiles.Find(ProfileId) ?? _settings.Profiles.Default));
	public string UsesDefaultText => Text.GameUsesDefaultProfile();
	public string OwnSettingsText => Text.GameOwnSettings();
	public string OwnSettingsHint => Text.GameOwnSettingsHint();
	public string ResetText => Text.GameResetToDefault();
	public string ResetHint => Text.GameResetToDefaultHint();
	public string CloseText => Text.GamePanelClose();
	public string PadTypeLabel => Text.PadTypeLabel();
	public string ProfileLabel => Text.ProfileLabel();
	public string AudioTitle => Text.AudioHapticsLabel();
	public string AudioHint => Text.AudioHapticsHint();
	public string AudioRumbleNote => Text.AudioHapticsRumbleNote();
	public string AudioStrengthLabel => Text.AudioHapticsStrengthLabel();
	public string AudioBassText => Text.AudioHapticsBandBass();
	public string AudioFullText => Text.AudioHapticsBandFull();

	[RelayCommand]
	private void Close() => Game = null;

	// "Settings For This Game": the profile named after the game (made from the default one's values once, or the one that
	// already has that name), assigned to it; if the game is running it becomes the active profile so changes apply now.
	[RelayCommand]
	private void UseOwnSettings()
	{
		if (Game == null || HasOwnProfile)
		{
			return;
		}
		string name = Game.Name.Length > ProfileBook.MaxNameLength ? Game.Name[..ProfileBook.MaxNameLength].TrimEnd() : Game.Name;
		ControllerProfile? profile = _settings.Profiles.Profiles.FirstOrDefault(p => !p.IsDefault && string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase))
			?? _settings.Profiles.Create(name, _settings.Profiles.Default.Values);
		if (profile == null)
		{
			return;
		}
		_settings.Profiles.AssignGame(Game.Key, profile.Id);
		ActivateIfRunning(profile.Id);
		RefreshProfile();
	}

	// "Back To Default": the game uses the default profile again (its own profile is kept for later).
	[RelayCommand]
	private void ResetToDefault()
	{
		if (Game == null)
		{
			return;
		}
		_settings.Profiles.AssignGame(Game.Key, null);
		ActivateIfRunning(ProfileBook.DefaultId);
		RefreshProfile();
	}

	[RelayCommand]
	private void Play() => Game?.PlayGameCommand.Execute(null);

	// Hide (a found game) or Remove (an added one): the row leaves the list, the panel closes.
	[RelayCommand]
	private void HideOrRemove()
	{
		GameItemViewModel? game = Game;
		Game = null;
		if (game?.IsFound == true)
		{
			game.HideGameCommand.Execute(null);
		}
		else if (game?.IsUserAdded == true)
		{
			game.RemoveGameCommand.Execute(null);
		}
	}

	public string HideOrRemoveText => Game?.IsFound == true ? Text.HideGame() : Text.RemoveGame();
	public bool CanHideOrRemove => Game is { IsFound: true } or { IsUserAdded: true };

	private void ActivateIfRunning(string profileId)
	{
		if (Game != null && string.Equals(_runningGameKey(), Game.Key, StringComparison.OrdinalIgnoreCase) && _settings.Profiles.ActiveId != profileId)
		{
			_settings.ActivateProfile(profileId);
		}
	}

	private void RefreshProfile()
	{
		OnPropertyChanged(nameof(HasOwnProfile));
		OnPropertyChanged(nameof(UsesDefault));
		OnPropertyChanged(nameof(IsSupported));
		OnPropertyChanged(nameof(IsUnsupported));
		OnPropertyChanged(nameof(SourceText));
		OnPropertyChanged(nameof(ProfileLine));
		OnPropertyChanged(nameof(HideOrRemoveText));
		OnPropertyChanged(nameof(CanHideOrRemove));
		Game?.Profile?.Refresh();
		Feel.Refresh();
		RefreshStartFile();
		((ProfileAudioHapticsViewModel)AudioHaptics).Refresh();
	}

	protected override void OnLanguageChanged()
	{
		OnPropertyChanged(string.Empty);
		base.OnLanguageChanged();
	}
}
