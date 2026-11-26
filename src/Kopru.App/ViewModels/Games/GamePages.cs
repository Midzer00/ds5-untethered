// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// Status line parts. Mode and Line still name the removed DualSense Mode; only the sidebar (Pages.cs) reads them.
public static class GameModeText
{
	// Parts of a status line, e.g. "Cyberpunk 2077 · DualSense Mode" (the same in every language).
	public static string Join(string first, string second) => first + " · " + second;

	public static string Mode(GameMode mode) => mode == GameMode.Supported ? Text.ModeSupported() : Text.ModeUnsupported();

	// "Cyberpunk 2077 · DualSense Mode", or the mode alone without a known game.
	public static string Line(GameModeState state) => state.Name == null
		? Mode(state.Mode)
		: GameModeText.Join(state.Name, Mode(state.Mode));
}

// One row of the supported games list: features and, for a game the user added, a remove button.
public sealed partial class GameItemViewModel : ObservableObject
{
	private readonly GameModeController _games;

	public GameItemViewModel(KnownGame game, GameModeController games)
	{
		_games = games;
		Game = game;
	}

	public KnownGame Game { get; }
	public string Name => Game.Name;
	public string Exe => Game.Exe;
	public string Key => Game.Key;
	public bool HasHaptics => Game.Haptics;
	public bool HasTriggers => Game.AdaptiveTriggers;
	public bool IsUserAdded => Game.IsUserAdded;
	public string HapticsText => Text.BadgeHaptics();
	public string TriggersText => Text.BadgeTriggers();
	public string UserAddedText => Text.MarkUserAdded();
	public string RemoveGameText => Text.RemoveGame();
	public string PathTip => Found?.Folder ?? Game.Path ?? Game.Exe;

	// A game found installed that the catalog does not list: where it came from (plain text) and Hide.
	public FoundGame? Found { get; init; }
	public bool IsFound => Found != null;
	// The launcher's name (a product name, the same in every language).
	public string FoundText => Found?.Source switch { InstalledSource.Epic => "Epic", InstalledSource.Steam => "Steam", _ => string.Empty };
	public string FoundTip => Found is { } found ? GameModeText.Join(Text.InstalledFrom(found.Source), found.Folder) : string.Empty;
	public string HideText => Text.HideGame();
	public string HideTip => Text.HideGameTip();

	// Hides a found game (set by the list).
	public Action<GameItemViewModel>? Hide { get; init; }

	[RelayCommand]
	private void HideGame() => Hide?.Invoke(this);

	// The profile used while this game is recognised.
	public GameProfilePicker? Profile { get; init; }

	// The controller type games see while this game is in front (DualSense unless chosen).
	public GamePadTypePicker? PadType { get; init; }

	// The picture at the start of the row (null without a picture service: the placeholder).
	public GameImageHandle? Image { get; init; }

	// How "Oyna" starts the game (null: we don't know how, no button). Set by the list; read again when the row's installed
	// mark changes.
	public Func<GameItemViewModel, GameLaunch?>? FindLaunch { get; init; }

	public void RefreshLaunch()
	{
		OnPropertyChanged(nameof(Launch));
		OnPropertyChanged(nameof(CanLaunch));
		OnPropertyChanged(nameof(ShowsPlay));
	}
	public GameLaunch? Launch => FindLaunch?.Invoke(this);
	public bool CanLaunch => Launch != null;
	// "Oyna" where we know how to start the game and it is not already running (then "Oyuna Dön" stands in its place).
	public bool ShowsPlay => CanLaunch && !IsRunning;
	public string PlayText => Text.PlayGame();
	public string PlayTip => Text.PlayGameTip();

	// Starts the game (set by the list).
	public Action<GameItemViewModel>? Play { get; init; }

	[RelayCommand]
	private void PlayGame() => Play?.Invoke(this);

	// The key of the game running now (null: none; set by the list): that game's row and panel show "Oyuna Dön".
	public Func<string?>? RunningKey { get; init; }
	public bool IsRunning => RunningKey?.Invoke() is { } key && string.Equals(key, Key, StringComparison.OrdinalIgnoreCase);
	public string BackToGameText => Text.BackToGame();
	public string BackToGameTip => Text.BackToGameTip(Name);

