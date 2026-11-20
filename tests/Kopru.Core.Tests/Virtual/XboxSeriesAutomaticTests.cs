// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Virtual;
using static Kopru.Core.Tests.AutoPadTypeTests;

namespace Kopru.Core.Tests;

// Automatic with the Xbox Series pad: the game's own choice, native DualSense (list, learned, Steam's DualSense flag), Xbox
// trigger rumble games (the shipped list and learned ones; only with the experimental Xbox Trigger Rumble setting on and
// the pad usable here), DualShock 4 (Steam's flag), the default type, then Xbox 360; the trigger rumble list and its
// learning. The setting is off by default: Automatic then never gives Xbox Series.
[Trait("Category", "Windows")]
public class XboxSeriesAutomaticTests : IDisposable
{
	private const long DualSenseApp = 9201;
	private const long DualShockApp = 9202;
	private const long PlainApp = 9203;
	private const long TriggerApp = 9204;
	private const long BothApp = 9205;
	private const long TriggerDs4App = 9206;

	private static readonly GameCatalog Catalog = new([new CatalogGame("Listed Racer", ["racer.exe"], 9200, true, true)]);

	private static readonly PlayStationSupport Shipped = new([
		new(DualSenseApp, PlayStationPad.DualSense),
		new(DualShockApp, PlayStationPad.DualShock4),
		new(BothApp, PlayStationPad.DualSense),
		new(TriggerDs4App, PlayStationPad.DualShock4)
	], "2026-11-18");

	// The fixture list (the shipped file is another one).
	private static readonly XboxTriggerGames Triggers = XboxTriggerGames.Parse("""
		{ "generated": "2026-11-18", "games": [
		  { "name": "Rumble Rally", "steamAppId": 9204, "exe": [], "source": "x" },
		  { "name": "Both Ways Racer", "steamAppId": 9205, "exe": [] },
		  { "name": "Pad Four Shooter", "steamAppId": 9206, "exe": [] },
		  { "name": "Listed Racer", "steamAppId": 9200, "exe": ["racer.exe"] },
		  { "name": "Store Only Motorsport", "steamAppId": null, "exe": [] },
		  { "name": "Exe Named Game", "steamAppId": null, "exe": ["exenamed.exe"] }
		] }
		""");

	private readonly string root = Path.Combine(Path.GetTempPath(), "kopru-xboxseries-" + Guid.NewGuid().ToString("N"));

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

	private long now = 1_000_000;

	private FoundGame Steam(long appId, string? name = null)
	{
		string folder = Path.Combine(root, "common", "Game" + appId);
		Directory.CreateDirectory(folder);
		File.WriteAllBytes(Path.Combine(folder, $"game{appId}.exe"), []);
		return new FoundGame(name ?? "Game " + appId, InstalledSource.Steam, folder, appId.ToString(System.Globalization.CultureInfo.InvariantCulture),
			SteamType: "Game");
	}

	// The setting on (the 0.2 behaviour these tests cover); MakeOff leaves it at its default.
	private (GameModeController Games, GameModeTests.FakeProcesses Processes, SignalBridge Bridge) Make(params FoundGame[] found)
	{
		(GameModeController games, GameModeTests.FakeProcesses processes, SignalBridge bridge) = MakeOff(found);
		games.XboxTriggerRumbleOn = true;
		return (games, processes, bridge);
	}

	private (GameModeController Games, GameModeTests.FakeProcesses Processes, SignalBridge Bridge) MakeOff(params FoundGame[] found)
	{
		var processes = new GameModeTests.FakeProcesses();
		var bridge = new SignalBridge();
		var watcher = new GameWatcher(Catalog, processes, log: _ => { });
		var games = new GameModeController(Catalog, watcher, bridge)
		{
			Log = _ => { },
			PlayStation = Shipped,
			TriggerGames = Triggers,
			ClockMs = () => now,
			XboxSeriesOsSupported = true
		};
		games.FoundGames = found;
		return (games, processes, bridge);
	}

