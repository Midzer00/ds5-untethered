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

	public GameCatalog(IEnumerable<CatalogGame> games)
	{
		Games = games.Where(g => !string.IsNullOrWhiteSpace(g.Name)).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
		foreach (CatalogGame game in Games)
		{
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

	public string? Updated { get; private init; }

	public CatalogGame? Find(string? exe) => exe != null && byExe.TryGetValue(Normalize(exe), out CatalogGame? game) ? game : null;

	// The file name only, trimmed ("" for null or blank).
	public static string Normalize(string? exe) => string.IsNullOrWhiteSpace(exe) ? string.Empty : Path.GetFileName(exe.Trim());

	// Throws JsonException on malformed JSON.
	public static GameCatalog Parse(string json)
	{
		CatalogFile? file = JsonSerializer.Deserialize<CatalogFile>(json, Options);
		return new GameCatalog(file?.Games ?? []) { Version = file?.Version.ToString(), Updated = file?.Updated };
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
