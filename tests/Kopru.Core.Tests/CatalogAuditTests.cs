// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;

namespace Kopru.Core.Tests;

// Invariants of the embedded supported games list that the watcher's lookups rely on (from the detection study).
public class CatalogAuditTests
{
	private static readonly GameCatalog Embedded = GameCatalog.LoadEmbedded();

	// Folder names every install path has; a catalog name equal to one would match every game in that place.
	[Theory]
	[InlineData("Program Files")]
	[InlineData("Program Files (x86)")]
	[InlineData("steamapps")]
	[InlineData("common")]
	[InlineData("SteamLibrary")]
	[InlineData("Binaries")]
	[InlineData("Win64")]
	[InlineData("Epic Games")]
	[InlineData("WindowsApps")]
	[InlineData("XboxGames")]
	[InlineData("Windows")]
	[InlineData("System32")]
	public void CommonInstallFolderNames_MatchNoGame(string folder) => Assert.Null(Embedded.FindByName(folder));

	[Fact]
	public void NoTwoEntries_ShareANormalisedName()
	{
		var names = Embedded.Games.Select(g => GameCatalog.NormalizeName(g.Name)).ToList();
		Assert.Equal(names.Count, names.Distinct().Count());
	}

	[Fact]
	public void NoCatalogExe_IsAnIgnoredApp() =>
		Assert.DoesNotContain(Embedded.Games.SelectMany(g => g.Exe), e => GameWatcher.Ignored.Contains(GameCatalog.Normalize(e)));

	// Every entry whose name is long enough is found by its own name (none is shadowed by another).
	[Fact]
	public void EveryLongEnoughEntry_IsFoundByItsOwnName()
	{
		foreach (CatalogGame game in Embedded.Games.Where(g => GameCatalog.NormalizeName(g.Name).Length >= GameCatalog.MinNameLength))
		{
			Assert.Same(game, Embedded.FindByName(game.Name));
		}
	}

	// Every exe name (and its Unreal Engine pair) finds the entry that lists it: no exe name is shared by two entries.
	[Fact]
	public void EveryExeName_FindsItsOwnEntry_AndNoOther()
	{
		var owners = Embedded.Games.SelectMany(g => g.Exe.Select(e => (Exe: GameCatalog.Normalize(e), Game: g)))
			.GroupBy(x => x.Exe, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(x => x.Game).Distinct().Count() > 1).Select(g => g.Key);
		Assert.Empty(owners);
		foreach (CatalogGame game in Embedded.Games)
		{
			foreach (string exe in game.Exe)
			{
				Assert.Same(game, Embedded.Find(exe));
				if (GameCatalog.ShippingPair(GameCatalog.Normalize(exe)) is { } pair)
				{
					Assert.Same(game, Embedded.Find(pair));
				}
			}
		}
	}

	// Names of programs that start a game rather than being it (launchers, anti-cheat stubs) and generic names unrelated
	// programs use: Steam starts them for many games, so they must not identify one.
	private static readonly string[] LauncherHints = ["launcher", "start_protected", "bootstrap", "easyanticheat", "anticheat", "battleye", "eac.exe", "_eac"];

	private static readonly string[] GenericExe =
	[
		"game.exe", "main.exe", "start.exe", "client.exe", "app.exe", "play.exe", "run.exe", "setup.exe", "update.exe", "engine.exe",
		"player.exe", "launcher.exe", "startup.exe", "control.exe", "xrEngine.exe", "PlayGTAV.exe", "HYP.exe", "metro.exe",
		"start_epic_crossplay_game.exe", "UnityCrashHandler64.exe"
	];

	[Fact]
	public void NoCatalogExe_IsLauncherLikeOrGeneric()
	{
		var names = Embedded.Games.SelectMany(g => g.Exe).Select(GameCatalog.Normalize).ToList();
		Assert.DoesNotContain(names, e => LauncherHints.Any(h => e.Contains(h, StringComparison.OrdinalIgnoreCase)));
		Assert.DoesNotContain(names, e => GenericExe.Contains(e, StringComparer.OrdinalIgnoreCase));
	}

