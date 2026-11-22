// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;
using Kopru.Core.Steam;

namespace Kopru.Core.Tests;

// The other games found installed (Steam app manifests, Epic manifests), with made-up files only: Steam's non-game entries
// and Epic add-ons left out, a catalog game shown once, and recognising them running by their install folder after every
// catalog match.
public class FoundGamesTests
{
	private const string EpicDir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
	private const string Common = @"D:\SteamLibrary\steamapps\common";

	private readonly FakeInstallFiles files = new();
	private readonly List<string> logged = [];

	private InstalledGameFinder Finder() =>
		new(files, null, new SteamLibrary(new FakeInstallSteam(files, @"C:\Steam")), EpicDir, logged.Add);

	private static string Manifest(long id, string name, string dir, int flags = 4) =>
		$"\"AppState\"\n{{\n\t\"appid\"\t\t\"{id}\"\n\t\"name\"\t\t\"{name}\"\n\t\"StateFlags\"\t\t\"{flags}\"\n\t\"installdir\"\t\t\"{dir}\"\n}}\n";

	// One Steam app in the D: library, with its folder.
	private void Steam(long id, string name, string dir, int flags = 4, bool folder = true)
	{
		files.File($@"D:\SteamLibrary\steamapps\appmanifest_{id}.acf", Manifest(id, name, dir, flags));
		if (folder)
		{
			files.Dir($@"{Common}\{dir}");
		}
	}

