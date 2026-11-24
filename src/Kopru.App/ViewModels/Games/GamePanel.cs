// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;

namespace Kopru.App.ViewModels;

// The panel of one game on the Games page (opened by clicking its row): its picture, name and where it comes from, Play,
// Hide or Remove, its controller type and profile, and its feel. "Settings For This Game" gives the game a profile of its own
// (named after it, made once) and shows that profile's settings here; "Back To Default" puts it back on the default profile.
// One instance per page: it shows the game in Game (null: closed). Its settings are in three segments (Game,
// Vibration, Triggers); the one shown is kept while the app runs (opening another game keeps it), not saved.
// Closing the running game, in two steps (GameModeController checks the key and that a close was asked before ending).
public interface IGameCloser
{
	bool RequestClose(string key);

	bool End(string key);
}

public enum CloseGameStep
{
	None,
	Waiting,
	Confirm
}

public sealed partial class GamePanelViewModel : ViewModelBase
{
	private readonly SettingsState _settings;
	private readonly Func<string?> _runningGameKey;
	private readonly Action<byte[], byte[]>? _testTriggers;
	private readonly Func<bool>? _isEdge;
	private readonly IGameCloser? _closer;
	private readonly Func<TimeSpan, Task> _delay;
	private int _closeRound;
	private GameItemViewModel? _watchedGame;
	private GamePadTypePicker? _watchedPadType;

