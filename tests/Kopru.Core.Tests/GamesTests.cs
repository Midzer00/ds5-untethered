// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Protocol;

namespace Kopru.Core.Tests;

public class GamesTests
{
	private const string Fixture = """
		{
		  "version": 1,
		  "updated": "2026-11-09",
		  "games": [
		    { "name": "Returnal", "exe": ["Returnal-Win64-Shipping.exe"], "steamAppId": 1649240, "haptics": true,
		      "adaptiveTriggers": true, "exeConfidence": "high", "notes": "" },
		    { "name": "Assetto Corsa Competizione", "exe": ["AC2-Win64-Shipping.exe", "acc.exe"], "steamAppId": null,
		      "haptics": false, "adaptiveTriggers": true, "exeConfidence": "medium", "notes": "triggers only" }
		  ]
		}
		""";

	private static GameCatalog Catalog => GameCatalog.Parse(Fixture);

	[Fact]
	public void Catalog_Parses()
	{
		GameCatalog c = Catalog;
		Assert.Equal(2, c.Games.Count);
		Assert.Equal("1", c.Version);
		Assert.Equal("2026-11-09", c.Updated);
		CatalogGame acc = c.Games[0];
		Assert.Equal("Assetto Corsa Competizione", acc.Name);
		Assert.Null(acc.SteamAppId);
		Assert.False(acc.Haptics);
		Assert.True(acc.AdaptiveTriggers);
		Assert.Equal("medium", acc.ExeConfidence);
		Assert.Equal(1649240, c.Games[1].SteamAppId);
	}

	[Theory]
	[InlineData("Returnal-Win64-Shipping.exe", "Returnal")]
	[InlineData("RETURNAL-WIN64-SHIPPING.EXE", "Returnal")]
	[InlineData(@"C:\Games\Acc\ACC.exe", "Assetto Corsa Competizione")]
	[InlineData("ac2-win64-shipping.exe", "Assetto Corsa Competizione")]
	public void Catalog_MatchesExeCaseInsensitively(string exe, string name) => Assert.Equal(name, Catalog.Find(exe)?.Name);

	[Theory]
	[InlineData("notepad.exe")]
	[InlineData("")]
	[InlineData(null)]
	public void Catalog_UnknownExe_NoMatch(string? exe) => Assert.Null(Catalog.Find(exe));

	[Fact]
	public void Catalog_Embedded_LoadsOrIsEmpty()
	{
		GameCatalog c = GameCatalog.LoadEmbedded();
		Assert.All(c.Games, g => Assert.False(string.IsNullOrWhiteSpace(g.Name)));
		Assert.Throws<System.Text.Json.JsonException>(() => GameCatalog.Parse("{ nope"));
	}

	[Fact]
	public void Watcher_IgnoresShellAndReportsMatch()
	{
		string? exe = "explorer.exe";
		var watcher = new GameWatcher(Catalog, () => exe);
		Assert.Null(watcher.Poll());
		exe = null;
		Assert.Null(watcher.Poll());
		exe = "returnal-win64-shipping.exe";
		ForegroundGame? game = watcher.Poll();
		Assert.Equal("Returnal", game?.Name);
		exe = "Tetris.exe";
		Assert.Equal("Tetris", watcher.Poll()?.Name);
		Assert.Null(watcher.Poll()?.Match);
		exe = Path.GetFileName(Environment.ProcessPath);
		Assert.Null(watcher.Poll());
	}

	[Theory]
	[InlineData(GameOverride.Supported, false, false, GameMode.Supported, ModeReason.User)]
	[InlineData(GameOverride.Unsupported, true, true, GameMode.Unsupported, ModeReason.User)]
	[InlineData(GameOverride.Auto, true, false, GameMode.Supported, ModeReason.Catalog)]
	[InlineData(GameOverride.Auto, false, true, GameMode.Supported, ModeReason.Detected)]
	[InlineData(GameOverride.Auto, false, false, GameMode.Unsupported, ModeReason.Detected)]
	[InlineData(GameOverride.Auto, false, null, GameMode.Unsupported, ModeReason.Default)]
	public void Resolve_Order(GameOverride choice, bool inCatalog, bool? learned, GameMode mode, ModeReason reason)
	{
		CatalogGame? match = inCatalog ? Catalog.Games[0] : null;
		Assert.Equal((mode, reason), GameModeController.Resolve(choice, match, learned));
	}

