// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;
using Kopru.Core.Input;
using Kopru.Core.Protocol;

namespace Kopru.Core.Tests;

// "PS: Open The App": a single short PS tap with no game in front opens the app instead of reaching the game.
public class PsOpenAppTests
{
	private const int Hat = ShortcutProcessor.Buttons0, Ps = ShortcutProcessor.Buttons2;

	private readonly StepClock clock = new();
	private readonly ShortcutProcessor shortcuts;
	private readonly List<ShortcutAction> fired = [];
	private readonly List<string> log = [];
	private PsTapRoute route = PsTapRoute.OpenApp;
	private int asked;

	public PsOpenAppTests()
	{
		shortcuts = new ShortcutProcessor(clock) { Log = log.Add };
		shortcuts.Triggered += fired.Add;
		shortcuts.TapRoute = () =>
		{
			asked++;
			return new PsTapDecision(route, "test");
		};
	}

	private static byte[] Body(int hat = 8, bool square = false, bool ps = false)
	{
		var b = new byte[InputState.BodySize];
		b[Hat] = (byte)(hat | (square ? 0x10 : 0));
		b[Ps] = (byte)(ps ? 0x01 : 0);
		return b;
	}

	private byte[] Step(byte[] body, double ms = 4)
	{
		clock.Advance(TimeSpan.FromMilliseconds(ms));
		byte[] copy = body.ToArray();
		shortcuts.Apply(copy);
		return copy;
	}

	private static bool PsOf(byte[] b) => (b[Ps] & 0x01) != 0;

	// Presses PS for holdMs, releases it, then follows for 200 ms; true when the game saw PS at any point.
	private bool Tap(double holdMs = 120)
	{
		bool seen = PsOf(Step(Body(ps: true)));
		seen |= PsOf(Step(Body(ps: true), holdMs));
		for (int i = 0; i < 10; i++)
		{
			seen |= PsOf(Step(Body(), 20));
		}
		return seen;
	}

	[Theory]
	// Setting off: always the game.
	[InlineData(false, false, null, PsTapRoute.Game, "setting off")]
	[InlineData(false, true, null, PsTapRoute.Game, "setting off")]
	[InlineData(false, false, "acc.exe", PsTapRoute.Game, "setting off")]
	// Setting on: no game and the window not in front opens the app.
	[InlineData(true, false, null, PsTapRoute.OpenApp, "no game in front")]
	// A game in front: the game, as before; the app is neither shown nor hidden.
	[InlineData(true, false, "acc.exe", PsTapRoute.Game, "game in front: acc.exe")]
	[InlineData(true, true, "acc.exe", PsTapRoute.Game, "game in front: acc.exe")]
	// The window already in front: hidden again (and not passed on).
	[InlineData(true, true, null, PsTapRoute.HideApp, "app in front")]
	public void Rule(bool opensApp, bool appInFront, string? gameInFront, PsTapRoute expected, string reason) =>
		Assert.Equal(new PsTapDecision(expected, reason), PsTapRule.Decide(opensApp, appInFront, gameInFront));

	[Theory]
	// "Also In Games" on, setting off: still the game.
	[InlineData(false, false, "acc.exe", PsTapRoute.Game, "setting off")]
	[InlineData(false, true, null, PsTapRoute.Game, "setting off")]
	// A game in front: the app opens anyway.
	[InlineData(true, false, "acc.exe", PsTapRoute.OpenApp, "game in front, opens anyway: acc.exe")]
	// The window in front hides it, even while the game is still reported in front (refreshed once a second).
	[InlineData(true, true, "acc.exe", PsTapRoute.HideApp, "app in front")]
	[InlineData(true, true, null, PsTapRoute.HideApp, "app in front")]
	// Neither: opens the app.
	[InlineData(true, false, null, PsTapRoute.OpenApp, "no game in front")]
	public void Rule_AlsoInGames(bool opensApp, bool appInFront, string? gameInFront, PsTapRoute expected, string reason) =>
		Assert.Equal(new PsTapDecision(expected, reason), PsTapRule.Decide(opensApp, appInFront, gameInFront, inGames: true));

	// In a game with "Also In Games": the tap that opens the app and the one that hides it never reach the game; a long
	// press still does.
	[Fact]
	public void AlsoInGames_ConsumedTapsStayAway_LongPressReachesTheGame()
	{
		bool shown = false;
		shortcuts.TapRoute = () => PsTapRule.Decide(true, shown, "acc.exe", inGames: true);
		shortcuts.Triggered += a => shown = a == ShortcutAction.OpenApp || (a != ShortcutAction.HideApp && shown);
		Assert.False(Tap());
		Assert.False(Tap());
		Assert.True(Tap(holdMs: 700));
		Assert.Equal([ShortcutAction.OpenApp, ShortcutAction.HideApp], fired);
		Assert.Equal("ps tap: 140 ms -> OpenApp (game in front, opens anyway: acc.exe)", log[0]);
		Assert.Equal("ps tap: 140 ms -> HideApp (app in front)", log[1]);
		Assert.StartsWith("ps tap: 720 ms -> Game (held too long", log[2]);
	}

