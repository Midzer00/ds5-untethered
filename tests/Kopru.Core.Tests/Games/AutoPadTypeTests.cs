// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// Controller type "Automatic": (1) the game's own choice, else (2) what we know about the game: DualSense for a listed
// game or one learned to use the DualSense natively, a Steam game's store categories (DualSense / DualShock 4), (3) the
// default type for a game we know nothing about, (4) else Xbox 360; with no game running the default type, else
// DualSense. The type is put in when the game's process starts, for Automatic games as for games with their own choice.
[Trait("Category", "Windows")]
public class AutoPadTypeTests : IDisposable
{
	private const long DualSenseApp = 9101;
	private const long DualShockApp = 9102;
	private const long PlainApp = 9103;
	private const long LocalApp = 9104;

	private static readonly GameCatalog Catalog = new([
		new CatalogGame("Listed Racer", ["racer.exe"], 9100, true, true)
	]);

	private static readonly PlayStationSupport Shipped = new([
		new(DualSenseApp, PlayStationPad.DualSense),
		new(DualShockApp, PlayStationPad.DualShock4)
	], "2026-11-17");

	private readonly string root = Path.Combine(Path.GetTempPath(), "kopru-autopad-" + Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		try
		{
			Directory.Delete(root, true);
		}
		catch (IOException)
		{
		}
	}

	// The tests' clock (the launch-time scan runs at most every LaunchScanInterval for the same games).
	private long now = 1_000_000;

	private void Later() => now += (long)GameModeController.LaunchScanInterval.TotalMilliseconds;

	// A Steam game found installed, with its exe on disk (the launch-time scan looks for the exes in its folder). type: the
	// kind Steam's cache lists (null: not read).
	private FoundGame Steam(long appId, PlayStationPad? local = null, string? type = "Game")
	{
		string folder = Path.Combine(root, "common", "Game" + appId);
		Directory.CreateDirectory(folder);
		File.WriteAllBytes(Path.Combine(folder, $"game{appId}.exe"), []);
		return new FoundGame("Game " + appId, InstalledSource.Steam, folder, appId.ToString(System.Globalization.CultureInfo.InvariantCulture), PlayStation: local,
			SteamType: type);
	}

	private static readonly FoundGame Epic = new("Epic Game", InstalledSource.Epic, @"E:\Epic\EpicGame", "EpicGameApp", @"E:\Epic\EpicGame\EpicGame.exe");

	private (GameModeController Games, GameModeTests.FakeProcesses Processes, SignalBridge Bridge) Make(params FoundGame[] found) =>
		Make(null, found);

	private (GameModeController Games, GameModeTests.FakeProcesses Processes, SignalBridge Bridge) Make(Kopru.Core.Steam.SteamLibrary? steam,
		params FoundGame[] found)
	{
		var processes = new GameModeTests.FakeProcesses();
		var bridge = new SignalBridge();
		var watcher = new GameWatcher(Catalog, processes, steam, log: _ => { });
		// The end of the chain on a PC without the Xbox Series pad: Xbox 360 (XboxSeriesPadTypeTests covers Xbox Series).
		var games = new GameModeController(Catalog, watcher, bridge) { Log = _ => { }, PlayStation = Shipped, ClockMs = () => now, XboxSeriesOsSupported = false };
		games.FoundGames = found;
		return (games, processes, bridge);
	}

