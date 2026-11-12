// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Games;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// Made-up folders for the installed games finder (listings only); nothing on this PC is read.
internal sealed class FakeGameFolders : IInstallFileSystem
{
	private readonly HashSet<string> dirs = new(StringComparer.OrdinalIgnoreCase);

	public FakeGameFolders(params string[] folders)
	{
		foreach (string folder in folders)
		{
			Add(folder);
		}
	}

	public void Add(string folder)
	{
		for (string? p = folder; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
		{
			dirs.Add(p);
		}
	}

	public void Remove(string folder) => dirs.RemoveWhere(d => d.StartsWith(folder, StringComparison.OrdinalIgnoreCase));

	public IEnumerable<string> FixedDrives() => [@"C:\"];

	public bool DirectoryExists(string path) => dirs.Contains(path);

	public IEnumerable<string> Directories(string path) =>
		dirs.Where(d => string.Equals(Path.GetDirectoryName(d), path, StringComparison.OrdinalIgnoreCase)).ToList();

	public IEnumerable<string> Files(string path, string pattern) => [];

	public string ReadAllText(string path) => throw new FileNotFoundException(path);
}

// The "All | Installed" choice on the supported games page: counts, the filter with the search, the remembered choice, the
// empty state, added folders and a game recognised running; and the new header row fitting at the smallest and a wide
// window in both languages. Renders docs/screens/supported-games-installed*-{tr,en}.png when KOPRU_RENDER_SCREENS=1.
public sealed class InstalledGamesAppTests
{
	private static InstalledGames Installed(FakeGameFolders folders) => new(new InstalledGameFinder(folders, log: _ => { }));

	private static async Task<MainWindowViewModel> OpenAsync(FakeGameFolders folders, MemorySettingsStore? store = null, FakeControllerService? service = null)
	{
		var vm = new MainWindowViewModel(service ?? new FakeControllerService(animate: false),
			new AppEnvironment(store ?? new MemorySettingsStore(new AppSettings()), InstalledGames: Installed(folders)));
		await vm.SupportedGames.ScanTask;
		return vm;
	}

	[AvaloniaFact]
	public async Task Counts_AndTheFilterWorksWithTheSearch()
	{
		MainWindowViewModel vm = await OpenAsync(new FakeGameFolders(@"C:\Games\Cyberpunk 2077", @"C:\Games\Returnal", @"C:\Games\Cyberpunk 2077 Mods"));
		SupportedGamesViewModel page = vm.SupportedGames;

		Assert.True(page.HasInstalledList);
		Assert.Equal(2, page.InstalledCount);
		Assert.Equal(Text.InstalledFilterCount(2), page.InstalledFilters[1].Name);
		Assert.Equal(0, page.InstalledFilterIndex);
		Assert.Equal(4, page.Games.Count);
		GameItemViewModel row = page.Games.Single(g => g.Name == "Returnal");
		Assert.True(row.IsInstalled);
		Assert.Contains(@"C:\Games\Returnal", row.InstalledTip);
		Assert.False(page.Games.Single(g => g.Name == "Marvel's Spider-Man Remastered").IsInstalled);

		page.InstalledFilterIndex = 1;
		Assert.True(vm.Settings.InstalledGamesOnly);
		Assert.Equal(new[] { "Cyberpunk 2077", "Returnal" }, page.Games.Select(g => g.Name));
		page.SearchText = "ret";
		Assert.Equal("Returnal", Assert.Single(page.Games).Name);
		page.SearchText = "spider";
		Assert.Empty(page.Games);
		Assert.True(page.HasNoMatch);
		Assert.False(page.HasNoInstalled);
		page.InstalledFilterIndex = 0;
		Assert.Single(page.Games);
		page.InstalledFilterIndex = -1;
		Assert.Equal(0, page.InstalledFilterIndex);
	}

	[AvaloniaFact]
	public async Task Choice_IsSaved_AndRemembered()
	{
		var store = new MemorySettingsStore(new AppSettings());
		MainWindowViewModel vm = await OpenAsync(new FakeGameFolders(@"C:\Games\Returnal"), store);
		vm.SupportedGames.InstalledFilterIndex = 1;
		vm.Settings.SaveNow();
		Assert.True(store.Saved!.InstalledGamesOnly);

		MainWindowViewModel again = await OpenAsync(new FakeGameFolders(@"C:\Games\Returnal"), store);
		Assert.Equal(1, again.SupportedGames.InstalledFilterIndex);
		Assert.Equal("Returnal", Assert.Single(again.SupportedGames.Games).Name);
	}

	[AvaloniaFact]
	public async Task NothingInstalled_ShowsTheEmptyState()
	{
		MainWindowViewModel vm = await OpenAsync(new FakeGameFolders(@"C:\Games\Something Else"),
			new MemorySettingsStore(new AppSettings { InstalledGamesOnly = true }));
		SupportedGamesViewModel page = vm.SupportedGames;

		Assert.Equal(0, page.InstalledCount);
		Assert.Equal(Text.InstalledFilterCount(0), page.InstalledFilters[1].Name);
		Assert.Empty(page.Games);
		Assert.True(page.HasNoInstalled);
		Assert.False(page.HasNoMatch);
		Assert.False(page.IsSearchingInstalled);
		page.InstalledFilterIndex = 0;
		Assert.False(page.HasNoInstalled);
		Assert.Equal(4, page.Games.Count);
	}

	[AvaloniaFact]
	public async Task AddedFolder_IsSearched_SavedAndRemovable()
	{
		var store = new MemorySettingsStore(new AppSettings());
		var folders = new FakeGameFolders(@"F:\Mine\Assetto Corsa Competizione");
		MainWindowViewModel vm = await OpenAsync(folders, store);
		SupportedGamesViewModel page = vm.SupportedGames;
		Assert.Equal(0, page.InstalledCount);
		Assert.True(page.HasNoFolders);

		page.PickFolder = () => Task.FromResult<string?>(null);
		await page.AddFolderCommand.ExecuteAsync(null);
		Assert.True(page.HasNoFolders);

		page.PickFolder = () => Task.FromResult<string?>(@"F:\Mine\");
		await page.AddFolderCommand.ExecuteAsync(null);
		Assert.Equal(@"F:\Mine", Assert.Single(page.Folders).Path);
		Assert.Equal(1, page.InstalledCount);
		Assert.True(page.Games.Single(g => g.Name == "Assetto Corsa Competizione").IsInstalled);
		// The same folder again is not added twice.
		await page.AddFolderCommand.ExecuteAsync(null);
		Assert.Single(page.Folders);
		vm.Settings.SaveNow();
		Assert.Equal(new[] { @"F:\Mine" }, store.Saved!.GameFolders!);

		await page.RemoveFolderCommand.ExecuteAsync(page.Folders[0]);
		Assert.True(page.HasNoFolders);
		Assert.Equal(0, page.InstalledCount);
	}

	[AvaloniaFact]
	public async Task GameRecognisedRunning_IsRemembered_WhileItsFolderExists()
	{
		var service = new FakeControllerService(animate: false);
		var folders = new FakeGameFolders(@"C:\Play\RT");
		var store = new MemorySettingsStore(new AppSettings());
		MainWindowViewModel vm = await OpenAsync(folders, store, service);
		Assert.Equal(0, vm.SupportedGames.InstalledCount);

		service.Watcher.Source.Show("Returnal-Win64-Shipping.exe", @"C:\Play\RT\Returnal\Binaries\Win64\Returnal-Win64-Shipping.exe");
		service.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		await vm.SupportedGames.ScanTask;

		Assert.Contains(new SeenGame("Returnal", @"C:\Play\RT"), vm.Settings.SeenGames);
		InstalledGame found = vm.SupportedGames.Games.Single(g => g.Name == "Returnal").Installed!;
		Assert.Equal(InstalledSource.Seen, found.Source);
		vm.Settings.SaveNow();
		Assert.Contains(new SeenGame("Returnal", @"C:\Play\RT"), store.Saved!.SeenGames!);

		// The folder is gone: the next scan drops it (the memory stays).
		folders.Remove(@"C:\Play\RT");
		await vm.SupportedGames.ScanAgainCommand.ExecuteAsync(null);
		Assert.Equal(0, vm.SupportedGames.InstalledCount);
	}

	[AvaloniaFact]
	public void WithoutAFinder_TheChoiceIsHidden_AndEveryGameShows()
	{
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false),
			new AppEnvironment(new MemorySettingsStore(new AppSettings { InstalledGamesOnly = true })));
		Assert.False(vm.SupportedGames.HasInstalledList);
		Assert.Equal(0, vm.SupportedGames.InstalledFilterIndex);
		Assert.Equal(4, vm.SupportedGames.Games.Count);
		Assert.False(vm.SupportedGames.HasNoInstalled);
	}

	// The header row (All | Installed, Scan Again, Folders) inside the list card, apart and uncut, at the smallest window and
	// a wide one, in both languages, with games found and with none.
	[AvaloniaFact]
	public async Task HeaderRow_Fits_AndRenders()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string outDir = Path.Combine(RepoRoot(), "docs", "screens");
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height, string? render) in new (double, double, string?)[]
				{
					(960, 640, "installed-min"), (2560, 1440, null), (1100, 700, "installed"), (1100, 700, "installed-all"), (1100, 700, "installed-empty")
				})
				{
					bool empty = render == "installed-empty";
					FakeGameFolders folders = empty
						? new FakeGameFolders(@"C:\Games\Something Else")
						: new FakeGameFolders(@"C:\Games\Cyberpunk 2077", @"C:\Games\Returnal", @"C:\Oyunlar\Assetto Corsa Competizione");
					MainWindowViewModel vm = await OpenAsync(folders, new MemorySettingsStore(new AppSettings
					{
						GameListCheckedAt = DateTimeOffset.Now,
						Language = code,
						InstalledGamesOnly = render != "installed-all",
						AlwaysGames = ["Returnal"]
					}));
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
					FitCheck.Settle(window);

					Grid row = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "InstalledRow");
					Assert.True(row.IsEffectivelyVisible);
					Control card = row.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("panel"));
					var parts = new[] { "InstalledChoice", "ScanAgainButton", "FoldersButton" }
						.Select(name => (name, FitCheck.Bounds(row.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name), window))).ToList();
					var problems = FitCheck.Apart(parts, FitCheck.Bounds(card, window), gap: 6).Concat(FitCheck.CutText(row)).Concat(FitCheck.CutText(card))
						.Select(p => $"{width}x{height} {code} {render}: {p}").ToList();
					Assert.Empty(problems);
					if (empty)
					{
						Assert.True(window.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "InstalledEmpty").IsEffectivelyVisible);
						Assert.True(window.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "EmptyAddFolderButton").IsEffectivelyVisible);
					}

					using var frame = window.CaptureRenderedFrame();
					Assert.NotNull(frame);
					if (writeFiles && render != null)
					{
						Directory.CreateDirectory(outDir);
						frame!.Save(Path.Combine(outDir, $"supported-games-{render}-{code}.png"));
					}
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static string RepoRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		return dir ?? throw new InvalidOperationException("Kopru.sln not found");
	}
}