	[Fact]
	public void Detector_LearnsNative_NeverDowngrades()
	{
		var d = new NativeSupportDetector();
		Assert.False(d.Observe("a.exe", default));
		Assert.Null(d.Get("a.exe"));
		Assert.True(d.Observe("a.exe", new GameSignals(false, false, true)));
		Assert.False(d.Get("a.exe"));
		Assert.True(d.Observe("a.exe", new GameSignals(false, true, false)));
		Assert.True(d.Get("A.EXE"));
		Assert.False(d.Observe("a.exe", new GameSignals(false, false, true)));
		Assert.True(d.Get("a.exe"));
		Assert.True(d.Observe("b.exe", new GameSignals(true, false, true)));
		Assert.True(d.Get("b.exe"));
	}

	[Fact]
	public void Controller_SwitchesProfiles_KeepsLastModeWithoutGame_AndRemembers()
	{
		var bridge = new FakeBridge();
		string? exe = "Indie.exe";
		var games = new GameModeController(Catalog, new GameWatcher(Catalog, () => exe), bridge);
		games.SetProfiles(new SupportedProfile(2.0, 0.5, true), new UnsupportedProfile(0.8, RumbleFeel.Punchy, FallbackTrigger.Weapon, 1));
		int memory = 0;
		games.MemoryChanged += () => memory++;

		games.Tick();
		Assert.Equal(new GameModeState("Indie.exe", "Indie", GameMode.Unsupported, ModeReason.Default), games.State);
		BridgeProfile p = bridge.Applied[^1];
		Assert.Equal(1, p.TriggerIntensity);
		Assert.False(p.LightFromGame);
		Assert.Equal(RumbleFeel.Punchy, p.Feel);
		Assert.Equal(TriggerEffects.Weapon(2, 6, 8), p.FallbackTrigger);
		Assert.Equal(2.0, p.GameHapticsLevel);

		// The game sends haptics audio: learned as native, switched to supported.
		bridge.Signals = new GameSignals(true, false, false);
		games.Tick();
		Assert.Equal((GameMode.Supported, ModeReason.Detected), (games.State.Mode, games.State.Reason));
		Assert.Equal(1, memory);
		p = bridge.Applied[^1];
		Assert.Equal(0.5, p.TriggerIntensity);
		Assert.True(p.LightFromGame);
		Assert.Null(p.FallbackTrigger);
		int applied = bridge.Applied.Count;

		// Desktop in front: the mode stays; nothing new is applied.
		exe = "explorer.exe";
		games.Tick();
		Assert.Equal(GameMode.Supported, games.State.Mode);
		Assert.Equal(applied, bridge.Applied.Count);

		// The user overrides; a catalog game resolves from the catalog.
		exe = "Indie.exe";
		games.SetOverride("indie.exe", GameOverride.Unsupported);
		Assert.Equal((GameMode.Unsupported, ModeReason.User), (games.State.Mode, games.State.Reason));
		Assert.Equal(2, memory);
		exe = "acc.exe";
		games.Tick();
		Assert.Equal(new GameModeState("acc.exe", "Assetto Corsa Competizione", GameMode.Supported, ModeReason.Catalog, Catalog.Find("acc.exe")).Mode, games.State.Mode);
		Assert.Equal(ModeReason.Catalog, games.State.Reason);

		Assert.True(games.SnapshotLearned()["indie.exe"]);
		Assert.Equal(GameOverride.Unsupported, games.SnapshotOverrides()["Indie.exe"]);
		Assert.Contains(games.KnownGames(), g => g.IsLearned && g.Exe == "Indie.exe");
		Assert.Equal(3, games.KnownGames().Count);

		// Saved memory loads into a new controller.
		var again = new GameModeController(Catalog, new GameWatcher(Catalog, () => "Indie.exe"), new FakeBridge());
		again.LoadMemory(games.SnapshotOverrides(), games.SnapshotLearned());
		again.Tick();
		Assert.Equal(ModeReason.User, again.State.Reason);
		again.SetOverride("Indie.exe", GameOverride.Auto);
		Assert.Equal((GameMode.Supported, ModeReason.Detected), (again.State.Mode, again.State.Reason));
	}

	[Fact]
	public void Profiles_ChangeAppliesAtOnce()
	{
		var bridge = new FakeBridge();
		var games = new GameModeController(Catalog, new GameWatcher(Catalog, () => "Returnal-Win64-Shipping.exe"), bridge);
		games.Tick();
		games.SetProfiles(new SupportedProfile(1.5, 0, true), new UnsupportedProfile());
		Assert.Equal(0, bridge.Applied[^1].TriggerIntensity);
		int count = bridge.Applied.Count;
		games.SetProfiles(new SupportedProfile(1.5, 0, true), new UnsupportedProfile());
		Assert.Equal(count, bridge.Applied.Count);
	}