	// ---- the list ----

	[Fact]
	public void List_LoadsTheSchema_AndFindsBySteamIdNameAndExe()
	{
		Assert.Equal(6, Triggers.Count);
		Assert.Equal("2026-11-18", Triggers.Generated);
		Assert.Equal("Rumble Rally", Triggers.FindBySteamAppId(TriggerApp)!.Name);
		Assert.Equal("x", Triggers.FindBySteamAppId(TriggerApp)!.Source);
		Assert.Equal("Store Only Motorsport", Triggers.FindByName("STORE ONLY: MOTORSPORT")!.Name);
		Assert.Equal("Exe Named Game", Triggers.FindByExe(@"C:\Games\ExeNamed.EXE")!.Name);
		Assert.Null(Triggers.FindBySteamAppId(PlainApp));
		Assert.Null(Triggers.FindByName("Store Only"));
		// Keys: the Steam app's key where the entry names one, else the name.
		Assert.Equal("steam:9204", Triggers.FindBySteamAppId(TriggerApp)!.Key);
		Assert.Equal("Store Only Motorsport", Triggers.FindByKey("Store Only Motorsport")!.Key);
	}

	[Fact]
	public void List_IsTolerant_ToABareArray_StringIds_AndMissingFields()
	{
		XboxTriggerGames list = XboxTriggerGames.Parse("""
			[ { "Name": "Alpha Game", "SteamAppId": "77", "Exe": "alpha.exe" }, { "steamAppId": 5 }, { "name": "  " }, { "name": "Alpha Game" } ]
			""");
		XboxTriggerGame alpha = Assert.Single(list.Games);
		Assert.Equal((77L, "alpha.exe"), (alpha.SteamAppId!.Value, alpha.Exe.Single()));
		Assert.Throws<System.Text.Json.JsonException>(() => XboxTriggerGames.Parse("{ \"nothing\": 1 }"));
	}

	[Fact]
	public void ShippedList_IsEmbedded_AndReadable()
	{
		XboxTriggerGames shipped = XboxTriggerGames.LoadEmbedded();
		Assert.True(shipped.Count > 0);
		Assert.All(shipped.Games, g => Assert.False(string.IsNullOrWhiteSpace(g.Name)));
	}

	// ---- Automatic order ----

	[Fact]
	public void Automatic_TheWholeOrder()
	{
		(GameModeController games, _, _) = Make(Steam(DualSenseApp), Steam(DualShockApp), Steam(PlainApp), Steam(TriggerApp), Steam(BothApp),
			Steam(TriggerDs4App));
		// Native DualSense: the list and Steam's DualSense flag.
		Assert.Equal(VirtualPadType.DualSense, games.AutomaticPadTypeFor("Listed Racer"));
		Assert.Equal(VirtualPadType.DualSense, games.AutomaticPadTypeFor("steam:" + DualSenseApp));
		// Xbox trigger rumble games; a game that also supports the DualSense gets the DualSense.
		Assert.Equal(VirtualPadType.XboxSeries, games.AutomaticPadTypeFor("steam:" + TriggerApp));
		Assert.Equal(VirtualPadType.DualSense, games.AutomaticPadTypeFor("steam:" + BothApp));
		// Trigger rumble comes before DualShock 4.
		Assert.Equal(VirtualPadType.XboxSeries, games.AutomaticPadTypeFor("steam:" + TriggerDs4App));
		Assert.Equal(VirtualPadType.DualShock4, games.AutomaticPadTypeFor("steam:" + DualShockApp));
		// Nothing known: Xbox 360 (not the experimental pad), or the default type when one is set.
		Assert.Equal(VirtualPadType.Xbox360, games.AutomaticPadTypeFor("steam:" + PlainApp));
		games.SetForcedPadType(VirtualPadType.DualShock4);
		Assert.Equal(VirtualPadType.DualShock4, games.AutomaticPadTypeFor("steam:" + PlainApp));
		Assert.Equal(VirtualPadType.XboxSeries, games.AutomaticPadTypeFor("steam:" + TriggerApp));
		// The game's own choice wins over everything.
		games.SetPadType("steam:" + TriggerApp, VirtualPadType.DualSense);
		Assert.Equal(VirtualPadType.DualSense, games.PadTypeFor("steam:" + TriggerApp));
		Assert.True(games.UsesXboxTriggers("steam:" + BothApp));
		Assert.True(games.SupportsDualSense("steam:" + BothApp));
		Assert.False(games.SupportsDualSense("steam:" + TriggerApp));
	}

