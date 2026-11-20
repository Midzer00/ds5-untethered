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

	private readonly Dictionary<string, string> texts = new(StringComparer.OrdinalIgnoreCase);

	// A small text file (a Steam or Epic manifest); its folder exists too.
	public FakeGameFolders File(string path, string text)
	{
		texts[path] = text;
		Add(Path.GetDirectoryName(path)!);
		return this;
	}

	public IEnumerable<string> FixedDrives() => [@"C:\"];

	public bool DirectoryExists(string path) => dirs.Contains(path.TrimEnd('\\'));

	public IEnumerable<string> Directories(string path) =>
		dirs.Where(d => string.Equals(Path.GetDirectoryName(d), path, StringComparison.OrdinalIgnoreCase)).ToList();

	public IEnumerable<string> Files(string path, string pattern)
	{
		string suffix = pattern.Contains('*') ? pattern[(pattern.LastIndexOf('*') + 1)..] : pattern;
		string prefix = pattern.Contains('*') ? pattern[..pattern.IndexOf('*')] : pattern;
		return texts.Keys.Where(f => string.Equals(Path.GetDirectoryName(f), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
			&& Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();
	}

	public string ReadAllText(string path) => texts.TryGetValue(path, out string? text) ? text : throw new FileNotFoundException(path);
}

// Steam's files read through the same made-up folders.
internal sealed class FakeGameSteam(FakeGameFolders folders, string steamPath) : Kopru.Core.Steam.ISteamEnvironment
{
	public string? SteamPath => steamPath;
	public bool IsSteamRunning => false;
	public bool FileExists(string path) => folders.Files(Path.GetDirectoryName(path)!, Path.GetFileName(path)).Any();
	public string ReadAllText(string path) => folders.ReadAllText(path);
	public IEnumerable<string> GetDirectories(string path) => folders.Directories(path);
	public DateTime GetLastWriteTimeUtc(string path) => DateTime.UnixEpoch;
	public IEnumerable<string> GetFiles(string path, string pattern) => folders.Files(path, pattern);
}

// Made-up games found by Steam and Epic (none of them in the sample catalog), plus Returnal in a game folder: the "Installed"
// view's found rows in tests and renders.
internal static class FakeFoundGames
{
	public const string Steam = @"C:\Steam";
	public const string Epic = @"C:\EpicData\Manifests";
	public const string RacerFolder = @"C:\Steam\steamapps\common\PixelRacer";

	public static FakeGameFolders Folders()
	{
		var folders = new FakeGameFolders(@"C:\Games\Returnal", RacerFolder, @"C:\Steam\steamapps\common\Moss Garden", @"C:\Steam\steamapps\common\Steamworks Shared",
			@"E:\Epic\Hollow Peak");
		folders.File(@"C:\Steam\steamapps\appmanifest_9009.acf", Manifest(9009, "Pixel Racer", "PixelRacer"));
		folders.File(@"C:\Steam\steamapps\appmanifest_9020.acf", Manifest(9020, "Moss Garden: A Very Long Name For A Quiet Little Gardening Game", "Moss Garden"));
		folders.File(@"C:\Steam\steamapps\appmanifest_228980.acf", Manifest(228980, "Steamworks Common Redistributables", "Steamworks Shared"));
		folders.File(Epic + @"\A.item", """{ "DisplayName": "Hollow Peak", "AppName": "HollowPeakApp", "AppCategories": ["public", "games"], "InstallLocation": "E:\\Epic\\Hollow Peak", "LaunchExecutable": "HollowPeak.exe" }""");
		return folders;
	}

	public static InstalledGames Installed(FakeGameFolders folders) =>
		new(new InstalledGameFinder(folders, null, new Kopru.Core.Steam.SteamLibrary(new FakeGameSteam(folders, Steam)), Epic, _ => { }));

	private static string Manifest(long id, string name, string dir) =>
		$"\"AppState\"\n{{\n\t\"appid\"\t\t\"{id}\"\n\t\"name\"\t\t\"{name}\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"{dir}\"\n}}\n";
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

	private static async Task<MainWindowViewModel> OpenFoundAsync(MemorySettingsStore? store = null, FakeControllerService? service = null)
	{
		var vm = new MainWindowViewModel(service ?? new FakeControllerService(animate: false),
			new AppEnvironment(store ?? new MemorySettingsStore(new AppSettings()), InstalledGames: FakeFoundGames.Installed(FakeFoundGames.Folders())));
		await vm.SupportedGames.ScanTask;
		return vm;
	}

	// "Supported": the catalog games, all or only those found ("Installed"). "Unsupported": the other games found (with their
	// launcher and both pickers) and the added games the list does not know.
	[AvaloniaFact]
	public async Task FoundGames_ShowInUnsupported_NotInSupported()
	{
		MainWindowViewModel vm = await OpenFoundAsync(new MemorySettingsStore(new AppSettings
		{
			UserGames = [new UserGame("Indie Game", @"C:\Games\Indie\Indie.exe")]
		}));
		SupportedGamesViewModel page = vm.SupportedGames;

		Assert.Equal(1, page.InstalledCount);
		Assert.Equal(Text.InstalledFilterCount(1), page.InstalledFilters[1].Name);
		Assert.Equal(4, page.Games.Count);
		Assert.DoesNotContain(page.Games, g => g.IsFound || g.IsUserAdded);
		Assert.False(page.ShowsAddHint);

		page.InstalledFilterIndex = 1;
		Assert.True(page.ShowsAddHint);
		Assert.Equal("Returnal", Assert.Single(page.Games).Name);

		page.ViewIndex = 2;
		Assert.True(page.ShowsAddHint);
		Assert.Equal(Text.UnsupportedViewCount(4), page.ViewFilters[2].Name);
		Assert.Equal(new[] { "Hollow Peak", "Indie Game", "Moss Garden: A Very Long Name For A Quiet Little Gardening Game", "Pixel Racer" },
			page.Games.Select(g => g.Name));
		GameItemViewModel racer = page.Games.Single(g => g.Name == "Pixel Racer");
		Assert.True(racer.IsFound);
		Assert.False(racer.IsInstalled);
		Assert.False(racer.HasHaptics);
		Assert.Equal("Steam", racer.FoundText);
		Assert.Contains(FakeFoundGames.RacerFolder, racer.FoundTip);
		Assert.Equal("steam:9009", racer.Key);
		Assert.NotNull(racer.PadType);
		Assert.NotNull(racer.Profile);
		Assert.Equal("Epic", page.Games.Single(g => g.Name == "Hollow Peak").FoundText);
		page.SearchText = "racer";
		Assert.Equal("Pixel Racer", Assert.Single(page.Games).Name);
	}

	// A found game is recognised running (its per-game controller type applies); Hide takes it off the list, saves it and
	// stops recognising it; Show brings it back.
	[AvaloniaFact]
	public async Task FoundGame_IsRecognised_AndHidingItIsSavedAndStopsThat()
	{
		var service = new FakeControllerService(animate: false);
		// No game runs (a game taking over from a running one would keep the controller type until it exits).
		service.Watcher.Exit("Cyberpunk2077.exe");
		service.Watcher.Exe = null;
		service.Games.Tick();
		var store = new MemorySettingsStore(new AppSettings { InstalledGamesOnly = true });
		MainWindowViewModel vm = await OpenFoundAsync(store, service);
		SupportedGamesViewModel page = vm.SupportedGames;
		page.ShowUnsupported = true;

		page.Games.Single(g => g.Name == "Pixel Racer").PadType!.Selected = page.Games.Single(g => g.Name == "Pixel Racer").PadType!.Options
			.Single(o => o.Type == Kopru.Core.Virtual.VirtualPadType.Xbox360);
		service.Watcher.Source.Show("Racer.exe", FakeFoundGames.RacerFolder + @"\bin\Racer.exe");
		service.Games.Tick();
		Assert.Equal("Pixel Racer", service.Games.State.Name);
		Assert.Equal("steam:9009", service.Games.ActiveGame!.Key);
		Assert.Equal(Kopru.Core.Virtual.VirtualPadType.Xbox360, service.Games.CurrentPadType);
		service.Watcher.Exit("Racer.exe");
		service.Games.Tick();

		page.Games.Single(g => g.Name == "Pixel Racer").HideGameCommand.Execute(null);
		Assert.DoesNotContain(page.Games, g => g.Name == "Pixel Racer");
		Assert.Equal(2, page.UnsupportedGames.Count);
		Assert.True(page.HasHiddenGames);
		Assert.Equal("Pixel Racer", Assert.Single(page.HiddenGames).Name);
		vm.Settings.SaveNow();
		Assert.Equal(new HiddenGame("steam:9009", "Pixel Racer"), Assert.Single(store.Saved!.HiddenGames!));

		service.Watcher.Source.Show("Racer.exe", FakeFoundGames.RacerFolder + @"\bin\Racer.exe");
		service.Games.Tick();
		Assert.Null(service.Games.ActiveGame);
		service.Watcher.Exit("Racer.exe");
		service.Games.Tick();

		// Hidden after a restart too, until shown again.
		MainWindowViewModel again = await OpenFoundAsync(store);
		again.SupportedGames.ShowUnsupported = true;
		Assert.DoesNotContain(again.SupportedGames.Games, g => g.Name == "Pixel Racer");
		again.SupportedGames.ShowHiddenCommand.Execute(again.SupportedGames.HiddenGames[0]);
		Assert.Contains(again.SupportedGames.Games, g => g.Name == "Pixel Racer");
		Assert.False(again.SupportedGames.HasHiddenGames);
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
						InstalledGamesOnly = render != "installed-all"
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