	// Back to the running game: the Overview's "Oyuna Dön" (GameReturn: Mouse Mode off, the game to the front; set by the list).
	public Action? GoBack { get; init; }

	[RelayCommand]
	private void BackToGame() => GoBack?.Invoke();

	public void RefreshRunning()
	{
		OnPropertyChanged(nameof(IsRunning));
		OnPropertyChanged(nameof(ShowsPlay));
	}

	// A game not in the supported list: found by its launcher, or added and unknown to the list (not an Xbox trigger rumble
	// game: those have their own view).
	public bool IsUnsupported => !IsXboxTrigger && (IsFound || (IsUserAdded && Game.Catalog == null));

	// A game that sends Xbox trigger rumble and does not support the DualSense (the Xbox Trigger Rumble view): from the
	// shipped list or learned. A listed DualSense game is never one (DualSense wins).
	public bool IsXboxTrigger { get; init; }

	// On this PC: found by its launcher, found installed, or added by hand.
	public bool IsOnThisPc => IsInstalled || IsFound || IsUserAdded;

	[RelayCommand]
	private void RemoveGame()
	{
		if (Game.Path != null)
		{
			_games.RemoveUserGame(Game.Path);
		}
	}

	public void Refresh() => PadType?.Refresh();

	public bool Matches(string query) => query.Length == 0
		|| CultureInfo.InvariantCulture.CompareInfo.IndexOf(Name, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0
		|| Exe.Contains(query, StringComparison.OrdinalIgnoreCase);
}

// A running app offered by "Add game".
public sealed class RunningAppItem(ProcessInfo process)
{
	public ProcessInfo Process { get; } = process;
	public string Name => ForegroundGame.DisplayName(Process.Exe);
	public string Tip => Process.Path ?? Process.Exe;
}

// Shared by both game pages: the running game and Steam Input.
public abstract class GameModePageViewModel : PageViewModel
{
	protected GameModePageViewModel(IControllerService service, SettingsState settings)
	{
		Service = service;
		Settings = settings;
		service.StateChanged += (_, _) => RunOnUi(() =>
		{
			OnPropertyChanged(nameof(CurrentGameText));
			OnPropertyChanged(nameof(IsActive));
			OnPropertyChanged(nameof(HasSteamWarning));
			OnPropertyChanged(nameof(HasSteamNote));
			OnPropertyChanged(nameof(SteamText));
		});
		settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.CurrentPadType))
			{
				OnPropertyChanged(nameof(CurrentGameText));
			}
		};
	}

	protected IControllerService Service { get; }
	public SettingsState Settings { get; }
	// "Cyberpunk 2077 · Xbox 360": the known game running and the controller it sees; without one, that none runs (also the
	// tooltip: the line can be cut).
	public string CurrentGameText => Service.Games.State.Name is { } name
		? GameModeText.Join(name, SettingsState.PadTypeName(Settings.CurrentPadType))
		: Text.NoGameYet();
	// A known game is running.
	public bool IsActive => Service.Games.State.Name != null;

	// Steam Input: a warning when it may take the controller, a muted note when it could not be read.
	public bool HasSteamWarning => SteamInputText.IsWarning(Service.SteamInput);
	public bool HasSteamNote => SteamInputText.IsUnknown(Service.SteamInput);
	public string SteamText => HasSteamWarning || HasSteamNote ? Service.SteamInput!.Advice : string.Empty;
	// Product name, the same in every language.
	public string SteamTitle => "Steam Input";
}

public sealed partial class SupportedGamesViewModel : GameModePageViewModel
{
	private IReadOnlyList<GameItemViewModel> _all = [];
	private int _knownCount = -1;

	private readonly GameCatalogUpdater? _updater;
	private readonly GameImageService? _images;
	private readonly Func<DateTimeOffset> _now;
	private CatalogUpdateResult? _lastResult;

