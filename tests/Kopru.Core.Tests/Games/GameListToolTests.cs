// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;
using Kopru.GameList;

namespace Kopru.Core.Tests;

public class GameListToolTests
{
	private const string Current = """
		{
		  "version": 1,
		  "updated": "2026-11-01",
		  "games": [
		    { "name": "God of War Ragnarok", "exe": ["GoWR.exe"], "steamAppId": null, "haptics": true, "adaptiveTriggers": true,
		      "exeConfidence": "medium", "notes": "manual note" },
		    { "name": "Alan Wake 2", "exe": ["AlanWake2.exe"], "steamAppId": null, "haptics": true, "adaptiveTriggers": true,
		      "exeConfidence": "medium", "notes": "Epic only" },
		    { "name": "Space Marine 2", "exe": ["Warhammer 40000 Space Marine 2.exe"], "steamAppId": 2183900, "haptics": false,
		      "adaptiveTriggers": true, "exeConfidence": "low", "notes": "" },
		    { "name": "Hand Added", "exe": ["hand.exe"], "steamAppId": null, "haptics": true, "adaptiveTriggers": false,
		      "exeConfidence": "low", "notes": "not on the wiki" }
		  ]
		}
		""";

	// Two rows of a list page as the parse API renders them, plus the later per-model table that must be skipped.
	private const string ListPage = """
		<table class="wikitable sortable" style="width: 100%">
		<tbody><tr><th>Game</th><th>Supported</th></tr>
		<tr>
			<td><a href="/wiki/God_of_War_Ragnar%C3%B6k" title="God of War Ragnarök">God of War Ragnarök</a></td>
			<td data-sort-value="1"><div title="Native support" class="svg-icon svg-25 tickcross-true"></div></td>
		</tr>
		<tr>
			<td><a href="/web/2026/https://www.pcgamingwiki.com/wiki/Ratchet" title="Ratchet &amp; Clank: Rift Apart">Ratchet &amp; Clank: Rift Apart</a></td>
			<td><div class="svg-icon tickcross-limited"></div></td>
		</tr>
		</tbody></table>
		<table class="wikitable sortable"><tr>
			<td><a href="/wiki/Edge_Only" title="Edge Only">Edge Only</a></td>
		</tr></table>
		""";

	private static IReadOnlyList<WikiGame> Wiki =>
	[
		new("Alan Wake II", true, false),
		new("God of War Ragnarök", true, true, 2322010),
		new("Warhammer 40,000: Space Marine II", true, true, 2183900),
		new("Brand New Game", false, true, 123),
		new("Hackable Only", false, false),
	];

	[Fact]
	public void ParseListTable_ReadsTheFirstTableOnly()
	{
		Assert.Equal(["God of War Ragnarök", "Ratchet & Clank: Rift Apart"], PcgwClient.ParseListTable(ListPage));
		Assert.Throws<InvalidDataException>(() => PcgwClient.ParseListTable("<html>blocked</html>"));
	}

	[Fact]
	public void Combine_JoinsBothLists()
	{
		IReadOnlyList<WikiGame> games = GameListMerger.Combine(["A", "B"], ["B", "C"], new Dictionary<string, long> { ["C"] = 7 });
		Assert.Equal([new WikiGame("A", true, false), new WikiGame("B", true, true), new WikiGame("C", false, true, 7)], games);
	}

	[Theory]
	[InlineData("God of War Ragnarök", "God of War Ragnarok")]
	[InlineData("Alan Wake II", "Alan Wake 2")]
	[InlineData("S.T.A.L.K.E.R. 2: Heart of Chornobyl", "STALKER 2 Heart of Chornobyl")]
	public void Key_IgnoresAccentsPunctuationAndRomanNumerals(string a, string b) => Assert.Equal(GameListMerger.Key(a), GameListMerger.Key(b));

	[Fact]
	public void Merge_KeepsManualDataAndTakesWikiFlags()
	{
		MergeResult result = GameListMerger.Merge(GameListFile.Parse(Current), Wiki, "2026-11-16");
		var byName = result.File.Games.ToDictionary(g => g.Name);

		GameEntry gow = byName["God of War Ragnarok"];
		Assert.Equal(["GoWR.exe"], gow.Exe);
		Assert.Equal("manual note", gow.Notes);
		Assert.Equal(2322010, gow.SteamAppId);

		GameEntry alan = byName["Alan Wake 2"];
		Assert.True(alan.Haptics);
		Assert.False(alan.AdaptiveTriggers);
		Assert.Equal(["AlanWake2.exe"], alan.Exe);

		GameEntry marine = byName["Space Marine 2"];
		Assert.True(marine.Haptics);
		Assert.Equal(["Warhammer 40000 Space Marine 2.exe"], marine.Exe);

		Assert.True(byName.ContainsKey("Hand Added"));
		GameEntry added = byName["Brand New Game"];
		Assert.Empty(added.Exe);
		Assert.Equal(GameListMerger.NewEntryNote, added.Notes);
		Assert.False(byName.ContainsKey("Hackable Only"));

		Assert.Equal(["Brand New Game"], result.Added);
		Assert.Empty(result.Removed);
		Assert.Equal(["God of War Ragnarok", "Alan Wake 2", "Space Marine 2"], result.Changed);
		Assert.Equal("2026-11-16", result.File.Updated);
		Assert.Equal(result.File.Games.Select(g => g.Name).Order(StringComparer.OrdinalIgnoreCase), result.File.Games.Select(g => g.Name));
	}

	[Fact]
	public void Merge_Prune_RemovesEntriesTheWikiDoesNotList()
	{
		MergeResult result = GameListMerger.Merge(GameListFile.Parse(Current), Wiki, "2026-11-16", prune: true);
		Assert.Equal(["Hand Added"], result.Removed);
		Assert.DoesNotContain(result.File.Games, g => g.Name == "Hand Added");
	}

	[Fact]
	public void Merge_NoChanges_KeepsTheDate()
	{
		GameListFile file = GameListFile.Parse(Current);
		IReadOnlyList<WikiGame> same = file.Games.Select(g => new WikiGame(g.Name, g.Haptics, g.AdaptiveTriggers, g.SteamAppId)).ToList();
		MergeResult result = GameListMerger.Merge(file, same, "2026-11-16");
		Assert.False(result.HasChanges);
		Assert.Equal("2026-11-01", result.File.Updated);
	}

	[Fact]
	public void Output_LoadsInTheApp_AndTheCheckedInFileRoundTrips()
	{
		MergeResult result = GameListMerger.Merge(GameListFile.Parse(Current), Wiki, "2026-11-16");
		GameCatalog catalog = GameCatalog.Parse(result.File.ToJson());
		Assert.Equal(result.File.Games.Count, catalog.Games.Count);
		Assert.Equal("God of War Ragnarok", catalog.Find("gowr.exe")?.Name);

		string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Kopru.Core", "Games", "supported-games.json");
		string original = File.ReadAllText(path).Replace("\r\n", "\n");
		Assert.Equal(original, GameListFile.Parse(original).ToJson().Replace("\r\n", "\n"));
	}

	[Fact]
	public void SourcesNote_ReplacesItsOwnSection()
	{
		MergeResult result = GameListMerger.Merge(GameListFile.Parse(Current), Wiki, "2026-11-16");
		string once = SourcesNote.Update("# Sources\r\n", "2026-11-16", "test", 5, result);
		string twice = SourcesNote.Update(once, "2026-11-23", "test", 5, result);
		Assert.Contains("2026-11-23", twice);
		Assert.DoesNotContain("2026-11-16", twice);
		Assert.StartsWith("# Sources", twice);
	}
}