	[Fact]
	public void OldWindows_AutomaticGivesXbox360_AndAnXboxSeriesChoiceIsPlayedAsXbox360()
	{
		(GameModeController games, _, SignalBridge bridge) = Make(Steam(PlainApp), Steam(TriggerApp), Steam(DualShockApp));
		List<XboxSeriesFallback> fellBack = [];
		games.XboxSeriesFellBack += fellBack.Add;
		games.XboxSeriesOsSupported = false;
		Assert.Equal(VirtualPadType.Xbox360, games.AutomaticPadTypeFor("steam:" + PlainApp));
		// No trigger rule without the pad: the next rules decide.
		Assert.Equal(VirtualPadType.Xbox360, games.AutomaticPadTypeFor("steam:" + TriggerApp));
		Assert.Equal(VirtualPadType.DualShock4, games.AutomaticPadTypeFor("steam:" + DualShockApp));
		Assert.Empty(fellBack);
		games.SetForcedPadType(VirtualPadType.XboxSeries);
		Assert.Equal(VirtualPadType.Xbox360, games.CurrentPadType);
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);
		XboxSeriesFallback once = Assert.Single(fellBack);
		Assert.False(once.Remembered);
		games.SetForcedPadType(null);
		games.SetForcedPadType(VirtualPadType.XboxSeries);
		Assert.Single(fellBack);
	}

	private static readonly DateTimeOffset Today = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

	// The next run of the app on this PC: a new controller with the saved memory, at the given time and version.
	private (GameModeController Games, SignalBridge Bridge) NextRun(GameMemory saved, DateTimeOffset at, string version = "0.2.0")
	{
		(GameModeController next, _, SignalBridge bridge) = Make(Steam(PlainApp), Steam(TriggerApp));
		next.UtcNow = () => at;
		next.AppVersion = version;
		next.LoadMemory(saved);
		return (next, bridge);
	}

	[Fact]
	public void StartFailure_SwapsToXbox360_ButOneFailureIsNotRemembered()
	{
		(GameModeController games, _, SignalBridge bridge) = Make(Steam(PlainApp), Steam(TriggerApp));
		games.UtcNow = () => Today;
		games.AppVersion = "0.2.0";
		games.SetForcedPadType(VirtualPadType.XboxSeries);
		Assert.Equal(VirtualPadType.XboxSeries, bridge.Pads[^1]);
		List<XboxSeriesFallback> fellBack = [];
		List<PadSwap> swaps = [];
		games.XboxSeriesFellBack += fellBack.Add;
		games.PadSwapped += swaps.Add;
		int saves = 0;
		games.MemoryChanged += () => saves++;

		// A slow first driver install, a pad Windows still held after a crash, a sleep: one failure.
		games.ReportXboxSeriesFailed("no START");
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);
		Assert.False(Assert.Single(fellBack).Remembered);
		Assert.True(Assert.Single(swaps).Fallback);
		Assert.True(saves > 0);
		// This run stays on Xbox 360.
		Assert.False(games.XboxSeriesUsableForAutomatic);
		Assert.Equal(VirtualPadType.Xbox360, games.CurrentPadType);
		GameMemory saved = games.SnapshotMemory();
		Assert.False(saved.XboxSeriesFailed);
		Assert.Equal(1, saved.XboxSeriesFailures);
		// A second report in the same run changes nothing (one failure per run).
		games.ReportXboxSeriesFailed("again");
		Assert.Single(fellBack);
		Assert.Equal(1, games.SnapshotMemory().XboxSeriesFailures);

		// The next run tries Xbox Series again.
		(GameModeController next, _) = NextRun(saved, Today.AddMinutes(5));
		Assert.True(next.XboxSeriesUsableForAutomatic);
		Assert.Equal(VirtualPadType.XboxSeries, next.AutomaticPadTypeFor("steam:" + TriggerApp));
		// It starts: the failure is forgotten.
		next.ReportXboxSeriesStarted();
		Assert.Equal(0, next.SnapshotMemory().XboxSeriesFailures);
	}

	[Fact]
	public void TwoFailuresInARow_AreRemembered_AutomaticPicksXbox360_UntilTheRetry()
	{
		(GameModeController games, _, _) = Make(Steam(PlainApp), Steam(TriggerApp));
		games.UtcNow = () => Today;
		games.AppVersion = "0.2.0";
		games.ReportXboxSeriesFailed("no START");
		(GameModeController second, _) = NextRun(games.SnapshotMemory(), Today.AddHours(1));
		List<XboxSeriesFallback> fellBack = [];
		second.XboxSeriesFellBack += fellBack.Add;
		second.ReportXboxSeriesFailed("no START again");
		Assert.True(Assert.Single(fellBack).Remembered);
		GameMemory saved = second.SnapshotMemory();
		Assert.True(saved.XboxSeriesFailed);
		Assert.Equal(2, saved.XboxSeriesFailures);
		Assert.Equal(Today.AddHours(1), saved.XboxSeriesFailedAt);
		Assert.Equal("0.2.0", saved.XboxSeriesFailedVersion);

		// Next start: Automatic gives Xbox 360; an explicit Xbox Series choice still tries the pad.
		(GameModeController third, _) = NextRun(saved, Today.AddDays(1));
		Assert.False(third.XboxSeriesUsableForAutomatic);
		Assert.True(third.XboxSeriesFailedBefore);
		Assert.Equal(VirtualPadType.Xbox360, third.AutomaticPadTypeFor("steam:" + PlainApp));
		Assert.Equal(VirtualPadType.Xbox360, third.AutomaticPadTypeFor("steam:" + TriggerApp));
		third.SetPadType("steam:" + PlainApp, VirtualPadType.XboxSeries);
		Assert.Equal(VirtualPadType.XboxSeries, third.PadTypeFor("steam:" + PlainApp));

		// A week later Automatic tries Xbox Series once more.
		(GameModeController weekLater, _) = NextRun(saved, Today.AddHours(1) + GameModeController.XboxSeriesRetryAfter);
		Assert.True(weekLater.XboxSeriesUsableForAutomatic);
		Assert.Equal(VirtualPadType.XboxSeries, weekLater.AutomaticPadTypeFor("steam:" + TriggerApp));
		// A failed retry is remembered again at once, for another week.
		weekLater.ReportXboxSeriesFailed("still no START");
		GameMemory again = weekLater.SnapshotMemory();
		Assert.True(again.XboxSeriesFailed);
		Assert.Equal(Today.AddHours(1) + GameModeController.XboxSeriesRetryAfter, again.XboxSeriesFailedAt);
		Assert.False(NextRun(again, again.XboxSeriesFailedAt!.Value.AddDays(1)).Games.XboxSeriesUsableForAutomatic);

		// Another app version tries again too.
		(GameModeController update, _) = NextRun(saved, Today.AddDays(1), "0.2.1");
		Assert.True(update.XboxSeriesUsableForAutomatic);
		// The pad started: everything is forgotten.
		update.ReportXboxSeriesStarted();
		GameMemory cleared = update.SnapshotMemory();
		Assert.False(cleared.XboxSeriesFailed);
		Assert.Equal(0, cleared.XboxSeriesFailures);
		Assert.Null(cleared.XboxSeriesFailedAt);
		Assert.Null(cleared.XboxSeriesFailedVersion);
		Assert.Equal(VirtualPadType.XboxSeries, update.AutomaticPadTypeFor("steam:" + TriggerApp));
	}

	[Fact]
	public void FailureRememberedWithoutADate_IsTriedAgain()
	{
		(GameModeController next, _) = NextRun(GameMemory.Default with { XboxSeriesFailed = true }, Today);
		Assert.True(next.XboxSeriesUsableForAutomatic);
		// Its retry failing is remembered at once.
		next.ReportXboxSeriesFailed("no START");
		Assert.True(next.SnapshotMemory().XboxSeriesFailed);
		Assert.Equal(Today, next.SnapshotMemory().XboxSeriesFailedAt);
	}

	// ---- recognition ----

	[Fact]
	public void ListGame_IsRecognisedByNameOrExe_WhenNoLauncherFoundIt()
	{
		(GameModeController games, GameModeTests.FakeProcesses processes, SignalBridge bridge) = Make();
		processes.Show("ForzaThing.exe", @"C:\XboxGames\Store Only Motorsport\Content\ForzaThing.exe");
		games.Tick();
		Assert.Equal("Store Only Motorsport", games.ActiveGame!.Key);
		Assert.Equal(VirtualPadType.XboxSeries, bridge.Pads[^1]);
		processes.Exit("ForzaThing.exe");
		games.Tick();
		processes.Show("exenamed.exe", @"D:\Somewhere\exenamed.exe");
		games.Tick();
		Assert.Equal("Exe Named Game", games.ActiveGame!.Name);
		Assert.Equal(VirtualPadType.XboxSeries, bridge.Pads[^1]);
	}

	// ---- learning ----

	[Fact]
	public void Learning_NeedsTheGameInFront_OnTheXboxSeriesPad_ForAFewSeconds()
	{
		FoundGame plain = Steam(PlainApp);
		(GameModeController games, GameModeTests.FakeProcesses processes, SignalBridge bridge) = Make(plain);
		games.SetForcedPadType(VirtualPadType.DualShock4);
		string exe = Path.Combine(plain.Folder, $"game{PlainApp}.exe");
		processes.Show(Path.GetFileName(exe), exe);
		games.Tick();
		Assert.Equal(VirtualPadType.DualShock4, bridge.Pads[^1]);
		// Not on the Xbox Series pad: nothing counts.
		for (int i = 0; i < XboxTriggerDetector.Ticks * 2; i++)
		{
			bridge.Signals = new GameSignals(false, false, false, true);
			games.Tick();
		}
		Assert.Empty(games.LearnedTriggerGames);

		games.SetPadType(plain.Key, VirtualPadType.XboxSeries);
		games.ApplyPadTypeNow();
		Assert.Equal(VirtualPadType.XboxSeries, bridge.Pads[^1]);
		int saves = 0;
		games.MemoryChanged += () => saves++;
		for (int i = 1; i < XboxTriggerDetector.Ticks; i++)
		{
			bridge.Signals = new GameSignals(false, false, true, true);
			games.Tick();
		}
		Assert.Empty(games.LearnedTriggerGames);
		bridge.Signals = new GameSignals(false, false, false, true);
		games.Tick();
		Assert.Equal([plain.Key], games.LearnedTriggerGames);
		Assert.Equal(1, saves);
		Assert.Equal([plain.Key], games.SnapshotMemory().XboxTriggerGames!);
		// Trigger rumble never counts as native DualSense use.
		Assert.Empty(games.NativeGames);

		// On Automatic it keeps Xbox Series before the default type.
		games.SetPadType(plain.Key, null);
		Assert.Equal(VirtualPadType.XboxSeries, games.AutomaticPadTypeFor(plain.Key));
		// An explicit other choice unlearns it.
		games.SetPadType(plain.Key, VirtualPadType.Xbox360);
		Assert.Empty(games.LearnedTriggerGames);
		games.SetPadType(plain.Key, null);
		Assert.Equal(VirtualPadType.DualShock4, games.AutomaticPadTypeFor(plain.Key));
	}

	[Fact]
	public void Learning_GameInTheBackground_OrAnotherSession_DoesNotAddUp()
	{
		FoundGame plain = Steam(PlainApp);
		(GameModeController games, GameModeTests.FakeProcesses processes, SignalBridge bridge) = Make(plain);
		games.SetPadType(plain.Key, VirtualPadType.XboxSeries);
		string exe = Path.Combine(plain.Folder, $"game{PlainApp}.exe");
		processes.Show(Path.GetFileName(exe), exe);
		games.Tick();
		for (int i = 1; i < XboxTriggerDetector.Ticks; i++)
		{
			bridge.Signals = new GameSignals(false, false, false, true);
			games.Tick();
		}
		// Another app in front: the signals of that time don't count.
		processes.Show("notepad.exe", @"C:\Windows\notepad.exe");
		bridge.Signals = new GameSignals(false, false, false, true);
		games.Tick();
		Assert.Empty(games.LearnedTriggerGames);
		// The game exits and starts again: a new session starts counting from zero.
		processes.Exit(Path.GetFileName(exe));
		processes.Exit("notepad.exe");
		games.Tick();
		processes.Show(Path.GetFileName(exe), exe);
		games.Tick();
		bridge.Signals = new GameSignals(false, false, false, true);
		games.Tick();
		Assert.Empty(games.LearnedTriggerGames);
	}

	// ---- the experimental setting ----

	[Fact]
	public void Setting_IsOffByDefault_AndSavedWithTheGames()
	{
		(GameModeController games, _, _) = MakeOff();
		Assert.False(games.XboxTriggerRumbleOn);
		Assert.False(GameMemory.Default.XboxTriggerRumbleOn);
		Assert.False(games.SnapshotMemory().XboxTriggerRumbleOn);
		int saves = 0;
		games.MemoryChanged += () => saves++;
		games.XboxTriggerRumbleOn = true;
		games.XboxTriggerRumbleOn = true;
		Assert.Equal(1, saves);
		Assert.True(games.SnapshotMemory().XboxTriggerRumbleOn);
		(GameModeController next, _) = NextRun(games.SnapshotMemory(), Today);
		Assert.True(next.XboxTriggerRumbleOn);
		next.LoadMemory(GameMemory.Default);
		Assert.False(next.XboxTriggerRumbleOn);
	}

	[Fact]
	public void Off_AutomaticNeverGivesXboxSeries_ListedOrLearned()
	{
		(GameModeController games, _, SignalBridge bridge) = MakeOff(Steam(DualSenseApp), Steam(DualShockApp), Steam(PlainApp), Steam(TriggerApp),
			Steam(BothApp), Steam(TriggerDs4App));
		games.LoadMemory(GameMemory.Default with { XboxTriggerGames = ["steam:" + PlainApp] });
		Assert.True(games.XboxSeriesUsableForAutomatic);
		// What these games got before 0.2: DualSense, DualShock 4, else Xbox 360.
		Assert.Equal(VirtualPadType.Xbox360, games.AutomaticPadTypeFor("steam:" + TriggerApp));
		Assert.Equal(VirtualPadType.Xbox360, games.AutomaticPadTypeFor("steam:" + PlainApp));
		Assert.Equal(VirtualPadType.DualShock4, games.AutomaticPadTypeFor("steam:" + TriggerDs4App));
		Assert.Equal(VirtualPadType.DualSense, games.AutomaticPadTypeFor("steam:" + BothApp));
		Assert.Equal(VirtualPadType.DualSense, games.AutomaticPadTypeFor("Listed Racer"));
		Assert.Equal(VirtualPadType.Xbox360, games.AutomaticPadTypeFor("Store Only Motorsport"));
		// Still known as trigger rumble games (the Games page lists them).
		Assert.True(games.UsesXboxTriggers("steam:" + TriggerApp));
		Assert.True(games.UsesXboxTriggers("steam:" + PlainApp));
		// The default type decides the games we know nothing about, trigger rumble games included.
		games.SetForcedPadType(VirtualPadType.DualShock4);
		Assert.Equal(VirtualPadType.DualShock4, games.AutomaticPadTypeFor("steam:" + TriggerApp));
		games.SetForcedPadType(null);
		Assert.DoesNotContain(VirtualPadType.XboxSeries, bridge.Pads);
	}

	[Fact]
	public void Off_AnXboxSeriesChoiceIsHonoured()
	{
		(GameModeController games, _, SignalBridge bridge) = MakeOff(Steam(PlainApp), Steam(TriggerApp));
		games.SetPadType("steam:" + TriggerApp, VirtualPadType.XboxSeries);
		Assert.Equal(VirtualPadType.XboxSeries, games.PadTypeFor("steam:" + TriggerApp));
		games.SetForcedPadType(VirtualPadType.XboxSeries);
		Assert.Equal(VirtualPadType.XboxSeries, games.AutomaticPadTypeFor("steam:" + PlainApp));
		Assert.Equal(VirtualPadType.XboxSeries, bridge.Pads[^1]);
	}

	// Xbox Series is an ordinary type: chosen by hand it is put in with the setting off, but the bridge's profile then
	// leaves the game's trigger motor levels unplayed; on, they play with the profile's Trigger Rumble choice.
	[Fact]
	public void Setting_DecidesWhetherTheBridgePlaysTriggerRumble_NotTheXboxSeriesChoice()
	{
		(GameModeController games, _, SignalBridge bridge) = MakeOff(Steam(TriggerApp));
		games.SetProfile(new GameProfile { TriggerRumble = TriggerRumbleFeel.Sharp });
		games.SetForcedPadType(VirtualPadType.XboxSeries);
		Assert.Equal(VirtualPadType.XboxSeries, bridge.Pads[^1]);
		Assert.False(bridge.Profile!.XboxTriggerRumbleOn);
		Assert.Equal(TriggerRumbleFeel.Sharp, bridge.Profile.TriggerRumble);
		Assert.Equal(TriggerRumbleFeel.Off, bridge.Profile.PlayedTriggerRumble);

		games.XboxTriggerRumbleOn = true;
		Assert.True(bridge.Profile!.XboxTriggerRumbleOn);
		Assert.Equal(TriggerRumbleFeel.Sharp, bridge.Profile.PlayedTriggerRumble);
		Assert.Equal(VirtualPadType.XboxSeries, games.CurrentPadType);

		games.XboxTriggerRumbleOn = false;
		Assert.Equal(TriggerRumbleFeel.Off, bridge.Profile!.PlayedTriggerRumble);
		Assert.Equal(VirtualPadType.XboxSeries, games.CurrentPadType);

		// Loaded on from the saved games: the profile follows.
		games.LoadMemory(games.SnapshotMemory() with { XboxTriggerRumbleOn = true });
		Assert.Equal(TriggerRumbleFeel.Sharp, bridge.Profile!.PlayedTriggerRumble);
	}

	[Fact]
	public void Off_ListGamesAreNotRecognisedByTheListAlone()
	{
		(GameModeController games, GameModeTests.FakeProcesses processes, SignalBridge bridge) = MakeOff();
		processes.Show("exenamed.exe", @"D:\Somewhere\exenamed.exe");
		games.Tick();
		Assert.Null(games.ActiveGame);
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);
		// Turned on: recognised, and it gets Xbox Series.
		games.XboxTriggerRumbleOn = true;
		games.Tick();
		Assert.Equal("Exe Named Game", games.ActiveGame!.Name);
		Assert.Equal(VirtualPadType.XboxSeries, bridge.Pads[^1]);
	}

	[Fact]
	public void Off_LearningOnlyOnTheXboxSeriesPad()
	{
		FoundGame plain = Steam(PlainApp);
		(GameModeController games, GameModeTests.FakeProcesses processes, SignalBridge bridge) = MakeOff(plain);
		string exe = Path.Combine(plain.Folder, $"game{PlainApp}.exe");
		processes.Show(Path.GetFileName(exe), exe);
		games.Tick();
		Assert.Equal(VirtualPadType.Xbox360, bridge.Pads[^1]);
		for (int i = 0; i < XboxTriggerDetector.Ticks * 2; i++)
		{
			bridge.Signals = new GameSignals(false, false, false, true);
			games.Tick();
		}
		Assert.Empty(games.LearnedTriggerGames);
		// The user's own Xbox Series choice: the pad is Xbox Series, so it learns.
		games.SetPadType(plain.Key, VirtualPadType.XboxSeries);
		games.ApplyPadTypeNow();
		for (int i = 0; i < XboxTriggerDetector.Ticks; i++)
		{
			bridge.Signals = new GameSignals(false, false, false, true);
			games.Tick();
		}
		Assert.Equal([plain.Key], games.LearnedTriggerGames);
		// Learned, but Automatic still gives Xbox 360 while the setting is off.
		games.SetPadType(plain.Key, null);
		Assert.Equal(VirtualPadType.Xbox360, games.AutomaticPadTypeFor(plain.Key));
	}

	[Fact]
	public void On_ListedAndLearnedGamesGetXboxSeries_UnknownGamesXbox360()
	{
		(GameModeController games, _, SignalBridge bridge) = MakeOff(Steam(PlainApp), Steam(TriggerApp), Steam(9207));
		games.LoadMemory(GameMemory.Default with { XboxTriggerGames = ["steam:9207"] });
		games.XboxTriggerRumbleOn = true;
		Assert.Equal(VirtualPadType.XboxSeries, games.AutomaticPadTypeFor("steam:" + TriggerApp));
		Assert.Equal(VirtualPadType.XboxSeries, games.AutomaticPadTypeFor("steam:9207"));
		Assert.Equal(VirtualPadType.XboxSeries, games.AutomaticPadTypeFor("Store Only Motorsport"));
		Assert.Equal(VirtualPadType.Xbox360, games.AutomaticPadTypeFor("steam:" + PlainApp));
		Assert.Equal(VirtualPadType.Xbox360, games.AutomaticPadTypeFor("unknowngame.exe"));
		// Off again: back to Xbox 360.
		games.XboxTriggerRumbleOn = false;
		Assert.Equal(VirtualPadType.Xbox360, games.AutomaticPadTypeFor("steam:" + TriggerApp));
		Assert.DoesNotContain(VirtualPadType.XboxSeries, bridge.Pads);
	}

	[Fact]
	public void Detector_CountsOnlyOneSessionOfOneGame()
	{
		XboxTriggerDetector d = new();
		for (int i = 1; i < XboxTriggerDetector.Ticks; i++)
		{
			Assert.False(d.Observe("a", true, 1));
		}
		Assert.False(d.Observe("b", true, 1));
		Assert.False(d.Observe("a", true, 1));
		Assert.False(d.Observe("a", false, 1));
		for (int i = 1; i < XboxTriggerDetector.Ticks; i++)
		{
			d.Observe("a", true, 2);
		}
		Assert.True(d.Observe("a", true, 2));
		Assert.True(d.IsLearned("A"));
		Assert.True(d.Forget("a"));
		Assert.False(d.IsLearned("a"));
	}
}
