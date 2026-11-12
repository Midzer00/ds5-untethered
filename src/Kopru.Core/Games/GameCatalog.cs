// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kopru.Core.Games;

// One game known to use the DualSense natively on PC (an entry of supported-games.json).
public sealed record CatalogGame(
	string Name,
	IReadOnlyList<string> Exe,
	long? SteamAppId = null,
	bool Haptics = false,
	bool AdaptiveTriggers = false,
	string? ExeConfidence = null,
	string? Notes = null);

// The list of games with native DualSense support, embedded as supported-games.json. Exe names match
// case-insensitively, by file name only ("C:\Games\Foo.EXE" matches "foo.exe"), and an Unreal Engine pair matches
// either way ("Foo.exe" matches "Foo-Win64-Shipping.exe"). A game without exe names can be found by its name (FindByName).
public sealed class GameCatalog
{
	public const string ResourceName = "Kopru.Core.Games.supported-games.json";

	private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

	private readonly Dictionary<string, CatalogGame> byExe = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<long, CatalogGame> bySteamAppId = [];
	private readonly Dictionary<string, CatalogGame> byName = new(StringComparer.Ordinal);

	// The shortest normalised name FindByName accepts: shorter names ("Luto", "F1 22") are too easy to hit by chance.
	public const int MinNameLength = 6;

	// The Unreal Engine game binary next to its small launcher ("Foo.exe" starts "Foo\Binaries\Win64\Foo-Win64-Shipping.exe").
	public const string ShippingSuffix = "-Win64-Shipping.exe";

	public GameCatalog(IEnumerable<CatalogGame> games)
	{
		Games = games.Where(g => !string.IsNullOrWhiteSpace(g.Name)).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
		foreach (CatalogGame game in Games)
		{
			if (game.SteamAppId is long appId)
			{
				bySteamAppId.TryAdd(appId, game);
			}
			bool hasExe = false;
			foreach (string exe in game.Exe ?? [])
			{
				string key = Normalize(exe);
				if (key.Length > 0)
				{
					hasExe = true;
					byExe.TryAdd(key, game);
				}
			}
			string name = NormalizeName(game.Name);
			if (!hasExe && name.Length >= MinNameLength)
			{
				byName.TryAdd(name, game);
			}
		}
	}

	public static GameCatalog Empty { get; } = new([]);

	public IReadOnlyList<CatalogGame> Games { get; }

	public string? Version { get; private init; }

	// The "version" field as a number (0 when missing).
	public int SchemaVersion { get; private init; }

	public string? Updated { get; private init; }

	public CatalogGame? Find(string? exe)
	{
		string key = Normalize(exe);
		if (key.Length == 0)
		{
			return null;
		}
		if (byExe.TryGetValue(key, out CatalogGame? game))
		{
			return game;
		}
		return ShippingPair(key) is string pair && byExe.TryGetValue(pair, out game) ? game : null;
	}

	// Whether some game can be found by name (one without exe names).
	public bool HasNamedGames => byName.Count > 0;

	// The game without exe names whose whole normalised name equals the normalised text, or null. Never a partial match.
	public CatalogGame? FindByName(string? text)
	{
		string key = NormalizeName(text);
		return key.Length >= MinNameLength && byName.TryGetValue(key, out CatalogGame? game) ? game : null;
	}

	// The other half of an Unreal Engine pair: "Foo-Win64-Shipping.exe" -> "Foo.exe" and "Foo.exe" -> "Foo-Win64-Shipping.exe".
	public static string? ShippingPair(string exe)
	{
		if (exe.EndsWith(ShippingSuffix, StringComparison.OrdinalIgnoreCase))
		{
			return exe.Length > ShippingSuffix.Length ? exe[..^ShippingSuffix.Length] + ".exe" : null;
		}
		return exe.Length > 4 && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] + ShippingSuffix : null;
	}

	// Lower-case letters and digits only ("LEGO® Batman™: Legacy" -> "legobatmanlegacy"); "" for null.
	public static string NormalizeName(string? text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}
		var builder = new System.Text.StringBuilder(text.Length);
		foreach (char c in text)
		{
			if (char.IsLetterOrDigit(c))
			{
				builder.Append(char.ToLowerInvariant(c));
			}
		}
		return builder.ToString();
	}

	public CatalogGame? FindBySteamAppId(long? appId) => appId is long id && bySteamAppId.TryGetValue(id, out CatalogGame? game) ? game : null;

	// The file name only, trimmed ("" for null or blank).
	public static string Normalize(string? exe) => string.IsNullOrWhiteSpace(exe) ? string.Empty : Path.GetFileName(exe.Trim());

	// Throws JsonException on malformed JSON.
	public static GameCatalog Parse(string json)
	{
		CatalogFile? file = JsonSerializer.Deserialize<CatalogFile>(json, Options);
		return new GameCatalog(file?.Games ?? []) { Version = file?.Version.ToString(), SchemaVersion = file?.Version ?? 0, Updated = file?.Updated };
	}

	// The embedded catalog; empty when it is missing or unreadable.
	public static GameCatalog LoadEmbedded()
	{
		try
		{
			using Stream? stream = typeof(GameCatalog).Assembly.GetManifestResourceStream(ResourceName);
			if (stream == null)
			{
				return Empty;
			}
			using StreamReader reader = new(stream);
			return Parse(reader.ReadToEnd());
		}
		catch (JsonException)
		{
			return Empty;
		}
	}

	// The highest "version" (file format) this build understands.
	public const int MaxSchemaVersion = 1;

	// Sorts by "updated" (ISO date, compared as text), then by version, then by game count (two lists from the same day);
	// positive when this one is newer.
	public int CompareFreshness(GameCatalog other)
	{
		int byDate = string.CompareOrdinal(Updated ?? string.Empty, other.Updated ?? string.Empty);
		if (byDate != 0)
		{
			return Math.Sign(byDate);
		}
		int byVersion = SchemaVersion.CompareTo(other.SchemaVersion);
		return byVersion != 0 ? byVersion : Games.Count.CompareTo(other.Games.Count);
	}

	// The embedded catalog or the downloaded copy at downloadedPath, whichever is newer. A missing, broken or
	// unsupported download falls back to the embedded one.
	public static GameCatalog Load(string? downloadedPath)
	{
		GameCatalog embedded = LoadEmbedded();
		GameCatalog? downloaded = TryLoadFile(downloadedPath);
		return downloaded != null && downloaded.CompareFreshness(embedded) > 0 ? downloaded : embedded;
	}

	// The catalog in path, or null when it is missing, unreadable or not a usable catalog.
	public static GameCatalog? TryLoadFile(string? path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return null;
		}
		try
		{
			if (!File.Exists(path))
			{
				return null;
			}
			GameCatalog catalog = Parse(File.ReadAllText(path));
			return GameCatalogUpdater.Validate(catalog) == null ? catalog : null;
		}
		catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
		{
			return null;
		}
	}

	private sealed class CatalogFile
	{
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("updated")]
		public string? Updated { get; set; }

		[JsonPropertyName("games")]
		public List<CatalogGame>? Games { get; set; }
	}
}