	public SupportedGamesViewModel(IControllerService service, SettingsState settings, GameCatalogUpdater? updater = null,
		Func<DateTimeOffset>? now = null, GameImageService? images = null, InstalledGames? installed = null) : base(service, settings)
	{
		_updater = updater;
		_images = images;
		_installedGames = installed;
		_now = now ?? (() => DateTimeOffset.Now);
		Panel = new GamePanelViewModel(settings, () => service.Games.ActiveGame?.Key, service.TestTriggers,
			() => service.Kind == ControllerKind.DualSenseEdge, new GameCloser(service.Games));
		// A start file chosen or cleared: the rows read "Oyna" again.
		settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.StartFiles))
			{
				foreach (GameItemViewModel item in _all)
				{
					item.RefreshLaunch();
				}
			}
		};
		InstalledFilters = [new FilterOption(() => Text.InstalledFilterAll()), new FilterOption(() => InstalledFilterName)];
		ViewFilters = [new FilterOption(() => Text.SupportedView()), new FilterOption(() => Text.XboxTriggerView()),
			new FilterOption(() => Text.UnsupportedViewCount(UnsupportedGames.Count))];
		Rebuild();
		InitInstalled();
		// The pictures follow the download setting: the rows load again (downloaded pictures only while it is on).
		settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.DownloadGameImages) && _images != null)
			{
				_images.Forget();
				Rebuild();
			}
			else if (e.PropertyName is nameof(SettingsState.HasPendingPadType) or nameof(SettingsState.PadStatus))
			{
				OnPropertyChanged(nameof(PendingGameLine));
			}
		};
		service.Games.MemoryChanged += () => RunOnUi(() =>
		{
			if (Service.Games.KnownGames().Count != _knownCount || Service.Games.LearnedTriggerGames.Count != _learnedTriggerCount)
			{
				Rebuild();
			}
			else
			{
				foreach (GameItemViewModel item in _all)
				{
					item.Refresh();
				}
			}
		});
		settings.ProfilesChanged += () =>
		{
			foreach (GameItemViewModel item in _all)
			{
				item.Profile?.Refresh();
			}
		};
		service.Games.CatalogChanged += () => RunOnUi(() =>
		{
			Rebuild();
			OnPropertyChanged(nameof(ListStatusText));
		});
		// A game started or ended: its row and panel swap "Oyna" and "Oyuna Dön".
		service.Games.StateChanged += _ => RunOnUi(RefreshRunning);
	}

	// The way back to the running game (the Overview's "Oyuna Dön"; set by the main window). Without it no row offers it.
	public GameReturn? BackToGame
	{
		get => _backToGame;
		set
		{
			_backToGame = value;
			RefreshRunning();
		}
	}
	private GameReturn? _backToGame;

	private string? RunningKey() => _backToGame != null ? Service.Games.ActiveGame?.Key : null;

	private void GoBackToGame() => _backToGame?.BackCommand.Execute(null);

	private void RefreshRunning()
	{
		foreach (GameItemViewModel item in _all)
		{
			item.RefreshRunning();
		}
	}

	// The automatic check found new games (the count); raised on the UI thread.
	public event Action<int>? ListUpdatedAutomatically;

	public bool CanUpdateList => _updater != null;
	public string UpdateListText => IsUpdatingList ? Text.UpdatingGameList() : Text.UpdateGameList();
	public string AutoUpdateLabel => Text.AutoUpdateGameList();
	public bool HasListError => _lastResult?.Status == CatalogUpdateStatus.Failed;

	// "72 games · updated 9 Nov 2026", then the outcome of the last check.
	public string ListStatusText
	{
		get
		{
			GameCatalog catalog = Service.Games.Catalog;
			string info = Text.GameListInfo(catalog.Games.Count, catalog.Updated ?? string.Empty);
			return _lastResult switch
			{
				{ Status: CatalogUpdateStatus.UpToDate } => GameModeText.Join(info, Text.GameListUpToDate()),
				{ Status: CatalogUpdateStatus.Updated, Added.Count: > 0 } r => GameModeText.Join(info, Text.GameListAdded(r.Added.Count)),
				{ Status: CatalogUpdateStatus.Failed } r => Text.GameListFailed(r.Reason),
				_ => info
			};
		}
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(UpdateListText))]
	[NotifyCanExecuteChangedFor(nameof(UpdateListCommand))]
	private bool _isUpdatingList;

	private bool CanRunUpdate() => _updater != null && !IsUpdatingList;

	[RelayCommand(CanExecute = nameof(CanRunUpdate))]
	private Task UpdateList() => CheckListAsync(automatic: false);

	// The weekly automatic check (when on); silent unless it added games.
	public Task AutoUpdateAsync() =>
		GameCatalogUpdater.ShouldAutoCheck(_updater != null && Settings.AutoUpdateGameList, Settings.GameListCheckedAt, _now())
			? CheckListAsync(automatic: true)
			: Task.CompletedTask;

	private async Task CheckListAsync(bool automatic)
	{
		if (_updater == null || IsUpdatingList)
		{
			return;
		}
		IsUpdatingList = true;
		CatalogUpdateResult result;
		try
		{
			result = await _updater.CheckAsync(Service.Games.Catalog);
		}
		finally
		{
			IsUpdatingList = false;
		}
		if (result.Status != CatalogUpdateStatus.Failed)
		{
			Settings.GameListCheckedAt = _now();
		}
		if (result.Status == CatalogUpdateStatus.Updated)
		{
			Service.Games.SetCatalog(result.Catalog);
		}
		if (!automatic)
		{
			_lastResult = result;
		}
		RunOnUi(() =>
		{
			Rebuild();
			OnPropertyChanged(nameof(ListStatusText));
			OnPropertyChanged(nameof(HasListError));
			if (automatic && result.Added.Count > 0)
			{
				ListUpdatedAutomatically?.Invoke(result.Added.Count);
			}
		});
	}

	public override string Title => Text.PageGames();
	public string Intro => Text.GameListHint();

	// The running game and the controller type waiting for it ("Cyberpunk 2077 · Xbox 360"); the note and Apply Now are
	// SettingsState's.
	public string PendingGameLine => Settings.PadStatus is { Pending: { } pending } status
		? status.GameName is { } game ? GameModeText.Join(game, SettingsState.PadTypeName(pending)) : SettingsState.PadTypeName(pending)
		: string.Empty;
	// The view's name (the main switch's accessible name).
	public string ListLabel => ShowUnsupported ? Text.UnsupportedGamesTitle() : ShowXboxTriggers ? Text.XboxTriggerView() : Text.SupportedGameList();
	public string SearchWatermark => Text.SearchGames();
	public string DownloadImagesLabel => Text.DownloadGameImages();
	public string DownloadImagesHint => Text.DownloadGameImagesHint();
	public string AddGameText => Text.AddGame();
	public string AddFromFileText => Text.AddGameFromFile();
	public string RunningHeader => Text.AddGameRunningHeader();
	public string NoRunningText => Text.AddGameNoRunning();

	// Running apps for "Add game" (filled when its menu opens).
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasNoRunningApps))]
	private IReadOnlyList<RunningAppItem> _runningApps = [];

	public bool HasNoRunningApps => RunningApps.Count == 0;

	public void RefreshRunningApps() => RunningApps = Service.Games.RunningApps().Select(p => new RunningAppItem(p)).ToList();

	// Asks for an exe file (local path out; null when cancelled). Set by the view.
	public Func<Task<string?>>? PickExe { get; set; }

	[RelayCommand]
	private void AddRunning(RunningAppItem? app)
	{
		if (app != null)
		{
			Service.Games.AddUserGame(new UserGame(app.Name, app.Process.Path ?? app.Process.Exe));
		}
	}

	[RelayCommand]
	private async Task AddFromFile()
	{
		if (PickExe == null || await PickExe() is not { Length: > 0 } path)
		{
			return;
		}
		Service.Games.AddUserGame(new UserGame(Path.GetFileNameWithoutExtension(path), path));
	}
	public string CountText => Text.GameCount(Games.Count);
	public string NoMatchText => Text.NoMatchingGames();
	// "No matching games" for a search; the "Installed" view says instead that nothing was found (or that it is looking).
	public bool HasNoMatch => Games.Count == 0 && !HasNoInstalled && !IsSearchingInstalled;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Games))]
	[NotifyPropertyChangedFor(nameof(CountText))]
	[NotifyPropertyChangedFor(nameof(HasNoMatch))]
	private string _searchText = string.Empty;

	// "Desteklenen": the catalog games (and added ones the list knows), all or only those installed here. "Desteklenmeyen": the
	// games found here or added that the list does not know (all on this PC, so "Installed" does not apply to them).
	// "Xbox Tetik Titreşimi": the games that send Xbox trigger rumble (the shipped list and learned ones), those on this PC
	// first.
	public IReadOnlyList<GameItemViewModel> Games
	{
		get
		{
			string query = (SearchText ?? string.Empty).Trim();
			IEnumerable<GameItemViewModel> shown = _all.Where(g => InShownView(g) && g.Matches(query));
			return (ShowXboxTriggers ? shown.OrderByDescending(g => g.IsOnThisPc) : shown).ToList();
		}
	}

	private bool InShownView(GameItemViewModel game) => ShowUnsupported ? game.IsUnsupported
		: ShowXboxTriggers ? game.IsXboxTrigger && (!InstalledOnly || game.IsOnThisPc)
		: !game.IsUnsupported && !game.IsXboxTrigger && (!InstalledOnly || game.IsInstalled);

	// The main switch: Supported | Xbox Trigger Rumble | Unsupported (n). Not saved: the page opens on Supported.
	public IReadOnlyList<FilterOption> ViewFilters { get; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ViewIndex))]
	[NotifyPropertyChangedFor(nameof(ShowsInstalledChoice))]
	private bool _showUnsupported;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ViewIndex))]
	[NotifyPropertyChangedFor(nameof(ShowsInstalledChoice))]
	private bool _showXboxTriggers;

	// The Xbox view's switch row: the experimental Xbox Trigger Rumble setting (the same one as in Settings, so both stay in step).
	public string XboxTriggerRumbleTitle => Text.XboxTriggerRumbleTitle();
	public string ExperimentalTag => Text.ExperimentalTag();
	public string XboxTriggerRumbleShort => Text.XboxTriggerRumbleSettingShort();
	public string XboxTriggerRumbleLabel => Text.XboxTriggerRumbleSetting();
	public string XboxTriggerRumbleHint => Text.XboxTriggerRumbleSettingHint();

	partial void OnShowUnsupportedChanged(bool value)
	{
		if (value)
		{
			ShowXboxTriggers = false;
		}
		RefreshList();
	}

	partial void OnShowXboxTriggersChanged(bool value)
	{
		if (value)
		{
			ShowUnsupported = false;
		}
		RefreshList();
	}

	// 0 = Supported, 1 = Xbox Trigger Rumble, 2 = Unsupported; -1 (selection cleared) leaves the choice alone.
	public int ViewIndex
	{
		get => ShowXboxTriggers ? 1 : ShowUnsupported ? 2 : 0;
		set
		{
			if (value is 0 or 1 or 2)
			{
				ShowXboxTriggers = value == 1;
				ShowUnsupported = value == 2;
			}
		}
	}

	// "All | Installed" filters the supported view only.
	public bool ShowsInstalledChoice => HasInstalledList && !ShowUnsupported;

	// The panel of the game whose row was clicked (closed: Panel.Game null).
	public GamePanelViewModel Panel { get; }

	[RelayCommand]
	private void OpenGame(GameItemViewModel? row) => Panel.Game = row;

	// Starts a row's game (tests replace it: nothing is launched there); false when it could not be started.
	public Func<GameLaunch, string, bool> StartGame { get; set; } = GameLauncher.Start;

	// A game could not be started (its name); the main window shows a toast.
	public event Action<string>? LaunchFailed;

	private void PlayRow(GameItemViewModel row)
	{
		if (row.Launch is { } launch && !StartGame(launch, row.Name))
		{
			LaunchFailed?.Invoke(row.Name);
		}
	}

	// The games whose missing "Oyna" was logged (once per game).
	private readonly HashSet<string> _noLaunchLogged = new(StringComparer.OrdinalIgnoreCase);

	private GameLaunch? LaunchFor(GameItemViewModel row)
	{
		(GameLaunch? launch, string? reason) = GameLauncher.Explain(row.Game, row.Found, row.Installed, File.Exists, Settings.StartFileFor(row.Key));
		if (launch == null && reason != null && _noLaunchLogged.Add(row.Key))
		{
			Kopru.Core.Diagnostics.AppLog.Write($"games: no launch for {row.Name} ({reason})");
		}
		return launch;
	}

	protected override void OnLanguageChanged()
	{
		// Item templates do not observe the items: build them again with the new names.
		Rebuild();
		base.OnLanguageChanged();
	}

	private int _learnedTriggerCount = -1;

	// A found or added game that sends Xbox trigger rumble and does not support the DualSense.
	private bool XboxTriggerOnly(string key, FoundGame? found = null) =>
		Service.Games.UsesXboxTriggers(key, found) && !Service.Games.SupportsDualSense(key, found);

	private void Rebuild()
	{
		IReadOnlyList<KnownGame> known = Service.Games.KnownGames();
		_knownCount = known.Count;
		_learnedTriggerCount = Service.Games.LearnedTriggerGames.Count;
		IEnumerable<GameItemViewModel> rows = known.Select(g => new GameItemViewModel(g, Service.Games)
		{
			IsXboxTrigger = g.Catalog == null && XboxTriggerOnly(g.Key),
			FindLaunch = LaunchFor,
			Play = PlayRow,
			RunningKey = RunningKey,
			GoBack = GoBackToGame,
			Profile = new GameProfilePicker(Settings, g.Key),
			PadType = new GamePadTypePicker(Service.Games, g.Key),
			Image = _images == null ? null : new GameImageHandle(_images, new GameImageRequest(g.Key, g.Catalog?.SteamAppId, g.IsUserAdded ? g.Path ?? g.Exe : null))
		});
		List<GameItemViewModel> listed = rows.Concat(VisibleFound(known).Select(FoundRow)).ToList();
		_all = listed.Concat(TriggerListRows(listed)).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
		PushFound(known);
		MarkInstalled();
		// The open panel follows its game into the new rows (closed when the game is gone).
		if (Panel.Game is { } open)
		{
			Panel.Game = _all.FirstOrDefault(g => g.Key == open.Key);
		}
	}

	private GameItemViewModel FoundRow(FoundGame found)
	{
		var game = new KnownGame(found.Name, Path.GetFileName(found.LaunchExe) ?? string.Empty, false, false, false, null, found.Key, found.LaunchExe);
		return new GameItemViewModel(game, Service.Games)
		{
			Found = found,
			IsXboxTrigger = XboxTriggerOnly(found.Key, found),
			Hide = HideFound,
			FindLaunch = LaunchFor,
			Play = PlayRow,
			RunningKey = RunningKey,
			GoBack = GoBackToGame,
			Profile = new GameProfilePicker(Settings, found.Key),
			PadType = new GamePadTypePicker(Service.Games, found.Key),
			Image = _images == null ? null : new GameImageHandle(_images, new GameImageRequest(found.Key, found.SteamAppId, found.LaunchExe))
		};
	}
}

