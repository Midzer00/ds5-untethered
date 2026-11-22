// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Steam;

namespace Kopru.Core.Tests;

// Known-game recognition (catalog exe, Steam library, user-added) and the DualSense Mode switch (only the user changes it).
public class GameModeTests
{
	private const string SteamRoot = @"C:\Program Files (x86)\Steam";

	// A catalog game without exe names: found through its Steam app id only.
	private static readonly GameCatalog Catalog = new(
	[
		new CatalogGame("Returnal", ["Returnal-Win64-Shipping.exe"], 1649240, true, true, "high"),
		new CatalogGame("Cyberpunk 2077", [], 1091500, true, true),
		new CatalogGame("Assetto Corsa Competizione", ["acc.exe"], null, false, true),
	]);

	private const string LibraryFoldersVdf = """
		"libraryfolders"
		{
			"0"
			{
				"path"		"C:\\Program Files (x86)\\Steam"
				"label"		""
				"apps" { "228980" "0" }
			}
			"1"
			{
				"path"		"D:\\SteamLibrary"
				"apps" { "1091500" "70000000000" }
			}
		}
		""";

	private const string OldLibraryFoldersVdf = """
		"LibraryFolders"
		{
			"TimeNextStatsReport"		"1700000000"
			"ContentStatsID"		"-123"
			"1"		"E:\\Games\\Steam"
		}
		""";

	private static string Manifest(long appId, string installDir) => $$"""
		"AppState"
		{
			"appid"		"{{appId}}"
			"Universe"		"1"
			"name"		"Game"
			"installdir"		"{{installDir}}"
		}
		""";

	private static FakeSteamFiles SteamFixture() => new(SteamRoot, new()
	{
		[SteamRoot + @"\steamapps\libraryfolders.vdf"] = LibraryFoldersVdf,
		[SteamRoot + @"\steamapps\appmanifest_1649240.acf"] = Manifest(1649240, "Returnal"),
		[@"D:\SteamLibrary\steamapps\appmanifest_1091500.acf"] = Manifest(1091500, "Cyberpunk 2077"),
		[@"D:\SteamLibrary\steamapps\appmanifest_99.acf"] = "\"AppState\" { \"appid\" \"99\" }",
		[@"D:\SteamLibrary\steamapps\appmanifest_7.acf"] = "{ broken",
	});

	[Fact]
	public void Steam_LibraryFolders_BothFormats()
	{
		Assert.Equal([SteamRoot, @"D:\SteamLibrary"], SteamLibrary.LibraryFolders(SteamRoot + "\\", Vdf.Parse(LibraryFoldersVdf)));
		Assert.Equal([SteamRoot, @"E:\Games\Steam"], SteamLibrary.LibraryFolders(SteamRoot, Vdf.Parse(OldLibraryFoldersVdf)));
		Assert.Equal([SteamRoot], SteamLibrary.LibraryFolders(SteamRoot, null));
	}

	[Fact]
	public void Steam_Manifest_GivesAppIdAndInstallDir()
	{
		Assert.Equal((1091500L, "Cyberpunk 2077"), SteamLibrary.ParseManifest(Manifest(1091500, "Cyberpunk 2077")));
		Assert.Null(SteamLibrary.ParseManifest("\"AppState\" { \"appid\" \"99\" }"));
		Assert.Null(SteamLibrary.ParseManifest("\"AppState\" { \"appid\" \"x\" \"installdir\" \"A\" }"));
	}