	[Fact]
	public void ShortTap_WithNoGame_OpensTheApp_AndIsNotPassedOn()
	{
		Assert.False(Tap());
		Assert.Equal([ShortcutAction.OpenApp], fired);
		Assert.Equal(1, asked);
		Assert.Equal(["ps tap: 140 ms -> OpenApp (test)"], log);
	}

	// "Coming and going": the first tap shows the window, the next one (window in front) hides it, then shows it again.
	[Fact]
	public void Taps_ShowAndHideTheApp_InTurn()
	{
		bool shown = false;
		shortcuts.TapRoute = () => PsTapRule.Decide(true, shown, null);
		shortcuts.Triggered += a => shown = a == ShortcutAction.OpenApp || (a != ShortcutAction.HideApp && shown);
		Assert.False(Tap());
		Assert.False(Tap());
		Assert.False(Tap());
		Assert.Equal([ShortcutAction.OpenApp, ShortcutAction.HideApp, ShortcutAction.OpenApp], fired);
		Assert.Equal(["ps tap: 140 ms -> OpenApp (no game in front)", "ps tap: 140 ms -> HideApp (app in front)",
			"ps tap: 140 ms -> OpenApp (no game in front)"], log);
	}

	[Fact]
	public void ShortTap_WithAGameInFront_ReachesTheGame()
	{
		shortcuts.TapRoute = () => PsTapRule.Decide(true, false, "acc.exe");
		Assert.True(Tap());
		Assert.Empty(fired);
		Assert.Equal(["ps tap: 140 ms -> Game (game in front: acc.exe)"], log);
	}

	[Fact]
	public void ShortTap_WithTheWindowInFront_HidesTheApp()
	{
		route = PsTapRoute.HideApp;
		Assert.False(Tap());
		Assert.Equal([ShortcutAction.HideApp], fired);
	}

	[Fact]
	public void NoRoute_OrAThrowingOne_ReachesTheGame()
	{
		shortcuts.TapRoute = null;
		Assert.True(Tap());
		shortcuts.TapRoute = () => throw new InvalidOperationException();
		Assert.True(Tap());
		Assert.Empty(fired);
		Assert.Equal(["ps tap: 140 ms -> Game (no route)", "ps tap: 140 ms -> Game (route failed: InvalidOperationException)"], log);
	}

	[Fact]
	public void LongHold_IsNotATap_AndReachesTheGame()
	{
		Assert.True(Tap(holdMs: ShortcutProcessor.OpenAppMaxHold.TotalMilliseconds + 100));
		Assert.Empty(fired);
		Assert.Equal(0, asked);
		Assert.Equal(["ps tap: 720 ms -> Game (held too long (over 600 ms))"], log);
	}

	[Fact]
	public void Combo_DoesNotOpenTheApp_AndStillWorks()
	{
		Step(Body(ps: true));
		Step(Body(ps: true, square: true));
		Step(Body(ps: true));
		for (int i = 0; i < 10; i++)
		{
			Assert.False(PsOf(Step(Body(), 20)));
		}
		Assert.Equal([ShortcutAction.ToggleSupportedMode], fired);
		Assert.Equal(0, asked);
		Assert.Equal(["ps tap: 28 ms -> combo (modifier used)"], log);
	}

	[Fact]
	public void MasterSwitchOff_PsPassesUnchanged_AndNothingOpens()
	{
		shortcuts.Enabled = false;
		Assert.True(PsOf(Step(Body(ps: true))));
		Assert.False(PsOf(Step(Body())));
		Assert.Empty(fired);
		Assert.Equal(0, asked);
		Assert.Equal(["ps tap: 4 ms -> Game (shortcuts off)"], log);
	}

	[Fact]
	public void GameInFront_FollowsTheForegroundWindow_NotJustARunningGame()
	{
		var processes = new GameModeTests.FakeProcesses();
		var catalog = new GameCatalog([new CatalogGame("Assetto Corsa Competizione", ["acc.exe"], null, false, true)]);
		var games = new GameModeController(catalog, new GameWatcher(catalog, processes), null);
		Assert.False(games.IsGameInFront);
		processes.Show("acc.exe");
		games.Tick();
		Assert.True(games.IsGameInFront);
		Assert.Equal("acc.exe", games.GameInFront);
		// Alt-tab to another app: the game still runs (ActiveGame) but is not in front.
		processes.Show("notepad.exe");
		games.Tick();
		Assert.NotNull(games.ActiveGame);
		Assert.False(games.IsGameInFront);
	}
}