	public FoundGamesTests()
	{
		files.Drives.Add(@"D:\");
		files.File(@"C:\Steam\steamapps\libraryfolders.vdf", "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}\n");
	}

	[Theory]
	[InlineData(228980, "Steamworks Common Redistributables", true)]
	[InlineData(250820, "SteamVR", true)]
	[InlineData(1628350, "Steam Linux Runtime 3.0 (sniper)", true)]
	[InlineData(9001, "Steam Linux Runtime - Soldier", true)]
	[InlineData(9002, "Proton 9.0", true)]
	[InlineData(9003, "Proton Experimental", true)]
	[InlineData(9004, "Proton EasyAntiCheat Runtime", true)]
	[InlineData(9005, "Some Shooter Dedicated Server", true)]
	[InlineData(9006, "Source SDK Base 2013 Multiplayer", true)]
	[InlineData(9007, "Pixel Racer Soundtrack", true)]
	[InlineData(9008, "Pixel Racer OST", true)]
	[InlineData(9009, "Pixel Racer", false)]
	[InlineData(9010, "Ghost Harbor", false)]
	[InlineData(9011, "Protons and Neutrons", false)]
	[InlineData(9012, "Steamroller Tycoon", false)]
	public void SteamNonGames_ExplicitIdsAndNames(long id, string name, bool notGame) => Assert.Equal(notGame, SteamNonGames.IsNonGame(id, name));

	[Fact]
	public void Steam_ListsTheOtherGames_LeavesOutToolsAndUnfinishedInstalls()
	{
		Steam(9009, "Pixel Racer", "PixelRacer");
		Steam(9020, "Moss Garden", "Moss Garden", flags: 6);
		Steam(228980, "Steamworks Common Redistributables", "Steamworks Shared");
		Steam(9002, "Proton 9.0", "Proton 9.0");
		// Being downloaded; folder gone; a catalog game by app id; a catalog game by its name (no app id in the catalog).
		Steam(9030, "Half Done", "Half Done", flags: 1026);
		Steam(9040, "Gone Game", "Gone Game", folder: false);
		Steam(1649240, "Returnal", "Returnal");
		Steam(9050, "Stellar Blade™", "SB Folder");

		InstalledScan scan = Finder().Scan(InstalledGameFinderTests.Catalog);

		Assert.Equal(new[] { "Moss Garden", "Pixel Racer" }, scan.Found.Select(g => g.Name));
		FoundGame racer = scan.Found.Single(g => g.Name == "Pixel Racer");
		Assert.Equal(InstalledSource.Steam, racer.Source);
		Assert.Equal($@"{Common}\PixelRacer", racer.Folder);
		Assert.Equal("steam:9009", racer.Key);
		Assert.Equal(9009, racer.SteamAppId);
		Assert.Null(racer.LaunchExe);
		Assert.Equal(new[] { "Returnal", "Stellar Blade" }, scan.Games.Select(g => g.Game.Name));
		Assert.Equal(2, scan.NotGames);
		string line = Assert.Single(logged);
		Assert.Matches(@"^installed games: 4 found \(steam 4, epic 0, registry 0, folders 0, seen 0; 2 in the list, 2 others, 2 not games left out\) in \d+ ms$", line);
		Assert.DoesNotContain("\\", line);
	}

	[Fact]
	public void Epic_ListsTheOtherGames_LeavesOutAddOnsEnginesAndUnfinishedInstalls()
	{
		files.Dir(@"E:\Epic\Hollow Peak").Dir(@"E:\Epic\Engine").Dir(@"E:\Epic\Addon").Dir(@"E:\Epic\Half").Dir(@"E:\Epic\NoLaunch").Dir(@"E:\Epic\AlanWake2");
		files.File(EpicDir + @"\A.item", """{ "DisplayName": "Hollow Peak", "AppName": "HollowPeakApp", "MainGameAppName": "HollowPeakApp", "AppCategories": ["public", "games", "applications"], "bIsApplication": true, "bIsExecutable": true, "InstallLocation": "E:\\Epic\\Hollow Peak", "LaunchExecutable": "Binaries/Win64/HollowPeak.exe" }""");
		files.File(EpicDir + @"\B.item", """{ "DisplayName": "Unreal Engine", "AppName": "UE_5.4", "AppCategories": ["public", "engines"], "InstallLocation": "E:\\Epic\\Engine", "LaunchExecutable": "Engine/Binaries/Win64/UnrealEditor.exe" }""");
		files.File(EpicDir + @"\C.item", """{ "DisplayName": "Hollow Peak - Winter Pack", "AppName": "WinterPack", "MainGameAppName": "HollowPeakApp", "AppCategories": ["public", "addons"], "InstallLocation": "E:\\Epic\\Addon", "LaunchExecutable": "x.exe" }""");
		files.File(EpicDir + @"\D.item", """{ "DisplayName": "Half Installed", "AppName": "Half", "bIsIncompleteInstall": true, "InstallLocation": "E:\\Epic\\Half", "LaunchExecutable": "Half.exe" }""");
		files.File(EpicDir + @"\E.item", """{ "DisplayName": "No Launch", "AppName": "NoLaunch", "InstallLocation": "E:\\Epic\\NoLaunch", "LaunchExecutable": "" }""");
		files.File(EpicDir + @"\F.item", """{ "DisplayName": "Alan Wake II", "AppName": "AW2", "InstallLocation": "E:\\Epic\\AlanWake2", "LaunchExecutable": "AlanWake2.exe" }""");

		InstalledScan scan = Finder().Scan(InstalledGameFinderTests.Catalog);

		FoundGame peak = Assert.Single(scan.Found);
		Assert.Equal("Hollow Peak", peak.Name);
		Assert.Equal(InstalledSource.Epic, peak.Source);
		Assert.Equal(@"E:\Epic\Hollow Peak", peak.Folder);
		Assert.Equal(@"E:\Epic\Hollow Peak\Binaries\Win64\HollowPeak.exe", peak.LaunchExe);
		Assert.Equal("epic:HollowPeakApp", peak.Key);
		Assert.Null(peak.SteamAppId);
		Assert.Equal("Alan Wake 2", Assert.Single(scan.Games).Game.Name);
		Assert.Equal(3, scan.NotGames);
	}

	// A found game whose folder or name is a catalog game found another way is shown once, as the catalog game.
	[Fact]
	public void FoundGame_ThatIsACatalogGame_IsShownOnce()
	{
		Steam(9060, "Cyberpunk 2077 Ultimate", "Cyberpunk 2077");
		files.File(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\Cyberpunk2077.exe");
		files.Dir(@"E:\Epic\Returnal");
		files.File(EpicDir + @"\A.item", """{ "DisplayName": "RT Deluxe", "AppName": "RT", "InstallLocation": "E:\\Epic\\Returnal", "LaunchExecutable": "RT.exe" }""");

		InstalledScan scan = Finder().Scan(InstalledGameFinderTests.Catalog, [Common]);

		Assert.Empty(scan.Found);
		Assert.Equal(new[] { "Cyberpunk 2077", "Returnal" }, scan.Games.Select(g => g.Game.Name));
	}

	// --- Recognising found games running ---

	private static readonly FoundGame Racer = new("Pixel Racer", InstalledSource.Steam, $@"{Common}\PixelRacer", "9009");
	private static readonly FoundGame Peak = new("Hollow Peak", InstalledSource.Epic, @"E:\Epic\Hollow Peak", "HollowPeakApp", @"E:\Epic\Hollow Peak\Binaries\Win64\HollowPeak.exe");

	private static (GameWatcher, GameModeTests.FakeProcesses, List<string>) Watcher(IProcessDetails? details = null)
	{
		var processes = new GameModeTests.FakeProcesses();
		var log = new List<string>();
		var watcher = new GameWatcher(InstalledGameFinderTests.Catalog, processes, details: details ?? new Details(), log: log.Add) { FoundGames = [Racer, Peak] };
		return (watcher, processes, log);
	}

	[Fact]
	public void Steam_AnyExeUnderTheInstallFolder()
	{
		(GameWatcher watcher, GameModeTests.FakeProcesses processes, List<string> log) = Watcher();
		processes.Show("Racer.exe", $@"{Common}\PixelRacer\bin\x64\Racer.exe");

		ForegroundGame game = watcher.Poll()!;

		Assert.Equal("Pixel Racer", game.Name);
		Assert.Null(game.Match);
		Assert.Same(Racer, game.Found);
		Assert.Equal("steam:9009", game.Key);
		Assert.Equal(["game: Pixel Racer recognised by Steam install folder (Racer.exe)"], log);
	}

	[Fact]
	public void Epic_LaunchExe_ThenAnyExeUnderTheInstallFolder()
	{
		(GameWatcher watcher, GameModeTests.FakeProcesses processes, List<string> log) = Watcher();
		processes.Show("HollowPeak.exe", @"E:\Epic\Hollow Peak\Binaries\Win64\HollowPeak.exe");
		Assert.Equal("epic:HollowPeakApp", watcher.Poll()!.Key);
		processes.Show("Helper.exe", @"E:\Epic\Hollow Peak\Tools\Helper.exe");
		Assert.Equal("Hollow Peak", watcher.Poll()!.Name);
		Assert.Equal(["game: Hollow Peak recognised by Epic launch exe (HollowPeak.exe)", "game: Hollow Peak recognised by Epic install folder (Helper.exe)"], log);
	}

	[Theory]
	[InlineData(@"D:\SteamLibrary\steamapps\common\PixelRacer2\Racer.exe")]
	[InlineData(@"D:\SteamLibrary\steamapps\common\Racer.exe")]
	[InlineData(@"D:\SteamLibrary\steamapps\common\PixelRacer")]
	[InlineData(@"E:\Epic\Hollow Peak Tools\HollowPeak.exe")]
	[InlineData(@"E:\Epic\HollowPeak.exe")]
	[InlineData(@"C:\Elsewhere\HollowPeak.exe")]
	[InlineData(null)]
	public void NothingOutsideTheInstallFolder_Matches(string? path)
	{
		(GameWatcher watcher, GameModeTests.FakeProcesses processes, _) = Watcher();
		processes.Show(path == null ? "HollowPeak.exe" : Path.GetFileName(path), path);
		Assert.Null(watcher.Poll());
	}

	// Inside a found game's folder, every catalog signal still wins: exe name, Steam app id, folder name, version info and
	// the window title.
	[Theory]
	[InlineData(@"D:\SteamLibrary\steamapps\common\PixelRacer\Cyberpunk2077.exe", null, "Cyberpunk 2077", "exe name")]
	[InlineData(@"D:\SteamLibrary\steamapps\common\PixelRacer\Returnal\Launcher.exe", null, "Returnal", "folder name")]
	[InlineData(@"D:\SteamLibrary\steamapps\common\PixelRacer\Launcher.exe", "Stellar Blade", "Stellar Blade", "window title")]
	[InlineData(@"E:\Epic\Hollow Peak\Binaries\Win64\HollowPeak.exe", "Alan Wake 2", "Alan Wake 2", "window title")]
	public void FoundGame_NeverShadowsACatalogGame(string path, string? title, string catalogName, string signal)
	{
		(GameWatcher watcher, GameModeTests.FakeProcesses processes, List<string> log) = Watcher(new Details { Title = title });
		processes.Show(Path.GetFileName(path), path);

		ForegroundGame game = watcher.Poll()!;

		Assert.Equal(catalogName, game.Name);
		Assert.NotNull(game.Match);
		Assert.Null(game.Found);
		Assert.Equal([$"game: {catalogName} recognised by {signal} ({Path.GetFileName(path)})"], log);
	}

	[Fact]
	public void FoundGame_NeverShadowsACatalogGame_BySteamAppId()
	{
		var processes = new GameModeTests.FakeProcesses();
		var steam = new GameModeTests.FakeSteamFiles(@"C:\Steam", new Dictionary<string, string>
		{
			[@"C:\Steam\steamapps\appmanifest_1649240.acf"] = Manifest(1649240, "Returnal", "RT"),
		});
		var watcher = new GameWatcher(InstalledGameFinderTests.Catalog, processes, new SteamLibrary(steam), new Details(), _ => { })
		{
			FoundGames = [new FoundGame("Wrong", InstalledSource.Steam, @"C:\Steam\steamapps\common\RT", "1")]
		};
		processes.Show("Stub.exe", @"C:\Steam\steamapps\common\RT\Stub.exe");
		Assert.Equal("Returnal", watcher.Poll()!.Name);
	}

	// A user's added game wins too, and a found game that is no longer in the list (hidden) is not recognised.
	[Fact]
	public void AddedGameWins_AndAHiddenGameIsNotRecognised()
	{
		(GameWatcher watcher, GameModeTests.FakeProcesses processes, _) = Watcher();
		watcher.UserGames = [new UserGame("My Racer", $@"{Common}\PixelRacer\Racer.exe")];
		processes.Show("Racer.exe", $@"{Common}\PixelRacer\Racer.exe");
		Assert.Equal("My Racer", watcher.Poll()!.Name);

		watcher.UserGames = [];
		watcher.FoundGames = [Peak];
		Assert.Null(watcher.Poll());
	}

	// The per-game controller type follows a found game's key.
	[Fact]
	public void Controller_UsesTheFoundGamesKey()
	{
		var processes = new GameModeTests.FakeProcesses();
		var watcher = new GameWatcher(InstalledGameFinderTests.Catalog, processes, details: new Details(), log: _ => { });
		var games = new GameModeController(InstalledGameFinderTests.Catalog, watcher, null) { Log = _ => { } };
		games.FoundGames = [Racer];
		games.SetPadType("steam:9009", Kopru.Core.Virtual.VirtualPadType.Xbox360);
		processes.Show("Racer.exe", $@"{Common}\PixelRacer\Racer.exe");
		games.Tick();
		Assert.Equal("Pixel Racer", games.State.Name);
		Assert.Equal(Kopru.Core.Virtual.VirtualPadType.Xbox360, games.CurrentPadType);
	}

	private sealed class Details : IProcessDetails
	{
		public string? Title { get; init; }

		public string? WindowTitle(ProcessInfo process) => Title;

		public (string? Product, string? Description) VersionInfo(string path) => (null, null);
	}
}
