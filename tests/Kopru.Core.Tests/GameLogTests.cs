// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;

namespace Kopru.Core.Tests;

// The game lines in the log: recognition and the active game, once per change (not per poll), no paths.
public class GameLogTests
{
	private static readonly GameCatalog Catalog = new(
	[
		new CatalogGame("Lego Batman: Legacy of the Dark Knight", ["LEGOBatmanLotDK-Win64-Shipping.exe"], null, true, true),
		new CatalogGame("Returnal", ["Returnal-Win64-Shipping.exe"], 1649240, true, true),
	]);

	private const string BatmanPath = @"C:\Users\someone\Games\LEGO Batman\LEGOBatmanLotDK-Win64-Shipping.exe";

	[Fact]
	public void Watcher_LogsAnExeNameMatch_OncePerProcess_WithoutThePath()
	{
		var processes = new GameModeTests.FakeProcesses();
		var log = new List<string>();
		var watcher = new GameWatcher(Catalog, processes, log: log.Add);
		processes.Show("LEGOBatmanLotDK-Win64-Shipping.exe", BatmanPath);
		for (int i = 0; i < 5; i++)
		{
			Assert.NotNull(watcher.Poll());
		}
		Assert.Equal(["game: Lego Batman: Legacy of the Dark Knight recognised by exe name (LEGOBatmanLotDK-Win64-Shipping.exe)"], log);
	}

	private static (GameModeController Games, GameModeTests.FakeProcesses Processes, List<string> Log) Setup()
	{
		var processes = new GameModeTests.FakeProcesses();
		var log = new List<string>();
		var games = new GameModeController(Catalog, new GameWatcher(Catalog, processes, log: _ => { }), null) { Log = log.Add };
		return (games, processes, log);
	}

	[Fact]
	public void Controller_LogsTheActiveGame_OncePerChange()
	{
		(GameModeController games, GameModeTests.FakeProcesses processes, List<string> log) = Setup();
		processes.Show("LEGOBatmanLotDK-Win64-Shipping.exe", BatmanPath);
		for (int i = 0; i < 5; i++)
		{
			games.Tick();
		}
		Assert.Equal(["game: Lego Batman: Legacy of the Dark Knight is the active game (LEGOBatmanLotDK-Win64-Shipping.exe)"], log);

		processes.Exit("LEGOBatmanLotDK-Win64-Shipping.exe");
		games.Tick();
		games.Tick();
		Assert.Equal("game: Lego Batman: Legacy of the Dark Knight is no longer the active game (it exited)", log[^1]);
		Assert.Equal(2, log.Count);
		Assert.DoesNotContain(log, l => l.Contains('\\'));
	}

	[Fact]
	public void Controller_LogsATakeOver()
	{
		(GameModeController games, GameModeTests.FakeProcesses processes, List<string> log) = Setup();
		processes.Show("Returnal-Win64-Shipping.exe");
		games.Tick();
		processes.Show("LEGOBatmanLotDK-Win64-Shipping.exe");
		games.Tick();
		Assert.Equal(
		[
			"game: Returnal is the active game (Returnal-Win64-Shipping.exe)",
			"game: Returnal is no longer the active game (Lego Batman: Legacy of the Dark Knight took over)",
			"game: Lego Batman: Legacy of the Dark Knight is the active game (LEGOBatmanLotDK-Win64-Shipping.exe)",
		], log);
	}
}
