// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.GameList;

// Keeps a "Last automatic refresh" section at the end of SOURCES.md, between two markers, up to date.
public static class SourcesNote
{
	public const string Start = "<!-- gamelist:start -->";
	public const string End = "<!-- gamelist:end -->";

	public static string Update(string sources, string today, string method, int wikiCount, MergeResult result)
	{
		string block = string.Join("\r\n",
			Start,
			"## Last automatic refresh (tools/Kopru.GameList)",
			"",
			$"{today}: read {wikiCount} games via {method}; flags follow the PCGamingWiki lists (true, limited and hackable all count as listed).",
			$"Added {result.Added.Count}, removed {result.Removed.Count}, flags changed {result.Changed.Count}. New entries have no exe name yet (\"{GameListMerger.NewEntryNote}\"); fill them in by hand.",
			"",
			"- https://www.pcgamingwiki.com/wiki/" + PcgwClient.HapticsPage,
			"- https://www.pcgamingwiki.com/wiki/" + PcgwClient.TriggersPage,
			End);
		int s = sources.IndexOf(Start, StringComparison.Ordinal);
		int e = sources.IndexOf(End, StringComparison.Ordinal);
		if (s >= 0 && e > s)
		{
			return sources[..s] + block + sources[(e + End.Length)..];
		}
		return sources.TrimEnd() + "\r\n\r\n" + block + "\r\n";
	}
}
