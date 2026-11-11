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
	private PsTapRoute route = PsTapRoute.OpenApp;
	private int asked;

	public PsOpenAppTests()
	{
		shortcuts = new ShortcutProcessor(clock);
		shortcuts.Triggered += fired.Add;
		shortcuts.TapRoute = () =>
		{
			asked++;
			return route;
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
	[InlineData(false, false, false, PsTapRoute.Game)]
	[InlineData(false, true, false, PsTapRoute.Game)]
	[InlineData(false, false, true, PsTapRoute.Game)]
	// Setting on: no game and the window not in front opens the app.
	[InlineData(true, false, false, PsTapRoute.OpenApp)]
	// A game in front: the game, as before.
	[InlineData(true, false, true, PsTapRoute.Game)]
	// The window already in front: nothing (and not passed on).
	[InlineData(true, true, false, PsTapRoute.Nothing)]
	[InlineData(true, true, true, PsTapRoute.Nothing)]
	public void Rule(bool opensApp, bool appInFront, bool gameInFront, PsTapRoute expected) =>
		Assert.Equal(expected, PsTapRule.Decide(opensApp, appInFront, gameInFront));

	[Fact]
	public void ShortTap_WithNoGame_OpensTheApp_AndIsNotPassedOn()
	{
		Assert.False(Tap());
		Assert.Equal([ShortcutAction.OpenApp], fired);
		Assert.Equal(1, asked);
	}

	[Fact]
	public void ShortTap_WithAGameInFront_ReachesTheGame()
	{
		route = PsTapRoute.Game;
		Assert.True(Tap());
		Assert.Empty(fired);
	}

	[Fact]
	public void ShortTap_WithTheWindowInFront_DoesNothing()
	{
		route = PsTapRoute.Nothing;
		Assert.False(Tap());
		Assert.Empty(fired);
	}

	[Fact]
	public void NoRoute_OrAThrowingOne_ReachesTheGame()
	{
		shortcuts.TapRoute = null;
		Assert.True(Tap());
		shortcuts.TapRoute = () => throw new InvalidOperationException();
		Assert.True(Tap());
		Assert.Empty(fired);
	}

	[Fact]
	public void LongHold_IsNotATap_AndReachesTheGame()
	{
		Assert.True(Tap(holdMs: ShortcutProcessor.OpenAppMaxHold.TotalMilliseconds + 100));
		Assert.Empty(fired);
		Assert.Equal(0, asked);
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
	}

	[Fact]
	public void MasterSwitchOff_PsPassesUnchanged_AndNothingOpens()
	{
		shortcuts.Enabled = false;
		Assert.True(PsOf(Step(Body(ps: true))));
		Assert.False(PsOf(Step(Body())));
		Assert.Empty(fired);
		Assert.Equal(0, asked);
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
		// Alt-tab to another app: the game still runs (ActiveGame) but is not in front.
		processes.Show("notepad.exe");
		games.Tick();
		Assert.NotNull(games.ActiveGame);
		Assert.False(games.IsGameInFront);
	}
}
