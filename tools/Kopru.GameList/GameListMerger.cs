// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kopru.GameList;

// One game as PCGamingWiki lists it: on the haptic feedback list, the adaptive triggers list or both.
public sealed record WikiGame(string Name, bool Haptics, bool AdaptiveTriggers, long? SteamAppId = null);

// One entry of supported-games.json, in the file's field order.
public sealed class GameEntry
{
	[JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
	[JsonPropertyName("exe")] public List<string> Exe { get; set; } = [];
	[JsonPropertyName("steamAppId")] public long? SteamAppId { get; set; }
	[JsonPropertyName("haptics")] public bool Haptics { get; set; }
	[JsonPropertyName("adaptiveTriggers")] public bool AdaptiveTriggers { get; set; }
	[JsonPropertyName("exeConfidence")] public string? ExeConfidence { get; set; }
	[JsonPropertyName("notes")] public string? Notes { get; set; }
}

public sealed class GameListFile
{
	[JsonPropertyName("version")] public int Version { get; set; } = 1;
	[JsonPropertyName("updated")] public string Updated { get; set; } = string.Empty;
	[JsonPropertyName("games")] public List<GameEntry> Games { get; set; } = [];

	private static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		DefaultIgnoreCondition = JsonIgnoreCondition.Never
	};

	public static GameListFile Parse(string json) => JsonSerializer.Deserialize<GameListFile>(json, Options) ?? new GameListFile();

	// Two-space indented JSON with CRLF line ends and a final line end, like the checked-in file.
	public string ToJson() => JsonSerializer.Serialize(this, Options).Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
}

// What a merge changed: game names added, removed and with changed flags.
public sealed record MergeResult(GameListFile File, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Changed)
{
	public bool HasChanges => Added.Count + Removed.Count + Changed.Count > 0;
}

// Merges PCGamingWiki's lists into the current file. Matching is by Steam app id, else by name (case, accents and
// punctuation ignored). Matched entries take the wiki's flags and keep their exe names, notes and name; wiki games that
// are new get no exe (unknown) and a note; entries the wiki doesn't list are kept (manual entries) unless prune.
public static class GameListMerger
{
	public const string NewEntryNote = "Added from PCGamingWiki; exe name unknown";

	public static MergeResult Merge(GameListFile current, IReadOnlyList<WikiGame> wiki, string today, bool prune = false)
	{
		var result = new List<GameEntry>();
		var added = new List<string>();
		var removed = new List<string>();
		var changed = new List<string>();
		var used = new HashSet<WikiGame>();
		Dictionary<long, WikiGame> byId = wiki.Where(w => w.SteamAppId != null).GroupBy(w => w.SteamAppId!.Value).ToDictionary(g => g.Key, g => g.First());
		Dictionary<string, WikiGame> byName = wiki.GroupBy(w => Key(w.Name)).ToDictionary(g => g.Key, g => g.First());

		foreach (GameEntry entry in current.Games)
		{
			WikiGame? match = entry.SteamAppId is long id && byId.TryGetValue(id, out WikiGame? a) ? a
				: byName.TryGetValue(Key(entry.Name), out WikiGame? b) ? b : null;
			if (match == null || !used.Add(match))
			{
				if (prune && match == null)
				{
					removed.Add(entry.Name);
				}
				else
				{
					result.Add(entry);
				}
				continue;
			}
			if (entry.Haptics != match.Haptics || entry.AdaptiveTriggers != match.AdaptiveTriggers || (entry.SteamAppId == null && match.SteamAppId != null))
			{
				changed.Add(entry.Name);
			}
			entry.Haptics = match.Haptics;
			entry.AdaptiveTriggers = match.AdaptiveTriggers;
			entry.SteamAppId ??= match.SteamAppId;
			result.Add(entry);
		}
		foreach (WikiGame game in wiki.Where(w => !used.Contains(w) && (w.Haptics || w.AdaptiveTriggers)))
		{
			if (result.Any(e => Key(e.Name) == Key(game.Name)))
			{
				continue;
			}
			used.Add(game);
			added.Add(game.Name);
			result.Add(new GameEntry
			{
				Name = game.Name,
				SteamAppId = game.SteamAppId,
				Haptics = game.Haptics,
				AdaptiveTriggers = game.AdaptiveTriggers,
				Notes = NewEntryNote
			});
		}
		result.Sort((x, y) => StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name));
		bool any = added.Count + removed.Count + changed.Count > 0;
		var file = new GameListFile { Version = current.Version == 0 ? 1 : current.Version, Updated = any ? today : current.Updated, Games = result };
		return new MergeResult(file, added, removed, changed);
	}

	private static readonly Dictionary<string, string> Roman = new()
	{
		["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9", ["x"] = "10"
	};

	// Lower case letters and digits only, accents removed, Roman numeral words as digits
	// ("God of War Ragnarök" -> "godofwarragnarok", "Alan Wake II" -> "alanwake2").
	public static string Key(string name)
	{
		var words = new List<string>();
		var word = new StringBuilder();
		foreach (char c in name.Normalize(NormalizationForm.FormD) + " ")
		{
			if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
			{
				continue;
			}
			if (char.IsLetterOrDigit(c))
			{
				word.Append(char.ToLowerInvariant(c));
			}
			else if (char.IsWhiteSpace(c) && word.Length > 0)
			{
				words.Add(word.ToString());
				word.Clear();
			}
		}
		return string.Concat(words.Select(w => Roman.TryGetValue(w, out string? digits) ? digits : w));
	}

	// Joins the haptic feedback and adaptive trigger lists into one game per name.
	public static IReadOnlyList<WikiGame> Combine(IEnumerable<string> haptics, IEnumerable<string> triggers, IReadOnlyDictionary<string, long>? steamIds = null)
	{
		var games = new Dictionary<string, WikiGame>();
		foreach (string name in haptics)
		{
			games[name] = new WikiGame(name, true, false);
		}
		foreach (string name in triggers)
		{
			games[name] = games.TryGetValue(name, out WikiGame? g) ? g with { AdaptiveTriggers = true } : new WikiGame(name, false, true);
		}
		return games.Values
			.Select(g => steamIds != null && steamIds.TryGetValue(g.Name, out long id) ? g with { SteamAppId = id } : g)
			.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}
}
