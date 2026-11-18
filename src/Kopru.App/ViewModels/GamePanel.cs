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

	partial void OnGameChanged(GameItemViewModel? value)
	{
		Naming = ProfileNaming.None;
		RefreshProfile();
	}

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
	// The start file as a row: its label, the chosen file's name under it, Choose File / Change beside it.
	public string StartFileLabel => Text.StartFileLabel();
	public string StartFileName => Game != null && _settings.StartFileFor(Game.Key) is { } chosen ? Path.GetFileName(chosen) : string.Empty;
	public string StartFilePickText => Text.StartFilePick();
	public string StartFileChangeShortText => Text.StartFileChangeShort();

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
		OnPropertyChanged(nameof(StartFileName));
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
	public bool IsSupported => Game is { IsUnsupported: false, IsXboxTrigger: false };
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
	public string AudioBandLabel => Text.AudioHapticsBandLabel();
	public string LightGroupLabel => Text.GamePanelLightGroup();

	[RelayCommand]
	private void Close() => Game = null;

	// "Settings For This Game": a new profile for the game, from the default profile's values; its name is asked first
	// (ProfileName, the game's name by default). Saved, it is assigned to the game; if the game is running it becomes the
	// active profile so changes apply now.
	[RelayCommand]
	private void UseOwnSettings()
	{
		if (Game == null || HasOwnProfile)
		{
			return;
		}
		BeginNaming(ProfileNaming.FromDefault, NextGameProfileName());
	}

	// The profile row's actions for the game's profile: a new one (from the game's current profile), rename it (not the
	// default profile), duplicate it. Each asks for the name in the row under the picker first: Enter saves, Esc cancels.
	[RelayCommand]
	private void NewProfile()
	{
		if (Game != null)
		{
			BeginNaming(ProfileNaming.New, NextGameProfileName());
		}
	}

	[RelayCommand(CanExecute = nameof(CanRenameProfile))]
	private void RenameProfile()
	{
		if (Game != null && _settings.Profiles.Find(ProfileId) is { IsDefault: false } profile)
		{
			BeginNaming(ProfileNaming.Rename, profile.Name);
		}
	}

	public bool CanRenameProfile => HasOwnProfile;

	[RelayCommand]
	private void DuplicateProfile()
	{
		if (Game != null)
		{
			BeginNaming(ProfileNaming.Duplicate, NextGameProfileName());
		}
	}

	// What the name field is for (None: it is closed).
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsNaming))]
	[NotifyPropertyChangedFor(nameof(NamingLabel))]
	private ProfileNaming _naming;

	// The name being typed.
	[ObservableProperty]
	private string _profileName = string.Empty;

	public bool IsNaming => Naming != ProfileNaming.None;

	// The field's label: what saving it does.
	public string NamingLabel => Naming switch
	{
		ProfileNaming.Rename => Text.ProfileRename(),
		ProfileNaming.Duplicate => Text.ProfileDuplicate(),
		ProfileNaming.None => string.Empty,
		_ => Text.ProfileNew()
	};

	public string NewProfileText => Text.ProfileNew();
	public string RenameProfileText => Text.ProfileRename();
	public string DuplicateProfileText => Text.ProfileDuplicate();
	public string ProfileActionsText => Text.ProfileMore();
	public string SaveText => Text.ProfileSave();
	public string CancelText => Text.ProfileCancel();

	// The name field opened (the view focuses it with the name selected, so typing replaces it).
	public event Action? NamingStarted;

	private void BeginNaming(ProfileNaming naming, string name)
	{
		ProfileName = name;
		Naming = naming;
		NamingStarted?.Invoke();
	}

	// "<Game>", or "<Game> 2", "<Game> 3"... when a profile already has that name.
	public string NextGameProfileName()
	{
		if (Game == null)
		{
			return string.Empty;
		}
		string Clip(string s) => s.Length > ProfileBook.MaxNameLength ? s[..ProfileBook.MaxNameLength].TrimEnd() : s;
		bool Taken(string n) => _settings.Profiles.Profiles.Any(p => !p.IsDefault && string.Equals(p.Name, n, StringComparison.CurrentCultureIgnoreCase));
		string name = Clip(Game.Name.Trim());
		if (!Taken(name))
		{
			return name;
		}
		for (int i = 2; ; i++)
		{
			string suffix = " " + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
			string candidate = Clip(Game.Name.Trim()[..Math.Min(Game.Name.Trim().Length, ProfileBook.MaxNameLength - suffix.Length)].TrimEnd() + suffix);
			if (!Taken(candidate))
			{
				return candidate;
			}
		}
	}

	// Enter: a new or duplicated profile is made and assigned to the game (active at once while it runs); a rename renames
	// the game's profile. A blank name keeps the field open.
	[RelayCommand]
	private void SaveProfileName()
	{
		GameItemViewModel? game = Game;
		ProfileNaming naming = Naming;
		string name = (ProfileName ?? string.Empty).Trim();
		if (game == null || naming == ProfileNaming.None || name.Length == 0)
		{
			return;
		}
		Naming = ProfileNaming.None;
		if (naming == ProfileNaming.Rename)
		{
			_settings.Profiles.Rename(ProfileId, name);
			RefreshProfile();
			return;
		}
		ProfileValues values = naming == ProfileNaming.FromDefault ? _settings.Profiles.Default.Values
			: _settings.Profiles.Find(ProfileId)?.Values ?? _settings.Profiles.Default.Values;
		if (ProfileId == _settings.Profiles.ActiveId && naming != ProfileNaming.FromDefault)
		{
			values = _settings.CaptureValues();
		}
		if (_settings.Profiles.Create(name, values) is not { } created)
		{
			return;
		}
		_settings.Profiles.AssignGame(game.Key, created.Id);
		ActivateIfRunning(created.Id);
		RefreshProfile();
	}

	// Esc.
	[RelayCommand]
	private void CancelProfileName() => Naming = ProfileNaming.None;

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

	// "Oyuna Dön" in Play's place while this game runs (Game.IsRunning): the panel closes and the Overview's way back runs
	// (Mouse Mode off, the game to the front).
	public string BackToGameText => Text.BackToGame();

	[RelayCommand]
	private void BackToGame()
	{
		GameItemViewModel? game = Game;
		if (game is not { IsRunning: true })
		{
			return;
		}
		Game = null;
		game.BackToGameCommand.Execute(null);
	}

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
		OnPropertyChanged(nameof(CanRenameProfile));
		RenameProfileCommand.NotifyCanExecuteChanged();
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

// What the pop-up's profile name field is for: a new profile from the default one (Settings For This Game), a new one
// from the game's profile, a rename, a duplicate.
public enum ProfileNaming
{
	None,
	FromDefault,
	New,
	Rename,
	Duplicate
}