// The Xbox trigger rumble games that no row stands for yet (not installed, found or added here): one row each, keyed like
// the running game would be (XboxTriggerGame.Key). A game the supported list knows, or one Steam lists with DualSense
// support, is left out (DualSense wins).
public sealed partial class SupportedGamesViewModel
{
	private IEnumerable<GameItemViewModel> TriggerListRows(IReadOnlyList<GameItemViewModel> rows)
	{
		HashSet<string> keys = new(rows.Select(r => r.Key), StringComparer.OrdinalIgnoreCase);
		HashSet<string> names = new(rows.Select(r => GameCatalog.NormalizeName(r.Name)), StringComparer.Ordinal);
		GameCatalog catalog = Service.Games.Catalog;
		foreach (XboxTriggerGame entry in Service.Games.TriggerGames.Games)
		{
			if (keys.Contains(entry.Key) || names.Contains(GameCatalog.NormalizeName(entry.Name)) || catalog.FindBySteamAppId(entry.SteamAppId) != null
				|| catalog.FindByName(entry.Name) != null || Service.Games.SupportsDualSense(entry.Key))
			{
				continue;
			}
			var game = new KnownGame(entry.Name, entry.Exe.FirstOrDefault() ?? string.Empty, false, false, false, null, entry.Key);
			yield return new GameItemViewModel(game, Service.Games)
			{
				IsXboxTrigger = true,
				FindLaunch = LaunchFor,
				Play = PlayRow,
				RunningKey = RunningKey,
				GoBack = GoBackToGame,
				Profile = new GameProfilePicker(Settings, entry.Key),
				PadType = new GamePadTypePicker(Service.Games, entry.Key),
				Image = _images == null ? null : new GameImageHandle(_images, new GameImageRequest(entry.Key, entry.SteamAppId, null))
			};
		}
	}
}