	// testTriggers: plays an effect per trigger (right, left) for a moment (IControllerService.TestTriggers).
	// isEdge: whether the connected controller is a DualSense Edge (the controller node's picture and name).
	public GamePanelViewModel(SettingsState settings, Func<string?> runningGameKey, Action<byte[], byte[]>? testTriggers = null, Func<bool>? isEdge = null,
		IGameCloser? closer = null, Func<TimeSpan, Task>? delay = null)
	{
		_settings = settings;
		_runningGameKey = runningGameKey;
		_testTriggers = testTriggers;
		_isEdge = isEdge;
		_closer = closer;
		_delay = delay ?? Task.Delay;
		Feel = new ProfileFeelViewModel(settings, () => ProfileId);
		Feel.LeftTrigger.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanTryCustomTriggers));
		Feel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(ProfileFeelViewModel.ShowsTriggerRumble) or "" or null)
			{
				OnPropertyChanged(nameof(HasGameTriggerSettings));
			}
			if (e.PropertyName is nameof(ProfileFeelViewModel.LightFromGame) or "" or null)
			{
				OnPropertyChanged(nameof(LightChipText));
			}
		};
		Feel.RightTrigger.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanTryCustomTriggers));
		AudioHaptics = new ProfileAudioHapticsViewModel(settings, () => ProfileId);
		AudioHaptics.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(IAudioHapticsSettings.On) or nameof(IAudioHapticsSettings.Available) or "" or null)
			{
				OnPropertyChanged(nameof(AudioGateOn));
			}
		};
		settings.ProfilesChanged += () => RunOnUi(RefreshProfile);
	}

	// The game shown (null: the panel is closed).
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsOpen))]
	private GameItemViewModel? _game;

	partial void OnGameChanged(GameItemViewModel? value)
	{
		ResetClose();
		Naming = ProfileNaming.None;
		if (_watchedGame != null)
		{
			_watchedGame.PropertyChanged -= OnWatchedGameChanged;
		}
		if (_watchedPadType != null)
		{
			_watchedPadType.PropertyChanged -= OnWatchedPadTypeChanged;
		}
		_watchedGame = value;
		_watchedPadType = value?.PadType;
		if (_watchedGame != null)
		{
			_watchedGame.PropertyChanged += OnWatchedGameChanged;
		}
		if (_watchedPadType != null)
		{
			_watchedPadType.PropertyChanged += OnWatchedPadTypeChanged;
		}
		RefreshProfile();
	}

	private void OnWatchedGameChanged(object? sender, PropertyChangedEventArgs e)
	{
		// The game starting or stopping flips the state chip, the chain's apply state and the chain's line.
		if (e.PropertyName is nameof(GameItemViewModel.IsRunning) or "" or null)
		{
			if (!IsGameRunning)
			{
				ResetClose();
			}
			OnPropertyChanged(nameof(IsGameRunning));
			OnPropertyChanged(nameof(ShowsCloseGame));
			OnPropertyChanged(nameof(StateText));
			OnPropertyChanged(nameof(ApplyStateText));
		}
	}

	private void OnWatchedPadTypeChanged(object? sender, PropertyChangedEventArgs e)
	{
		// The game sees a different controller type the moment the picker changes.
		if (e.PropertyName is nameof(GamePadTypePicker.Selected) or nameof(GamePadTypePicker.AutomaticType) or "" or null)
		{
			OnPropertyChanged(nameof(SeenPadName));
		}
	}

	public bool IsOpen => Game != null;

	// The segments, by what they set: the game (start file, type, profile, the game's light), vibration (the game's haptics,
	// classic rumble, sound to haptics), triggers (the game's trigger strength, trigger rumble, Custom Trigger).
	public const int GameSegment = 0;
	public const int VibrationSegment = 1;
	public const int TriggersSegment = 2;

	private int _selectedSegment = GameSegment;

	// The list stays the same on a language switch (only the names change).
	public IReadOnlyList<TabOption> Tabs { get; } =
		[new(() => Text.GamePanelTabGame()), new(() => Text.GamePanelTabVibration()), new(() => Text.GamePanelTabTriggers())];

	// The shown segment; out of range values (-1: nothing selected) are ignored.
	public int SelectedSegment
	{
		get => _selectedSegment;
		set
		{
			if (value is < GameSegment or > TriggersSegment || value == _selectedSegment)
			{
				return;
			}
			_selectedSegment = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(IsGameSegment));
			OnPropertyChanged(nameof(IsVibrationSegment));
			OnPropertyChanged(nameof(IsTriggersSegment));
			OnPropertyChanged(nameof(ShowsScope));
		}
	}

	public bool IsGameSegment => SelectedSegment == GameSegment;
	public bool IsVibrationSegment => SelectedSegment == VibrationSegment;
	public bool IsTriggersSegment => SelectedSegment == TriggersSegment;

	// The next (step 1) or previous (step -1) segment, wrapping around (Ctrl+Tab, Ctrl+Shift+Tab).
	public void StepSegment(int step) => SelectedSegment = ((SelectedSegment + step) % Tabs.Count + Tabs.Count) % Tabs.Count;

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

	// The controller art on the Game tab's chain reads the controller through these (the panel's `Settings`).
	public SettingsState Settings => _settings;
	public bool IsEdge => _isEdge?.Invoke() == true;
	// The physical controller's name (product names stay the same in every language).
	public string ControllerName => IsEdge ? "DualSense Edge" : "DualSense";

	// The header's state chip and the chain's apply state switch between set (the game is not running) and applied now.
	public bool IsGameRunning => Game?.IsRunning == true;
	public string StateText => IsGameRunning ? Text.GameStateRunning() : Text.GameStateNotRunning();
	public string ApplyStateText => IsGameRunning ? Text.GameAppliedNow() : Text.GameAppliesOnStart();
	public string ChainTitle => Text.GamePanelChainTitle();
	public string NodeGameText => Text.RowGame();
	public string NodeVirtualText => Text.RowVirtualPad();
	public string NodeControllerText => Text.CardController();
	public string GameSeesText => Text.GameSeesLabel();

	// The chain's middle node: the controller type the game gets (follows the picker's selected or automatic type).
	public string SeenPadName => Game?.PadType is { } picker ? SettingsState.PadTypeName(picker.Selected?.Type ?? picker.AutomaticType) : string.Empty;
	// The controller node's profile chip: the edited profile's display name.
	public string ProfileDisplayName => Game == null ? string.Empty : SettingsState.DisplayName(_settings.Profiles.Find(ProfileId) ?? _settings.Profiles.Default);
	// The controller node's light chip: the game sets the light, or the player's own light stays.
	public string LightChipText => Feel.LightFromGame ? Text.GameLightChip() : Text.OwnLightChip();
	// Right of the tab switch, on the feel tabs with a per-game profile: the scope block.
	public bool ShowsScope => (IsVibrationSegment || IsTriggersSegment) && HasOwnProfile;
	public string ProfileSharedShortText => Text.ProfileSharedShort();
	// The panel's own close button (an icon): its tooltip and accessible name.
	public string ClosePanelTip => Text.GamePanelCloseTip();

	// The sound-to-haptics plot shows the gate only while the feature is on and available.
	public bool AudioGateOn => AudioHaptics.On && AudioHaptics.Available;
	public string GateSoundWord => Text.GatePlotSound();
	public string GateThresholdWord => Text.GatePlotThreshold();
	public string GateHapticsWord => Text.GatePlotHaptics();
	public string GatePlotCaptionText => Text.GatePlotCaption();
	// The Triggers tab's footer: what Trigger Strength covers and what the Try button does.
	public string GameEffectsShortText => Text.GameEffectsShort();
	public string TryShortText => Text.CustomTriggerTryShort();

	// Sound to haptics of the edited profile (live through SettingsState when it is the active one).
	public IAudioHapticsSettings AudioHaptics { get; }

	private string ProfileId => Game == null ? ProfileBook.DefaultId : _settings.Profiles.ResolveForGame(Game.Key);

	// The game has a profile other than the default one: its settings are shown and edited here.
	public bool HasOwnProfile => Game != null && _settings.Profiles.ProfileForGame(Game.Key) is { } id && id != ProfileBook.DefaultId;
	public bool UsesDefault => Game != null && !HasOwnProfile;

	// A game in the supported list: the game's own effects come first; for the others classic rumble and sound to haptics.
	public bool IsSupported => Game is { IsUnsupported: false, IsXboxTrigger: false };
	public bool IsUnsupported => Game is { IsUnsupported: true };

	// The Triggers tab's left column has something: the game's trigger strength (supported games) or the Xbox Series pad's
	// trigger rumble (while that setting is on). Without either, Custom Trigger takes the left column.
	public bool HasGameTriggerSettings => IsSupported || Feel.ShowsTriggerRumble;

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
	// Under the controller type picker on the Game tab.
	public string PadTypeHint => Text.PadTypeGameHint();
	// The Vibration tab's classic rumble card.
	public string ClassicRumbleTitle => Text.GamePanelClassicRumble();
	public string ProfileLabel => Text.ProfileLabel();
	public string AudioTitle => Text.AudioHapticsLabel();
	public string AudioSectionTitle => Text.AudioHapticsSection();
	public string AudioHint => Text.AudioHapticsHint();
	public string AudioRumbleNote => Text.AudioHapticsRumbleNote();
	public string AudioStrengthLabel => Text.AudioHapticsStrengthLabel();
	public string AudioSensitivityLabel => Text.AudioHapticsSensitivityLabel();
	public string AudioSensitivityHint => Text.AudioHapticsSensitivityHint();
	public string AudioBassText => Text.AudioHapticsBandBass();
	public string AudioFullText => Text.AudioHapticsBandFull();
	public string AudioBandLabel => Text.AudioHapticsBandLabel();
	public string LightGroupLabel => Text.GamePanelLightGroup();
	public string TabsLabel => Text.GamePanelTabsLabel();
	public string CustomTriggerTitle => Text.CustomTriggerSection();
	public string CustomTriggerHint => Text.CustomTriggerHint();
	public string TryText => Text.TryAction();
	public string TryHint => Text.CustomTriggerTryHint();

	// Try: both triggers' custom effects for a moment (a trigger on Game's Effect stays off meanwhile). Only while one is set.
	public bool CanTryCustomTriggers => Feel.LeftTrigger.IsSet || Feel.RightTrigger.IsSet;

	[RelayCommand]
	private void TryCustomTriggers()
	{
		if (CanTryCustomTriggers)
		{
			_testTriggers?.Invoke(Feel.RightTrigger.Bytes() ?? Kopru.Core.Protocol.TriggerEffects.Off(), Feel.LeftTrigger.Bytes() ?? Kopru.Core.Protocol.TriggerEffects.Off());
		}
	}
	public string ResetRowLabel => Text.GameResetRowLabel();
	public string SharedText => Text.ProfileSharedByGames();

	// Every setting of segments 2 and 3 is the profile's (ProfileValues: the game's effects, rumble, sound to haptics, trigger
	// rumble, Custom Trigger), so they are shared with every other game that uses the same profile: said when there is one.
	public bool IsProfileShared => HasOwnProfile && _settings.Profiles.GameProfiles.Values.Count(id => id == ProfileId) > 1;

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
		// The name field is on the Game segment ("Settings For This Game" is also offered on the other two).
		SelectedSegment = GameSegment;
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

	// "Oyunu Kapat": the running game's main window is asked to close; only when the game is still there after CloseWait the
	// header asks whether to end it (Force Close, the only destructive step). Only the detected active game is ever touched
	// (the closer checks the key). Never offered without a closer or while the game does not run.
	public static TimeSpan CloseWait { get; set; } = TimeSpan.FromSeconds(5);

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowsCloseGame), nameof(IsClosing), nameof(ConfirmsForceClose), nameof(ShowsHideOrRemove))]
	private CloseGameStep _closeStep;

	public bool ShowsCloseGame => IsGameRunning && _closer != null && CloseStep == CloseGameStep.None;
	public bool IsClosing => CloseStep == CloseGameStep.Waiting;
	public bool ConfirmsForceClose => CloseStep == CloseGameStep.Confirm;
	public string CloseGameText => Text.CloseGame();
	public string ClosingText => Text.ClosingGame();
	public string ForceCloseQuestion => Text.ForceCloseQuestion();
	public string ForceCloseText => Text.ForceClose();
	// The question in Close Game's place (the full sentence, ForceCloseQuestion, is its tooltip).
	public string ForceCloseAskText => Text.ForceCloseAsk();

	private void ResetClose()
	{
		_closeRound++;
		CloseStep = CloseGameStep.None;
	}

	[RelayCommand]
	private async Task CloseGame()
	{
		if (_closer == null || Game is not { IsRunning: true } game || CloseStep != CloseGameStep.None)
		{
			return;
		}
		int round = ++_closeRound;
		CloseStep = CloseGameStep.Waiting;
		if (!_closer.RequestClose(game.Key))
		{
			// Nothing to ask (no window): straight to the question.
			CloseStep = CloseGameStep.Confirm;
			return;
		}
		await _delay(CloseWait);
		if (round != _closeRound)
		{
			return;
		}
		CloseStep = game.IsRunning ? CloseGameStep.Confirm : CloseGameStep.None;
	}

	[RelayCommand]
	private void ForceClose()
	{
		if (_closer != null && Game is { IsRunning: true } game && CloseStep == CloseGameStep.Confirm)
		{
			_closer.End(game.Key);
		}
		ResetClose();
	}

	[RelayCommand]
	private void KeepGame() => ResetClose();

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
	// Hide/Remove steps aside while Close Game asks its question (the header keeps its width).
	public bool ShowsHideOrRemove => CanHideOrRemove && !ConfirmsForceClose;

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
		OnPropertyChanged(nameof(HasGameTriggerSettings));
		OnPropertyChanged(nameof(SourceText));
		OnPropertyChanged(nameof(ProfileLine));
		OnPropertyChanged(nameof(IsProfileShared));
		OnPropertyChanged(nameof(ShowsScope));
		OnPropertyChanged(nameof(ProfileDisplayName));
		OnPropertyChanged(nameof(IsEdge));
		OnPropertyChanged(nameof(ControllerName));
		OnPropertyChanged(nameof(SeenPadName));
		OnPropertyChanged(nameof(HideOrRemoveText));
		OnPropertyChanged(nameof(CanHideOrRemove));
		OnPropertyChanged(nameof(ShowsHideOrRemove));
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