	[Fact]
	public void NoTwoEntries_ShareASteamAppId() =>
		Assert.Empty(Embedded.Games.Where(g => g.SteamAppId != null).GroupBy(g => g.SteamAppId).Where(g => g.Count() > 1));

	// Call of Duty HQ (Steam app 1938090) holds Modern Warfare II, Warzone, Black Ops 6 and Modern Warfare III, all run as
	// cod.exe: one entry for all of them, so the result never depends on which of several entries comes first. It is named
	// after the install folder, not "Call of Duty": Call of Duty (2003) installs into a folder of that name.
	[Fact]
	public void CallOfDutyHq_IsOneEntry()
	{
		CatalogGame cod = Assert.Single(Embedded.Games, g => g.Name == "Call of Duty HQ");
		Assert.Same(cod, Embedded.FindBySteamAppId(1938090));
		Assert.Same(cod, Embedded.Find("cod.exe"));
		Assert.Same(cod, Embedded.Find("cod22-cod.exe"));
		Assert.Same(cod, Embedded.FindByName("Call of Duty HQ"));
		Assert.Null(Embedded.FindByName("Call of Duty"));
		Assert.Null(Embedded.FindByInstallDir("Call of Duty"));
		Assert.DoesNotContain(Embedded.Games, g => g != cod && g.Exe.Contains("cod.exe", StringComparer.OrdinalIgnoreCase));
	}

	// "Painkiller (2025)", "Tomb Raider (2013)": the year tells the game from an older one of the same name, so neither
	// the year-less name, nor an exe or install folder named like it, may find the new game.
	[Theory]
	[InlineData("Delta Force", "Delta Force.exe")]
	[InlineData("Tomb Raider", "TombRaider.exe")]
	[InlineData("Painkiller", "Painkiller.exe")]
	[InlineData("Screamer", "Screamer.exe")]
	[InlineData("Marathon", "Marathon.exe")]
	[InlineData("Riven", "Riven.exe")]
	[InlineData("Lords of the Fallen", "LordsOfTheFallen.exe")]
	public void DatedEntries_AreNotFoundByTheOlderGamesName(string name, string exe)
	{
		Assert.Contains(Embedded.Games, g => g.Name.StartsWith(name + " (", StringComparison.Ordinal));
		Assert.Null(Embedded.FindByName(name));
		Assert.Null(Embedded.FindByInstallDir(name));
		Assert.Null(Embedded.Find(exe));
	}

	// A stored Steam install folder name finds its own entry, differs from the entry's name and is not a plain single
	// word ("Sparta", "Season") or a folder every install path has.
	[Fact]
	public void EverySteamInstallDir_FindsItsOwnEntry_AndIsSpecific()
	{
		var dirs = Embedded.Games.Where(g => g.SteamInstallDir != null).ToList();
		Assert.NotEmpty(dirs);
		foreach (CatalogGame game in dirs)
		{
			Assert.Same(game, Embedded.FindByInstallDir(game.SteamInstallDir));
			Assert.NotEqual(GameCatalog.NormalizeName(game.Name), GameCatalog.NormalizeName(game.SteamInstallDir));
			Assert.DoesNotMatch(@"^[A-Z]?[a-z]+$", game.SteamInstallDir!);
		}
	}

	[Theory]
	[InlineData("Program Files")]
	[InlineData("steamapps")]
	[InlineData("common")]
	[InlineData("Binaries")]
	[InlineData("Win64")]
	[InlineData("bin")]
	[InlineData("Game")]
	[InlineData("Content")]
	[InlineData("Epic Games")]
	public void CommonInstallFolderNames_AreNoInstallDir(string folder) => Assert.Null(Embedded.FindByInstallDir(folder));
}
