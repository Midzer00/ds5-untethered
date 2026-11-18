// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.Core.Games;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// One option of a segmented choice whose name can change while the language stays (e.g. a count in it).
public sealed class FilterOption(Func<string> name) : LabelledChoice(name);

// A folder the user added to look for games in (removable from the Folders menu).
public sealed class GameFolderItem(string path)
{
	public string Path { get; } = path;
}

// A row's "Installed" mark: where the game was found and its folder (the tooltip).
public sealed partial class GameItemViewModel
{
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsInstalled))]
	[NotifyPropertyChangedFor(nameof(InstalledTip))]
	[NotifyPropertyChangedFor(nameof(Launch))]
	[NotifyPropertyChangedFor(nameof(CanLaunch))]
	[NotifyPropertyChangedFor(nameof(ShowsPlay))]
	private InstalledGame? _installed;

	public bool IsInstalled => Installed != null;
	public string InstalledText => Text.InstalledMark();
	public string InstalledTip => Installed is not { } found
		? string.Empty
		: found.Folder is { Length: > 0 } folder ? GameModeText.Join(Text.InstalledFrom(found.Source), folder) : Text.InstalledFrom(found.Source);
}

// "All | Installed": the supported games installed on this PC (InstalledGames, scanned off the UI thread at start, on
// "Scan Again", when a folder is added or removed, when the list changes and when a supported game is recognised
// running), the remembered choice, the extra folders and the empty state. Without InstalledGames the choice is hidden
// and every game shows.
public sealed partial class SupportedGamesViewModel
{
	private readonly InstalledGames? _installedGames;
	private Dictionary<string, InstalledGame> _installedByName = new(StringComparer.OrdinalIgnoreCase);
	private GameCatalog? _scannedCatalog;
	private string? _lastSeenPath;
	private int _scansRunning;

	public bool HasInstalledList => _installedGames != null;

	// "All", "Installed (3)" ("Installed" until the first scan has finished).
	public IReadOnlyList<FilterOption> InstalledFilters { get; }
	public string InstalledFilterLabel => Text.InstalledFilterLabel();
	public string InstalledFilterName => _installedGames is { HasScanned: true } ? Text.InstalledFilterCount(InstalledCount) : Text.InstalledMark();

	public bool InstalledOnly => HasInstalledList && Settings.InstalledGamesOnly;

	// 0 = All, 1 = Installed (the segmented choice's order); -1 (selection cleared) leaves the choice alone.
	public int InstalledFilterIndex
	{
		get => InstalledOnly ? 1 : 0;
		set
		{
			if (value is 0 or 1 && HasInstalledList)
			{
				Settings.InstalledGamesOnly = value == 1;
			}
		}
	}

	// The games of the shown view on this PC ("Installed (n)"): the supported games installed here, or on "Xbox Trigger
	// Rumble" its games on this PC.
	public int InstalledCount => ShowXboxTriggers ? XboxTriggerGames.Count(g => g.IsOnThisPc) : InstalledGames.Count;

	// The line under the list on "Installed" and "Unsupported": a game not found is added with Add Game.
	public string InstalledAddHint => Text.InstalledAddHint();

	// The line under the rows: on "Unsupported" what these games get first ("these games use the Classic Rumble settings of
	// the profile you pick"), then the Add Game hint.
	public string AddHintLine => ShowUnsupported ? Text.UnsupportedGamesHint() + " " + Text.InstalledAddHint()
		: ShowXboxTriggers ? Text.XboxTriggerGamesHint()
		: Text.InstalledAddHint();
	public bool ShowsAddHint => (ShowUnsupported || ShowXboxTriggers || InstalledOnly) && !IsSearchingInstalled;

	// Found games the user hid (shown again from the Folders menu).
	public string HiddenGamesHeader => Text.HiddenGamesHeader();
	public string ShowGameText => Text.ShowGameAgain();
	public IReadOnlyList<HiddenGame> HiddenGames => Settings.HiddenGames;
	public bool HasHiddenGames => Settings.HiddenGames.Count > 0;

	// The found games of the last scan (hidden ones included).
	private IReadOnlyList<FoundGame> _found = [];