	[Fact]
	public void Scale_Feedback_ScalesEveryZone()
	{
		byte[] full = TriggerEffects.Feedback(3, 8);
		Assert.Equal(full, TriggerEffects.Scale(full, 1));
		Assert.Equal(TriggerEffects.Feedback(3, 4), TriggerEffects.Scale(full, 0.5));
		Assert.Equal(TriggerEffects.Feedback(3, 1), TriggerEffects.Scale(full, 0.1));
		Assert.Equal(TriggerEffects.Off(), TriggerEffects.Scale(full, 0));
		Assert.Equal(TriggerEffects.Off(), TriggerEffects.Scale(TriggerEffects.Feedback(0, 1), 0.2));
	}

	[Fact]
	public void Scale_WeaponAndVibration()
	{
		Assert.Equal(TriggerEffects.Weapon(2, 6, 4), TriggerEffects.Scale(TriggerEffects.Weapon(2, 6, 8), 0.5));
		Assert.Equal(TriggerEffects.Off(), TriggerEffects.Scale(TriggerEffects.Weapon(2, 6, 1), 0.3));
		byte[] v = TriggerEffects.Scale(TriggerEffects.Vibration(2, 8, 30), 0.25);
		Assert.Equal(TriggerEffects.Vibration(2, 2, 30), v);
		Assert.Equal(30, v[9]);
	}

	[Fact]
	public void Scale_UnknownModePassesThrough()
	{
		byte[] legacy = [0x01, 0x40, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0];
		Assert.Equal(legacy, TriggerEffects.Scale(legacy, 0.5));
		Assert.Equal(TriggerEffects.Off(), TriggerEffects.Scale(legacy, 0));
		Assert.True(TriggerEffects.IsOff(TriggerEffects.Off()));
		Assert.False(TriggerEffects.IsOff(legacy));
	}

	[Fact]
	public void FallbackEffect_MapsStrength()
	{
		Assert.Null(new UnsupportedProfile().TriggerEffect);
		Assert.Equal(TriggerEffects.Feedback(2, 4), new UnsupportedProfile(Trigger: FallbackTrigger.Feedback, FallbackStrength: 0.5).TriggerEffect);
		Assert.Equal(TriggerEffects.Vibration(2, 8, 30), GameProfiles.Effect(FallbackTrigger.Vibration, 1));
		Assert.Equal(TriggerEffects.Weapon(2, 6, 1), GameProfiles.Effect(FallbackTrigger.Weapon, 0));
	}

	[Fact]
	public void RumbleFeel_SoftIsSine_PunchyIsHigherAndKicks()
	{
		var soft = new RumbleSynth();
		var punchy = new RumbleSynth();
		Assert.Equal(((sbyte)0, (sbyte)0), soft.Next(0, 0, 1, RumbleFeel.Soft));
		int softCrossings = 0, punchyCrossings = 0;
		sbyte lastSoft = 0, lastPunchy = 0;
		int punchyEarly = 0, punchyLate = 0;
		for (int i = 0; i < 3000; i++)
		{
			(sbyte s, _) = soft.Next(255, 255, 1, RumbleFeel.Soft);
			(sbyte p, _) = punchy.Next(255, 255, 1, RumbleFeel.Punchy);
			if (lastSoft < 0 && s >= 0) softCrossings++;
			if (lastPunchy < 0 && p >= 0) punchyCrossings++;
			lastSoft = s;
			lastPunchy = p;
			if (i < RumbleSynth.KickFrames) punchyEarly = Math.Max(punchyEarly, Math.Abs((int)p));
			else punchyLate = Math.Max(punchyLate, Math.Abs((int)p));
		}
		Assert.InRange(softCrossings, 78, 81);
		Assert.InRange(punchyCrossings, 123, 126);
		Assert.Equal(127, punchyEarly);
		Assert.InRange(punchyLate, 85, 92);
	}

	[Fact]
	public void BridgeProfile_SameAs_ComparesBytes()
	{
		var a = new BridgeProfile(FallbackTrigger: TriggerEffects.Weapon(2, 6, 3));
		var b = new BridgeProfile(FallbackTrigger: TriggerEffects.Weapon(2, 6, 3));
		Assert.True(a.SameAs(b));
		Assert.False(a.SameAs(b with { FallbackTrigger = null }));
		Assert.False(a.SameAs(b with { Feel = RumbleFeel.Punchy }));
	}

	private sealed class FakeBridge : IGameBridge
	{
		public List<BridgeProfile> Applied { get; } = [];

		public GameSignals Signals;

		public void ApplyProfile(BridgeProfile profile) => Applied.Add(profile);

		public GameSignals TakeGameSignals()
		{
			GameSignals s = Signals;
			Signals = default;
			return s;
		}
	}
}
