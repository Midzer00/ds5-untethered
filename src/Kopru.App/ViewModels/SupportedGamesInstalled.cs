// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.Core.Games;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// One option of a segmented choice whose name can change while the language stays (e.g. a count in it).
public sealed class FilterOption(Func<string> name) : ViewModelBase
{
	public string Name => name();

	public void Refresh() => OnPropertyChanged(nameof(Name));
}

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

	public int InstalledCount => _all.Count(g => g.IsInstalled);

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ScanText))]
	[NotifyPropertyChangedFor(nameof(HasNoInstalled))]
	[NotifyPropertyChangedFor(nameof(IsSearchingInstalled))]
	[NotifyPropertyChangedFor(nameof(HasNoMatch))]
	private bool _isScanning;

	public string ScanText => IsScanning ? Text.ScanningGames() : Text.ScanGamesAgain();

	// The "Installed" view with nothing found: one sentence and "Add Folder". While the first scan runs: "Looking for games".
	public bool HasNoInstalled => InstalledOnly && InstalledCount == 0 && !IsScanning;
	public bool IsSearchingInstalled => InstalledOnly && InstalledCount == 0 && IsScanning;
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
		MarkInstalled();
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

	private void RefreshList()
	{
		InstalledFilters[1].Refresh();
		OnPropertyChanged(nameof(InstalledCount));
		OnPropertyChanged(nameof(InstalledFilterName));
		OnPropertyChanged(nameof(Games));
		OnPropertyChanged(nameof(CountText));
		OnPropertyChanged(nameof(HasNoMatch));
		OnPropertyChanged(nameof(HasNoInstalled));
		OnPropertyChanged(nameof(IsSearchingInstalled));
	}
}
