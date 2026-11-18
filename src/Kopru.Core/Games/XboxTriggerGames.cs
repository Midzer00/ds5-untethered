// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace Kopru.Core.Games;

// One game that sends trigger rumble to an Xbox One / Series controller ("impulse triggers").
public sealed record XboxTriggerGame(string Name, long? SteamAppId, IReadOnlyList<string> Exe, string? Source = null)
{
	// The per-game settings key of the game's row and of the running game recognised by this list: the Steam app's key
	// when the entry names one (the same key as the game found installed by Steam), else the game's name.
	public string Key => SteamAppId is long id
		? FoundGame.KeyFor(InstalledSource.Steam, id.ToString(CultureInfo.InvariantCulture))
		: Name;
}

// The games known to send Xbox trigger rumble, from xbox-trigger-games.json (embedded; a separate data file under its own
// licence, see THIRD-PARTY-NOTICES.md; how to refresh it: docs/RELEASING.md). File format:
//   { "generated": "yyyy-MM-dd", "note": "...", "license": "...",
//     "games": [ { "name": "...", "steamAppId": 123 | null, "exe": ["a.exe"], "source": "https://..." } ] }
// A game is matched by its Steam app id, by an exe name, or by its whole normalised name (the same name and folder rules as
// the supported games list: GameCatalog.FindByName). Nothing is looked up online at run time.
public sealed class XboxTriggerGames
{
	public const string ResourceName = "Kopru.Core.Games.xbox-trigger-games.json";

	// The same matching as the supported games list (exe, Steam app id, whole name), on this list's games only.
	private readonly GameCatalog matcher;
	private readonly Dictionary<string, XboxTriggerGame> byName = new(StringComparer.Ordinal);
	private readonly Dictionary<long, XboxTriggerGame> byApp = [];
	private readonly Dictionary<string, XboxTriggerGame> byKey = new(StringComparer.OrdinalIgnoreCase);

	public XboxTriggerGames(IEnumerable<XboxTriggerGame> games, string? generated = null)
	{
		Games = games.Where(g => !string.IsNullOrWhiteSpace(g.Name))
			.Select(g => g with { Name = g.Name.Trim(), Exe = (g.Exe ?? []).Where(e => GameCatalog.Normalize(e).Length > 0).ToList() })
			.DistinctBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
			.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
			.ToList();
		foreach (XboxTriggerGame game in Games)
		{
			byName.TryAdd(game.Name, game);
			byKey.TryAdd(game.Key, game);
			if (game.SteamAppId is long id and > 0)
			{
				byApp.TryAdd(id, game);
			}
		}
		matcher = new GameCatalog(Games.Select(g => new CatalogGame(g.Name, g.Exe, g.SteamAppId is > 0 ? g.SteamAppId : null)));
		Generated = generated;
	}

	public static XboxTriggerGames Empty { get; } = new([]);

	public IReadOnlyList<XboxTriggerGame> Games { get; }

	public int Count => Games.Count;

	// The date the list was made ("yyyy-MM-dd"), or null.
	public string? Generated { get; }

	public XboxTriggerGame? FindBySteamAppId(long? appId) => appId is long id && byApp.TryGetValue(id, out XboxTriggerGame? game) ? game : null;

	// By an exe file name (an Unreal Engine "-Win64-Shipping" pair included).
	public XboxTriggerGame? FindByExe(string? exe) => Of(matcher.Find(exe));

	// By the whole normalised name (a folder name, product name, window title or another list's name).
	public XboxTriggerGame? FindByName(string? text) => Of(matcher.FindByName(text));

	// By a row's or running game's key (XboxTriggerGame.Key).
	public XboxTriggerGame? FindByKey(string? key) => key != null && byKey.TryGetValue(key, out XboxTriggerGame? game) ? game : null;

	// Whether some game can be found by name.
	public bool HasNamedGames => matcher.HasNamedGames;

	private XboxTriggerGame? Of(CatalogGame? game) => game != null && byName.TryGetValue(game.Name, out XboxTriggerGame? found) ? found : null;

	// Throws JsonException on malformed JSON. Entries without a name are skipped; "exe" may also be one string.
	public static XboxTriggerGames Parse(string json)
	{
		using JsonDocument doc = JsonDocument.Parse(json);
		JsonElement root = doc.RootElement;
		JsonElement list = root.ValueKind switch
		{
			JsonValueKind.Array => root,
			JsonValueKind.Object when TryGet(root, "games", out JsonElement g) && g.ValueKind == JsonValueKind.Array => g,
			_ => throw new JsonException("xbox-trigger-games.json: no games list")
		};
		List<XboxTriggerGame> games = [];
		foreach (JsonElement item in list.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Object || !TryGet(item, "name", out JsonElement name) || name.ValueKind != JsonValueKind.String)
			{
				continue;
			}
			long? appId = null;
			if (TryGet(item, "steamAppId", out JsonElement app))
			{
				if (app.ValueKind == JsonValueKind.Number && app.TryGetInt64(out long n) && n > 0)
				{
					appId = n;
				}
				else if (app.ValueKind == JsonValueKind.String && long.TryParse(app.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out long s) && s > 0)
				{
					appId = s;
				}
			}
			List<string> exes = [];
			if (TryGet(item, "exe", out JsonElement exe))
			{
				if (exe.ValueKind == JsonValueKind.String)
				{
					exes.Add(exe.GetString()!);
				}
				else if (exe.ValueKind == JsonValueKind.Array)
				{
					exes.AddRange(exe.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
				}
			}
			string? source = TryGet(item, "source", out JsonElement src) && src.ValueKind == JsonValueKind.String ? src.GetString() : null;
			games.Add(new XboxTriggerGame(name.GetString()!, appId, exes, source));
		}
		string? generated = root.ValueKind == JsonValueKind.Object && TryGet(root, "generated", out JsonElement gen) && gen.ValueKind == JsonValueKind.String
			? gen.GetString()
			: null;
		return new XboxTriggerGames(games, generated);
	}

	private static bool TryGet(JsonElement obj, string name, out JsonElement value)
	{
		foreach (JsonProperty p in obj.EnumerateObject())
		{
			if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				value = p.Value;
				return true;
			}
		}
		value = default;
		return false;
	}

	private static XboxTriggerGames? embedded;

	// The embedded list (read once); empty when it is missing or unreadable.
	public static XboxTriggerGames LoadEmbedded()
	{
		if (embedded != null)
		{
			return embedded;
		}
		XboxTriggerGames list;
		try
		{
			using Stream? stream = typeof(XboxTriggerGames).Assembly.GetManifestResourceStream(ResourceName);
			if (stream == null)
			{
				list = Empty;
			}
			else
			{
				using StreamReader reader = new(stream);
				list = Parse(reader.ReadToEnd());
			}
		}
		catch (JsonException)
		{
			list = Empty;
		}
		return embedded = list;
	}
}
