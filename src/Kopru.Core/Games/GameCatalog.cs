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
// case-insensitively, by file name only ("C:\Games\Foo.EXE" matches "foo.exe").
public sealed class GameCatalog
{
	public const string ResourceName = "Kopru.Core.Games.supported-games.json";

	private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

	private readonly Dictionary<string, CatalogGame> byExe = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<long, CatalogGame> bySteamAppId = [];

	public GameCatalog(IEnumerable<CatalogGame> games)
	{
		Games = games.Where(g => !string.IsNullOrWhiteSpace(g.Name)).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
		foreach (CatalogGame game in Games)
		{
			if (game.SteamAppId is long appId)
			{
				bySteamAppId.TryAdd(appId, game);
			}
			foreach (string exe in game.Exe ?? [])
			{
				string key = Normalize(exe);
				if (key.Length > 0)
				{
					byExe.TryAdd(key, game);
				}
			}
		}
	}

	public static GameCatalog Empty { get; } = new([]);

	public IReadOnlyList<CatalogGame> Games { get; }

	public string? Version { get; private init; }

	// The "version" field as a number (0 when missing).
	public int SchemaVersion { get; private init; }

	public string? Updated { get; private init; }

	public CatalogGame? Find(string? exe) => exe != null && byExe.TryGetValue(Normalize(exe), out CatalogGame? game) ? game : null;

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