	// The found games shown and recognised: not hidden, and not a game the user added (its exe inside the folder).
	private IEnumerable<FoundGame> VisibleFound(IReadOnlyList<KnownGame> known)
	{
		List<string> added = known.Where(g => g.IsUserAdded && g.Path != null && Path.IsPathRooted(g.Path)).Select(g => g.Path!).ToList();
		return _found.Where(f => !Settings.IsGameHidden(f.Key)
			&& !added.Any(p => p.StartsWith(f.Folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)));
	}

	// Gives the watcher the found games it may recognise (after every catalog match).
	// Only when the list changed (a new list makes the watcher look at the game in front again).
	private void PushFound(IReadOnlyList<KnownGame> known)
	{
		List<FoundGame> visible = VisibleFound(known).ToList();
		if (_installedGames != null && !visible.SequenceEqual(Service.Games.FoundGames))
		{
			Service.Games.FoundGames = visible;
		}
	}

	private void HideFound(GameItemViewModel row)
	{
		if (row.Found is { } found && Settings.HideGame(found.Key, found.Name))
		{
			Rebuild();
		}
	}

	[RelayCommand]
	private void ShowHidden(HiddenGame? game)
	{
		if (game != null && Settings.ShowGame(game.Key))
		{
			Rebuild();
		}
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ScanText))]
	[NotifyPropertyChangedFor(nameof(HasNoInstalled))]
	[NotifyPropertyChangedFor(nameof(IsSearchingInstalled))]
	[NotifyPropertyChangedFor(nameof(HasNoMatch))]
	[NotifyPropertyChangedFor(nameof(ShowsAddHint))]
	private bool _isScanning;

	public string ScanText => IsScanning ? Text.ScanningGames() : Text.ScanGamesAgain();

	// "Installed" or "Unsupported" with nothing found: one sentence and "Add Folder". While the first scan runs: "Looking for
	// games".
	public bool HasNoInstalled => ShownCountOnThisPc == 0 && !IsScanning;
	public bool IsSearchingInstalled => ShownCountOnThisPc == 0 && IsScanning;

	// The games of the shown view that are on this PC (-1 on "All": it lists the whole catalog).
	private int ShownCountOnThisPc => ShowUnsupported ? UnsupportedGames.Count : InstalledOnly ? InstalledCount : -1;

	// The Xbox trigger rumble games ("Xbox Trigger Rumble"), taken once per rebuild.
	public IReadOnlyList<GameItemViewModel> XboxTriggerGames { get; private set; } = [];
	public string InstalledEmptyText => Text.InstalledEmpty();
	public string InstalledSearchingText => Text.InstalledSearching();

	public string FoldersText => Text.GameFolders();
	public string FoldersHint => Text.GameFoldersHint();
	public string AddFolderText => Text.AddGameFolder();
	public string RemoveFolderText => Text.RemoveGameFolder();
	public string NoFoldersText => Text.NoGameFolders();
	public IReadOnlyList<GameFolderItem> Folders => Settings.GameFolders.Select(f => new GameFolderItem(f)).ToList();
	public bool HasNoFolders => Settings.GameFolders.Count == 0;

	// Asks for a folder (local path out; null when cancelled). Set by the view.
	public Func<Task<string?>>? PickFolder { get; set; }

	// The scan started last (tests wait for it).
	public Task ScanTask { get; private set; } = Task.CompletedTask;

	private void InitInstalled()
	{
		if (_installedGames == null)
		{
			return;
		}
		Settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.InstalledGamesOnly))
			{
				OnPropertyChanged(nameof(InstalledOnly));
				OnPropertyChanged(nameof(InstalledFilterIndex));
				RefreshList();
			}
			else if (e.PropertyName == nameof(SettingsState.GameFolders))
			{
				OnPropertyChanged(nameof(Folders));
				OnPropertyChanged(nameof(HasNoFolders));
			}
			else if (e.PropertyName == nameof(SettingsState.HiddenGames))
			{
				OnPropertyChanged(nameof(HiddenGames));
				OnPropertyChanged(nameof(HasHiddenGames));
			}
		};
		// An updated list may hold games installed here: look again (not for an added game of the user's).
		Service.Games.CatalogChanged += () => RunOnUi(() =>
		{
			if (!ReferenceEquals(Service.Games.Catalog, _scannedCatalog))
			{
				_ = ScanInstalledAsync();
			}
		});
		Service.Games.StateChanged += _ => RunOnUi(OnGameRecognised);
		_ = ScanInstalledAsync();
	}

	// Looks for the installed games again off the UI thread (call on the UI thread); scans asked for meanwhile run once more.
	public Task ScanInstalledAsync()
	{
		if (_installedGames == null)
		{
			return Task.CompletedTask;
		}
		_scannedCatalog = Service.Games.Catalog;
		_scansRunning++;
		IsScanning = true;
		Task<InstalledScan> scan = _installedGames.ScanAsync(() => (Service.Games.Catalog, Settings.GameFolders, Settings.SeenGames));
		return ScanTask = FinishScanAsync(scan);
	}

	private async Task FinishScanAsync(Task<InstalledScan> scan)
	{
		try
		{
			await scan.ConfigureAwait(true);
		}
		finally
		{
			RunOnUi(() =>
			{
				_scansRunning--;
				ApplyInstalled(_installedGames!.Latest);
				IsScanning = _scansRunning > 0;
			});
		}
	}

	[RelayCommand]
	private Task ScanAgain() => ScanInstalledAsync();

	[RelayCommand]
	private async Task AddFolder()
	{
		if (PickFolder == null || await PickFolder() is not { Length: > 0 } folder)
		{
			return;
		}
		if (Settings.AddGameFolder(folder))
		{
			await ScanInstalledAsync();
		}
	}

	[RelayCommand]
	private Task RemoveFolder(GameFolderItem? folder) =>
		folder != null && Settings.RemoveGameFolder(folder.Path) ? ScanInstalledAsync() : Task.CompletedTask;

	// A supported game recognised running: its folder is remembered, and a game not yet counted as installed is looked for.
	private void OnGameRecognised()
	{
		if (Service.Games.ActiveGame is not { Match: { } match } game || string.Equals(game.Path, _lastSeenPath, StringComparison.OrdinalIgnoreCase)
			|| InstalledGameFinder.InstallFolderOf(game.Path) is not { } folder)
		{
			return;
		}
		_lastSeenPath = game.Path;
		if (Settings.RememberSeenGame(match.Name, folder) || !_installedByName.ContainsKey(match.Name))
		{
			_ = ScanInstalledAsync();
		}
	}

	private void ApplyInstalled(InstalledScan scan)
	{
		_installedByName = scan.Games.ToDictionary(g => g.Game.Name, StringComparer.OrdinalIgnoreCase);
		_found = scan.Found;
		Rebuild();
	}

	// The catalog rows found installed carry their mark (a game the user added keeps its own row as it is).
	private void MarkInstalled()
	{
		foreach (GameItemViewModel item in _all)
		{
			item.Installed = !item.IsUserAdded && item.Game.Catalog is { } game ? _installedByName.GetValueOrDefault(game.Name) : null;
		}
		RefreshList();
	}

	// The games found or added that are not in the supported list ("Unsupported (n)"), taken once per rebuild.
	public IReadOnlyList<GameItemViewModel> UnsupportedGames { get; private set; } = [];

	// Everything on this PC (counted for "Installed (n)"), taken once per rebuild.
	public IReadOnlyList<GameItemViewModel> InstalledGames { get; private set; } = [];

	private void RefreshList()
	{
		InstalledGames = _all.Where(g => !g.IsUnsupported && !g.IsXboxTrigger && g.IsInstalled).ToList();
		UnsupportedGames = _all.Where(g => g.IsUnsupported).ToList();
		XboxTriggerGames = _all.Where(g => g.IsXboxTrigger).ToList();
		ViewFilters[2].Refresh();
		OnPropertyChanged(nameof(UnsupportedGames));
		InstalledFilters[1].Refresh();
		OnPropertyChanged(nameof(ListLabel));
		OnPropertyChanged(nameof(InstalledCount));
		OnPropertyChanged(nameof(InstalledGames));
		OnPropertyChanged(nameof(InstalledFilterName));
		OnPropertyChanged(nameof(Games));
		OnPropertyChanged(nameof(CountText));
		OnPropertyChanged(nameof(HasNoMatch));
		OnPropertyChanged(nameof(HasNoInstalled));
		OnPropertyChanged(nameof(IsSearchingInstalled));
		OnPropertyChanged(nameof(ShowsAddHint));
		OnPropertyChanged(nameof(AddHintLine));
	}
}