	[Fact]
	public void Rule2_ListedGame_GetsDualSense()
	{
		(GameModeController games, _, _) = Make();
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor("Listed Racer"));
		Assert.Equal(VirtualPadType.DualSense, games.AutomaticPadTypeFor("Listed Racer"));
	}

	[Fact]
	public void Rule3_SteamCategories_DualSenseOrDualShock4()
	{
		(GameModeController games, _, _) = Make(Steam(DualSenseApp), Steam(DualShockApp));
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor("steam:" + DualSenseApp));
		Assert.Equal(VirtualPadType.DualShock4, games.PadTypeFor("steam:" + DualShockApp));
	}

	[Fact]
	public void Rule3_LocalSteamCache_ForGamesTheShippedListDoesNotKnow_ShippedWins()
	{
		(GameModeController games, _, _) = Make(Steam(LocalApp, PlayStationPad.DualShock4), Steam(DualSenseApp, PlayStationPad.DualShock4));
		Assert.Equal(VirtualPadType.DualShock4, games.PadTypeFor("steam:" + LocalApp));
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor("steam:" + DualSenseApp));
	}

	[Fact]
	public void Rule4_OtherKnownGames_GetXbox360()
	{
		(GameModeController games, _, _) = Make(Steam(PlainApp), Epic);
		games.AddUserGame(new UserGame("Hand Added", @"C:\Games\Hand\hand.exe"));
		Assert.Equal(VirtualPadType.Xbox360, games.PadTypeFor("steam:" + PlainApp));
		Assert.Equal(VirtualPadType.Xbox360, games.PadTypeFor(Epic.Key));
		Assert.Equal(VirtualPadType.Xbox360, games.PadTypeFor("hand.exe"));
		// A hand-added copy of a listed game is the listed game.
		games.AddUserGame(new UserGame("My Racer", @"C:\Games\Racer\racer.exe"));
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor(ForegroundGame.KeyOf(Catalog.Find("racer.exe"), "racer.exe")));
	}

	[Fact]
	public void Rule1_OwnChoice_WinsOverEverything()
	{
		(GameModeController games, _, _) = Make(Steam(PlainApp), Steam(DualSenseApp));
		games.SetForcedPadType(VirtualPadType.DualShock4);
		games.SetPadType("steam:" + DualSenseApp, VirtualPadType.Xbox360);
		games.SetPadType("Listed Racer", VirtualPadType.Xbox360);
		games.SetPadType("steam:" + PlainApp, VirtualPadType.DualSense);
		Assert.Equal(VirtualPadType.Xbox360, games.PadTypeFor("steam:" + DualSenseApp));
		Assert.Equal(VirtualPadType.Xbox360, games.PadTypeFor("Listed Racer"));
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor("steam:" + PlainApp));
	}

	// The default type is for the games we know nothing about: what we know beats it.
	[Fact]
	public void DefaultXbox360_KnownGamesStillGetTheirController()
	{
		(GameModeController games, _, _) = Make(Steam(DualSenseApp), Steam(DualShockApp), Steam(PlainApp));
		games.SetForcedPadType(VirtualPadType.Xbox360);
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor("Listed Racer"));
		Assert.Equal(VirtualPadType.DualSense, games.AutomaticPadTypeFor("Listed Racer"));
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor("steam:" + DualSenseApp));
		Assert.Equal(VirtualPadType.DualShock4, games.PadTypeFor("steam:" + DualShockApp));
		Assert.Equal(VirtualPadType.Xbox360, games.PadTypeFor("steam:" + PlainApp));
	}

	[Fact]
	public void DefaultType_UsedForGamesWeKnowNothingAbout_ElseXbox360()
	{
		(GameModeController games, _, _) = Make(Steam(PlainApp), Epic);
		games.AddUserGame(new UserGame("Hand Added", @"C:\Games\Hand\hand.exe"));
		Assert.Equal(VirtualPadType.Xbox360, games.AutomaticPadTypeFor("steam:" + PlainApp));
		games.SetForcedPadType(VirtualPadType.DualSense);
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor("steam:" + PlainApp));
		Assert.Equal(VirtualPadType.DualSense, games.AutomaticPadTypeFor(Epic.Key));
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor("hand.exe"));
		games.SetForcedPadType(VirtualPadType.DualShock4);
		Assert.Equal(VirtualPadType.DualShock4, games.PadTypeFor("steam:" + PlainApp));
		games.SetForcedPadType(null);
		Assert.Equal(VirtualPadType.Xbox360, games.PadTypeFor("steam:" + PlainApp));
		Assert.Equal(VirtualPadType.Xbox360, games.PadTypeFor("hand.exe"));
	}

	// Running with a default type: a known game says Automatic, a game we know nothing about the default type.
	[Fact]
	public void RunningGame_SourceNamesWhatPickedTheType()
	{
		FoundGame plain = Steam(PlainApp), ds = Steam(DualSenseApp);
		(GameModeController games, GameModeTests.FakeProcesses processes, SignalBridge bridge) = Make(plain, ds);
		games.SetForcedPadType(VirtualPadType.DualShock4);
		string dsExe = Path.Combine(ds.Folder, $"game{DualSenseApp}.exe");
		processes.Show(Path.GetFileName(dsExe), dsExe);
		games.Tick();
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);
		Assert.Equal(PadTypeSource.Automatic, games.PadStatus.Source);
		processes.Exit(Path.GetFileName(dsExe));
		games.Tick();
		string plainExe = Path.Combine(plain.Folder, $"game{PlainApp}.exe");
		processes.Show(Path.GetFileName(plainExe), plainExe);
		games.Tick();
		Assert.Equal(VirtualPadType.DualShock4, bridge.Pads[^1]);
		Assert.Equal(PadTypeSource.DefaultSetting, games.PadStatus.Source);
	}

	// A Steam game added by hand (keyed by its exe) whose exe lies in a library's install folder gets that app's store data.
	[Fact]
	public void HandAddedSteamGame_UsesTheAppOfItsLibraryFolder()
	{
		var files = new GameModeTests.FakeSteamFiles(@"C:\Steam", new()
		{
			[@"C:\Steam\steamapps\libraryfolders.vdf"] = """
				"libraryfolders" { "1" { "path" "D:\\SteamLibrary" } }
				""",
			[$@"D:\SteamLibrary\steamapps\appmanifest_{DualShockApp}.acf"] = $$"""
				"AppState" { "appid" "{{DualShockApp}}" "installdir" "Pad Game" }
				"""
		});
		(GameModeController games, _, _) = Make(new Kopru.Core.Steam.SteamLibrary(files));
		games.AddUserGame(new UserGame("Pad Game", @"D:\SteamLibrary\steamapps\common\Pad Game\bin\padgame.exe"));
		games.AddUserGame(new UserGame("Other", @"D:\SteamLibrary\steamapps\common\Other Game\other.exe"));
		Assert.Equal(VirtualPadType.DualShock4, games.PadTypeFor("padgame.exe"));
		Assert.Equal(VirtualPadType.DualShock4, games.AutomaticPadTypeFor("padgame.exe"));
		Assert.Equal(VirtualPadType.Xbox360, games.PadTypeFor("other.exe"));
	}

	[Fact]
	public void Rule5_NoGame_DefaultTypeElseDualSense()
	{
		(GameModeController games, _, SignalBridge bridge) = Make(Steam(PlainApp));
		Assert.Equal(VirtualPadType.DualSense, games.CurrentPadType);
		Assert.Equal(PadTypeSource.Default, games.PadStatus.Source);
		games.SetForcedPadType(VirtualPadType.Xbox360);
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);
	}

	[Fact]
	public void LearnedNative_OverridesTheCategoriesAndXbox360_AndIsSaved()
	{
		FoundGame plain = Steam(PlainApp);
		(GameModeController games, GameModeTests.FakeProcesses processes, SignalBridge bridge) = Make(plain, Steam(DualShockApp));
		games.SetPadType("steam:" + PlainApp, VirtualPadType.DualSense);
		int saves = 0;
		games.MemoryChanged += () => saves++;
		string exe = Path.Combine(plain.Folder, $"game{PlainApp}.exe");
		processes.Show(Path.GetFileName(exe), exe);
		games.Tick();
		Assert.Equal("steam:" + PlainApp, games.ActiveGame!.Key);

		// Rumble alone teaches nothing that counts; one trigger effect is not enough either, a few seconds of them in front are.
		bridge.Signals = new GameSignals(false, false, true);
		games.Tick();
		Assert.Empty(games.NativeGames);
		for (int i = 1; i < NativeSupportDetector.NativeTicks; i++)
		{
			bridge.Signals = new GameSignals(false, true, false);
			games.Tick();
		}
		Assert.Empty(games.NativeGames);
		bridge.Signals = new GameSignals(false, true, false);
		games.Tick();
		Assert.Equal(["steam:" + PlainApp], games.NativeGames);
		Assert.Equal(1, saves);
		Assert.Equal(["steam:" + PlainApp], games.SnapshotMemory().NativeGames!);

		// Back on Automatic it keeps DualSense (rule 2 before 4).
		games.SetPadType("steam:" + PlainApp, null);
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor("steam:" + PlainApp));

		// Saved and loaded: learned DualSense also beats a DualShock-only store page.
		(GameModeController again, _, _) = Make(Steam(PlainApp), Steam(DualShockApp));
		again.LoadMemory(GameMemory.Default with { NativeGames = ["steam:" + PlainApp, "steam:" + DualShockApp] });
		Assert.Equal(VirtualPadType.DualSense, again.PadTypeFor("steam:" + PlainApp));
		Assert.Equal(VirtualPadType.DualSense, again.PadTypeFor("steam:" + DualShockApp));
	}

	// The bug: a Steam game without PlayStation support got a DualSense it can't use. Now its process starting (behind its
	// launcher, not in front) puts Xbox 360 in before the game looks for controllers, like an own choice would.
	[Fact]
	public void LaunchTimeSwap_AutomaticSteamGameWithoutCategories_GetsXbox360AtProcessStart()
	{
		FoundGame plain = Steam(PlainApp);
		(GameModeController games, GameModeTests.FakeProcesses processes, SignalBridge bridge) = Make(plain);
		List<PadSwap> swaps = [];
		games.PadSwapped += swaps.Add;
		games.Tick();
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);

		string exe = Path.Combine(plain.Folder, $"game{PlainApp}.exe");
		processes.Start(Path.GetFileName(exe), exe);
		// The processes are listed at most every LaunchScanInterval.
		games.Tick();
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);
		Later();
		games.Tick();
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);
		Assert.Equal(new PadSwap(VirtualPadType.DualSense, VirtualPadType.Xbox360, plain.Key, plain.Name), Assert.Single(swaps));
		Assert.Equal(PadTypeSource.Automatic, games.PadStatus.Source);
		Assert.Null(games.PendingPadType);

		// It exits: DualSense again.
		processes.Exit(Path.GetFileName(exe));
		games.Tick();
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);
	}

	[Fact]
	public void LaunchTimeScan_LeavesOutGamesThatGetWhatRunsAlready()
	{
		FoundGame ds = Steam(DualSenseApp);
		(GameModeController games, GameModeTests.FakeProcesses processes, SignalBridge bridge) = Make(ds);
		games.Tick();
		Assert.Equal(0, processes.AllProcessesCalls);
		// Default DualSense: a game we know nothing about gets it too, and it runs already: nothing to look for either.
		(GameModeController forced, GameModeTests.FakeProcesses forcedProcesses, _) = Make(Steam(PlainApp));
		forced.SetForcedPadType(VirtualPadType.DualSense);
		Assert.Equal(VirtualPadType.DualSense, forced.PadTypeFor("steam:" + PlainApp));
		forced.Tick();
		Assert.Equal(0, forcedProcesses.AllProcessesCalls);
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);
		// Default DualShock 4: the listed game still gets DualSense, so it is looked for.
		forced.SetForcedPadType(VirtualPadType.DualShock4);
		forced.Tick();
		Assert.Equal(1, forcedProcesses.AllProcessesCalls);
	}

	// A game already running when its type changes (here: learned, while the default type is set elsewhere) still waits for
	// "Şimdi Uygula" or a restart.
	[Fact]
	public void RunningAutomaticGame_ChangeWaitsAsPending_ApplyNowPutsItIn()
	{
		FoundGame plain = Steam(PlainApp);
		(GameModeController games, GameModeTests.FakeProcesses processes, SignalBridge bridge) = Make(plain);
		string exe = Path.Combine(plain.Folder, $"game{PlainApp}.exe");
		processes.Show(Path.GetFileName(exe), exe);
		games.Tick();
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);
		games.PlayStation = new PlayStationSupport([new(PlainApp, PlayStationPad.DualShock4)]);
		Assert.Equal(VirtualPadType.Xbox360, games.CurrentPadType);
		Assert.Equal(VirtualPadType.DualShock4, games.PendingPadType);
		Assert.True(games.ApplyPadTypeNow());
		Assert.Equal(VirtualPadType.DualShock4, bridge.Pads[^1]);
		Assert.Null(games.PendingPadType);
	}

	// The processes are listed at most every LaunchScanInterval while the same games are looked for; a change of those games
	// lists them at once.
	[Fact]
	public void LaunchTimeScan_IsThrottled()
	{
		(GameModeController games, GameModeTests.FakeProcesses processes, _) = Make(Steam(PlainApp));
		games.Tick();
		games.Tick();
		games.Tick();
		Assert.Equal(1, processes.AllProcessesCalls);
		Later();
		games.Tick();
		Assert.Equal(2, processes.AllProcessesCalls);
		games.SetPadType("Listed Racer", VirtualPadType.Xbox360);
		games.Tick();
		Assert.Equal(3, processes.AllProcessesCalls);
	}

	// Software Steam's cache was not read for (or that it calls something other than a game; the scan leaves those out) is
	// never looked for in the background: Wallpaper Engine running must not swap the controller on the desktop.
	[Fact]
	public void BackgroundApp_NotKnownAsAGame_IsNotLookedFor()
	{
		FoundGame wallpaper = Steam(PlainApp, type: null);
		(GameModeController games, GameModeTests.FakeProcesses processes, SignalBridge bridge) = Make(wallpaper);
		string exe = Path.Combine(wallpaper.Folder, $"game{PlainApp}.exe");
		processes.Start(Path.GetFileName(exe), exe);
		games.Tick();
		Later();
		games.Tick();
		Assert.Equal(0, processes.AllProcessesCalls);
		Assert.Null(games.ActiveGame);
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);
	}

	// A game found only by its process (in the background) gives way to a game in front at once, controller type included.
	[Fact]
	public void GameInFront_TakesOverABackgroundFoundGame_AndGetsItsTypeAtOnce()
	{
		FoundGame plain = Steam(PlainApp), ds = Steam(DualSenseApp);
		(GameModeController games, GameModeTests.FakeProcesses processes, SignalBridge bridge) = Make(plain, ds);
		string plainExe = Path.Combine(plain.Folder, $"game{PlainApp}.exe");
		processes.Start(Path.GetFileName(plainExe), plainExe);
		games.Tick();
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);
		Assert.Equal(plain.Key, games.ActiveGame!.Key);

		string dsExe = Path.Combine(ds.Folder, $"game{DualSenseApp}.exe");
		processes.Show(Path.GetFileName(dsExe), dsExe);
		games.Tick();
		Assert.Equal(ds.Key, games.ActiveGame!.Key);
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);
		Assert.Null(games.PendingPadType);
	}

	// With the virtual DualSense as Windows' default output any program's sound reaches the haptics channels: haptics sound
	// alone then teaches nothing; trigger effects still do.
	[Fact]
	public void Learning_IgnoresHapticsSound_WhileThePadIsTheDefaultOutput()
	{
		FoundGame plain = Steam(PlainApp);
		(GameModeController games, GameModeTests.FakeProcesses processes, SignalBridge bridge) = Make(plain);
		bool padIsDefault = true;
		int asked = 0;
		games.PadIsDefaultOutput = () =>
		{
			asked++;
			return padIsDefault;
		};
		games.SetPadType(plain.Key, VirtualPadType.DualSense);
		string exe = Path.Combine(plain.Folder, $"game{PlainApp}.exe");
		processes.Show(Path.GetFileName(exe), exe);
		games.Tick();
		for (int i = 0; i < NativeSupportDetector.NativeTicks * 2; i++)
		{
			bridge.Signals = new GameSignals(true, false, false);
			games.Tick();
		}
		Assert.Empty(games.NativeGames);
		Assert.Equal(1, asked);

		padIsDefault = false;
		now += 10_000;
		for (int i = 0; i < NativeSupportDetector.NativeTicks; i++)
		{
			bridge.Signals = new GameSignals(true, false, false);
			games.Tick();
		}
		Assert.Equal([plain.Key], games.NativeGames);
	}

	// Choosing Xbox 360 or DualShock 4 for a game forgets that it was learned native (back on Automatic it no longer gets a
	// DualSense); choosing DualSense keeps it.
	[Fact]
	public void ExplicitNonDualSenseChoice_ForgetsTheLearnedNative()
	{
		(GameModeController games, _, _) = Make(Steam(PlainApp), Steam(DualShockApp));
		games.LoadMemory(GameMemory.Default with { NativeGames = ["steam:" + PlainApp, "steam:" + DualShockApp] });
		int saves = 0;
		games.MemoryChanged += () => saves++;
		games.SetPadType("steam:" + PlainApp, VirtualPadType.Xbox360);
		games.SetPadType("steam:" + DualShockApp, VirtualPadType.DualSense);
		Assert.Equal(["steam:" + DualShockApp], games.NativeGames);
		Assert.Equal(2, saves);
		games.SetPadType("steam:" + PlainApp, null);
		games.SetPadType("steam:" + DualShockApp, null);
		Assert.Equal(VirtualPadType.Xbox360, games.PadTypeFor("steam:" + PlainApp));
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor("steam:" + DualShockApp));
	}

	internal sealed class SignalBridge : IGameBridge
	{
		public List<VirtualPadType> Pads { get; } = [];

		public GameSignals Signals;

		// The last profile applied.
		public BridgeProfile? Profile { get; private set; }

		public void ApplyProfile(BridgeProfile profile) => Profile = profile;

		public GameSignals TakeGameSignals()
		{
			GameSignals s = Signals;
			Signals = default;
			return s;
		}

		public void UsePad(VirtualPadType type) => Pads.Add(type);
	}
}