	[Theory]
	[InlineData(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", 1091500L)]
	[InlineData(@"d:/steamlibrary/STEAMAPPS/common/cyberpunk 2077/bin/x64/Cyberpunk2077.exe", 1091500L)]
	[InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Returnal\Returnal\Binaries\Win64\Returnal-Win64-Shipping.exe", 1649240L)]
	[InlineData(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077 Mods\tool.exe", null)]
	[InlineData(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077", null)]
	[InlineData(@"C:\Windows\notepad.exe", null)]
	[InlineData(null, null)]
	public void Steam_MapsAProcessPathToItsAppId(string? path, long? appId) =>
		Assert.Equal(appId, new SteamLibrary(SteamFixture()).AppIdFor(path));

	[Fact]
	public void Steam_ReadsAgainAfterTheRefreshInterval()
	{
		long now = 0;
		FakeSteamFiles files = SteamFixture();
		var library = new SteamLibrary(files, () => now);
		const string game = @"D:\SteamLibrary\steamapps\common\New Game\game.exe";
		Assert.Null(library.AppIdFor(game));
		files.Files[@"D:\SteamLibrary\steamapps\appmanifest_5.acf"] = Manifest(5, "New Game");
		now += 1000;
		Assert.Null(library.AppIdFor(game));
		now += (long)SteamLibrary.RefreshInterval.TotalMilliseconds;
		Assert.Equal(5, library.AppIdFor(game));
		Assert.Null(new SteamLibrary(new FakeSteamFiles(null, [])).AppIdFor(game));
	}

	[Fact]
	public void Watcher_RecognisesCatalogExe_SteamLibraryAndUserGames()
	{
		var processes = new FakeProcesses();
		var watcher = new GameWatcher(Catalog, processes, new SteamLibrary(SteamFixture()));

		processes.Show("RETURNAL-WIN64-SHIPPING.EXE");
		Assert.Equal("Returnal", watcher.Poll()?.Name);

		// No exe in the list: found through the Steam library folder.
		processes.Show("Cyberpunk2077.exe", @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe");
		ForegroundGame? cyberpunk = watcher.Poll();
		Assert.Equal("Cyberpunk 2077", cyberpunk?.Name);
		Assert.Equal("Cyberpunk 2077", cyberpunk?.Key);
		Assert.Equal(1091500, cyberpunk?.Match?.SteamAppId);

		// A Steam game not in the list is not a known game.
		processes.Show("Other.exe", SteamRoot + @"\steamapps\common\Returnal2\Other.exe");
		Assert.Null(watcher.Poll());

		// Games the user added: by full path, or by file name.
		processes.Show("Indie.exe", @"C:\Games\Indie\Indie.exe");
		Assert.Null(watcher.Poll());
		watcher.UserGames = [new UserGame("My Indie", @"C:\Games\Indie\Indie.exe"), new UserGame("Tetris", "tetris.exe")];
		Assert.Equal("My Indie", watcher.Poll()?.Name);
		Assert.Equal("Indie.exe", watcher.Poll()?.Key);
		processes.Show("Indie.exe", @"D:\Elsewhere\Indie.exe");
		Assert.Null(watcher.Poll());
		processes.Show("Tetris.exe", @"X:\any\Tetris.exe");
		Assert.Equal("Tetris", watcher.Poll()?.Name);
	}

	[Theory]
	[InlineData("claude.exe", @"C:\Users\me\AppData\Local\AnthropicClaude\claude.exe")]
	[InlineData("Claude.exe", null)]
	[InlineData("chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
	[InlineData("msedge.exe", null)]
	[InlineData("Discord.exe", null)]
	[InlineData("notepad.exe", @"C:\Windows\notepad.exe")]
	[InlineData("Tetris.exe", @"C:\Games\Tetris.exe")]
	[InlineData("explorer.exe", null)]
	public void Watcher_NeverReportsDesktopOrUnknownApps(string exe, string? path)
	{
		var processes = new FakeProcesses();
		var watcher = new GameWatcher(Catalog, processes, new SteamLibrary(SteamFixture()));
		processes.Show(exe, path);
		Assert.Null(watcher.Poll());
		// Even a list entry naming one does not make it a game.
		var listed = new GameWatcher(new GameCatalog([new CatalogGame("Browser", ["chrome.exe", "claude.exe"])]), processes);
		if (exe is "chrome.exe" or "claude.exe" or "Claude.exe")
		{
			Assert.Null(listed.Poll());
		}
	}

	[Fact]
	public void Watcher_IgnoresThisApp_AndCachesPerProcess()
	{
		var processes = new FakeProcesses();
		var files = SteamFixture();
		var watcher = new GameWatcher(Catalog, processes, new SteamLibrary(files));
		processes.Show(Path.GetFileName(Environment.ProcessPath)!);
		Assert.Null(watcher.Poll());
		processes.Show("Cyberpunk2077.exe", @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\Cyberpunk2077.exe");
		Assert.NotNull(watcher.Poll());
		int reads = files.Reads;
		for (int i = 0; i < 5; i++)
		{
			Assert.NotNull(watcher.Poll());
		}
		Assert.Equal(reads, files.Reads);
	}

	// One profile for every game: the game's own effects pass through (scaled) and the classic rumble settings and the
	// fallback trigger effect go along for what the game does not send.
	[Fact]
	public void OneProfile_GoesToTheBridge_WithOrWithoutAGame()
	{
		var bridge = new GamesTests.FakeBridge();
		var processes = new FakeProcesses();
		var games = new GameModeController(Catalog, new GameWatcher(Catalog, processes), bridge);
		games.SetProfile(new GameProfile(2.0, 0.5, true, 0.8, RumbleFeel.Punchy, FallbackTrigger.Weapon, 1));
		Assert.Equal(GameMode.Supported, games.State.Mode);
		BridgeProfile p = bridge.Applied[^1];
		Assert.Equal((2.0, 0.5, true, 0.8, RumbleFeel.Punchy), (p.GameHapticsLevel, p.TriggerIntensity, p.LightFromGame, p.RumbleLevel, p.Feel));
		Assert.Equal(GameProfiles.Effect(FallbackTrigger.Weapon, 1), p.FallbackTrigger);
		int applied = bridge.Applied.Count;
		games.SetProfile(new GameProfile(2.0, 0.5, true, 0.8, RumbleFeel.Punchy, FallbackTrigger.Weapon, 1));
		Assert.Equal(applied, bridge.Applied.Count);

		// Unknown apps in front change nothing.
		processes.Show("chrome.exe");
		games.Tick();
		Assert.Null(games.State.Name);
		Assert.Equal(applied, bridge.Applied.Count);
	}

	// Every game session (a known game starting, ending or taking over) is a new GameSession in the profile: the fallback
	// trigger effect may come back for the next game.
	[Fact]
	public void GameSessions_AreCounted_InTheBridgeProfile()
	{
		var bridge = new GamesTests.FakeBridge();
		var processes = new FakeProcesses();
		var games = new GameModeController(Catalog, new GameWatcher(Catalog, processes), bridge);
		long start = bridge.Applied[^1].GameSession;
		processes.Show("acc.exe");
		games.Tick();
		long playing = bridge.Applied[^1].GameSession;
		Assert.NotEqual(start, playing);
		processes.Show("explorer.exe");
		games.Tick();
		processes.Show("acc.exe");
		games.Tick();
		Assert.Equal(playing, bridge.Applied[^1].GameSession);
		processes.Exit("acc.exe");
		games.Tick();
		Assert.NotEqual(playing, bridge.Applied[^1].GameSession);
	}

	[Fact]
	public void UserGames_AddRemove_KnownGamesAndMemory()
	{
		(GameModeController games, FakeProcesses processes) = Setup();
		int catalogChanged = 0;
		games.CatalogChanged += () => catalogChanged++;
		Assert.True(games.AddUserGame(new UserGame("Indie", @"C:\Games\Indie.exe")));
		Assert.False(games.AddUserGame(new UserGame("Indie again", @"c:\games\INDIE.exe")));
		Assert.False(games.AddUserGame(new UserGame("Blank", " ")));
		KnownGame indie = Assert.Single(games.KnownGames(), g => g.IsUserAdded);
		Assert.Equal(("Indie", "Indie.exe", "Indie.exe"), (indie.Name, indie.Exe, indie.Key));
		Assert.Equal(4, games.KnownGames().Count);
		Assert.Equal(1, catalogChanged);

		processes.Show("Indie.exe", @"C:\Games\Indie.exe");
		games.Tick();
		Assert.Equal("Indie", games.State.Name);

		GameMemory saved = games.SnapshotMemory();
		var again = new GameModeController(Catalog, new GameWatcher(Catalog, processes), null);
		again.LoadMemory(saved);
		Assert.Single(again.UserGames);
		Assert.True(again.RemoveUserGame(@"C:\Games\Indie.exe"));
		Assert.False(again.RemoveUserGame(@"C:\Games\Indie.exe"));
		Assert.Empty(again.UserGames);
		Assert.Equal(3, again.KnownGames().Count);
	}

	[Fact]
	public void Memory_LoadsTheUserGames()
	{
		var games = new GameModeController(Catalog, new GameWatcher(Catalog, new FakeProcesses()), null);
		games.LoadMemory(new GameMemory([new UserGame("A", "a.exe"), new UserGame("A2", "A.EXE")]));
		Assert.Equal(GameMode.Supported, games.State.Mode);
		Assert.Single(games.UserGames);
		Assert.Single(games.SnapshotMemory().UserGames);
	}

	[Fact]
	public void SetCatalog_SwapsTheWatchersList()
	{
		var processes = new FakeProcesses();
		var watcher = new GameWatcher(GameCatalog.Empty, processes);
		var games = new GameModeController(GameCatalog.Empty, watcher, null);
		processes.Show("acc.exe");
		games.Tick();
		Assert.Null(games.State.Name);
		int changed = 0;
		games.CatalogChanged += () => changed++;
		games.SetCatalog(Catalog);
		Assert.Same(Catalog, watcher.Catalog);
		Assert.Equal(1, changed);
		games.Tick();
		Assert.Equal("Assetto Corsa Competizione", games.State.Name);
	}

	private static (GameModeController, FakeProcesses) Setup()
	{
		var processes = new FakeProcesses();
		var games = new GameModeController(Catalog, new GameWatcher(Catalog, processes), new GamesTests.FakeBridge());
		return (games, processes);
	}

	// Processes by exe name: Show starts one (or brings it to the front), Exit ends it.
	internal sealed class FakeProcesses : IProcessSource
	{
		private readonly Dictionary<string, ProcessInfo> running = new(StringComparer.OrdinalIgnoreCase);
		private int nextId = 100;
		private ProcessInfo? current;

		public void Show(string exe, string? path = null)
		{
			if (!running.TryGetValue(exe, out ProcessInfo? process) || process.Path != path)
			{
				process = new ProcessInfo(nextId++, exe, path);
				running[exe] = process;
			}
			current = process;
		}

		public void Exit(string exe)
		{
			running.Remove(exe);
			if (current?.Exe == exe)
			{
				current = null;
			}
		}

		public ProcessInfo? Foreground() => current;

		public bool IsRunning(ProcessInfo process) => running.Values.Any(p => p.Id == process.Id);

		public IReadOnlyList<ProcessInfo> WindowedApps() => running.Values.ToList();

		// A process that runs without coming to the front (a game starting behind its launcher).
		public void Start(string exe, string? path = null)
		{
			if (!running.ContainsKey(exe))
			{
				running[exe] = new ProcessInfo(nextId++, exe, path);
			}
		}

		public int AllProcessesCalls { get; private set; }

		public IReadOnlyList<ProcessInfo> AllProcesses()
		{
			AllProcessesCalls++;
			return running.Values.ToList();
		}
	}

	// Steam's files in memory.
	internal sealed class FakeSteamFiles(string? steamPath, Dictionary<string, string> files) : ISteamEnvironment
	{
		public Dictionary<string, string> Files { get; } = new(files, StringComparer.OrdinalIgnoreCase);
		public int Reads { get; private set; }
		public string? SteamPath => steamPath;
		public bool IsSteamRunning => true;
		public bool FileExists(string path) => Files.ContainsKey(path);

		public string ReadAllText(string path)
		{
			Reads++;
			return Files.TryGetValue(path, out string? text) ? text : throw new FileNotFoundException(path);
		}

		public IEnumerable<string> GetDirectories(string path) => [];
		public DateTime GetLastWriteTimeUtc(string path) => DateTime.UnixEpoch;

		public IEnumerable<string> GetFiles(string path, string pattern)
		{
			string prefix = path.TrimEnd('\\') + "\\";
			return Files.Keys.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
				&& !f.AsSpan(prefix.Length).Contains('\\')
				&& System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, f.AsSpan(prefix.Length))).ToList();
		}
	}
}