// "Titreşim ve Tetikler": the active profile's game effects (the game's haptics and trigger strength) and classic rumble
// (used where the game sends nothing: rumble level and feel, the fallback trigger), with their tests. The games themselves are
// on the Games page; "Desteklenmeyen Oyunlar" opens it on that view.
public sealed partial class OtherGamesViewModel : GameModePageViewModel
{
	public OtherGamesViewModel(IControllerService service, SettingsState settings) : base(service, settings)
	{
		Feel = new ProfileFeelViewModel(settings);
		settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.ActiveProfileName))
			{
				OnPropertyChanged(nameof(ProfileText));
			}
		};
	}

	// Two tabs, one card each, so the page fits the window without scrolling: the game's own effects (the default) and Xbox
	// And DS4 Rumble. The tab names are the card titles. The selected tab is kept while the app runs (not saved), as on Settings.
	public const int GameEffectsTab = 0;
	public const int RumbleTab = 1;

	private int _selectedTab = GameEffectsTab;

	public IReadOnlyList<TabOption> Tabs { get; } = [new(() => Text.GameEffectsCard()), new(() => Text.ClassicRumbleCard())];

	// Out of range values (-1: nothing selected) are ignored.
	public int SelectedTab
	{
		get => _selectedTab;
		set
		{
			if (value is < GameEffectsTab or > RumbleTab || value == _selectedTab)
			{
				return;
			}
			_selectedTab = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(IsGameEffectsTab));
			OnPropertyChanged(nameof(IsRumbleTab));
		}
	}

	public bool IsGameEffectsTab => SelectedTab == GameEffectsTab;
	public bool IsRumbleTab => SelectedTab == RumbleTab;

	// Opens the Games page on "Desteklenmeyen" (set by the main window).
	public Action? OpenUnsupportedGames { get; set; }
	public string UnsupportedLinkText => Text.UnsupportedGamesTitle();
	public string UnsupportedLinkHint => Text.UnsupportedGamesHint();

	[RelayCommand]
	private void ShowUnsupportedGames() => OpenUnsupportedGames?.Invoke();

	public override string Title => Text.PageHapticsTriggers();
	public string Intro => Text.PageHapticsTriggersIntro();
	// "Profil: Varsayılan": the profile these settings are saved to (the active one).
	public string ProfileText => Text.ControllerProfileLine(Settings.ActiveProfileName);
	public string ProfileTip => Text.EditingProfileTip();
	// The active profile's feel settings (the same view model as a game's panel on the Games page).
	public ProfileFeelViewModel Feel { get; }
	// The try row at the end of the Xbox And DS4 Rumble card: "Dene" with the rumble, one side and the fallback effect.
	public string TryLabel => Text.TryRowLabel();
	public string TryHint => Text.TryRowHint();
	public string RumbleTestText => Text.RumbleLevel();
	public string TriggerTestText => Text.TryTriggerEffect();
	public string LeftText => Text.TestHapticsLeft();
	public string RightText => Text.TestHapticsRight();
	public string RumbleTryHint => Text.OtherGamesRumbleTryHint();
	public string TriggerTryHint => Text.OtherGamesTriggerTryHint();
	public string PauseHapticsLabel => Text.PauseHapticsInSilence();
	public string PauseHapticsShort => Text.PauseHapticsInSilenceShort();
	public string PauseHapticsHint => Text.PauseHapticsInSilenceHint();
	public string ExperimentalTag => Text.ExperimentalTag();
	public string XboxTriggerRumbleTitle => Text.XboxTriggerRumbleTitle();
	public string XboxTriggerRumbleLabel => Text.XboxTriggerRumbleSetting();
	public string XboxTriggerRumbleShort => Text.XboxTriggerRumbleSettingShort();
	public string XboxTriggerRumbleHint => Text.XboxTriggerRumbleSettingHint();
	[RelayCommand]
	private void TestRumble() => Service.TestRumble();

	[RelayCommand]
	private void TestTrigger() => Service.TestFallbackTrigger();

	[RelayCommand]
	private void TestHaptics(HapticSide side) => Service.TestHaptics(side);
}

// The panel's Close Game, over the game mode controller (which only touches the detected active game).
internal sealed class GameCloser(Kopru.Core.Games.GameModeController games) : IGameCloser
{
	public bool RequestClose(string key) => games.RequestCloseActiveGame(key);

	public bool End(string key) => games.EndActiveGame(key);
}
