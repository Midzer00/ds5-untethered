// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Kopru.Core.Games;

namespace Kopru.DetectStudy;

// One Windows launch option of a Steam app: the exe path relative to the install folder, as Steam's appinfo lists it.
// BetaKey: the branches the option is limited to (null: every branch).
internal sealed record Launch(string Executable, string? Arguments, string? Type, string? Description, string? BetaKey = null);

// Public Steam metadata for one catalog game: the app id it resolved to, Steam's name, install folder and launch options.
internal sealed record Truth(CatalogGame Game, long AppId, string AppIdSource, string? SteamName, string? InstallDir, IReadOnlyList<Launch> Launches);

// Downloads (once, into the cache) and reads public Steam metadata: api.steamcmd.net (appinfo: config.installdir,
// config.launch) and the store search API (name -> app id for catalog games without an app id). Sends only game names
// and app ids. Sequential, with a delay between requests.
internal sealed class GroundTruth(string cacheDir, bool offline)
{
	private static readonly HttpClient Http = CreateHttp();
	private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(1200);
	private DateTime last = DateTime.MinValue;

	public List<string> Problems { get; } = [];

	private static HttpClient CreateHttp()
	{
		var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		http.DefaultRequestHeaders.UserAgent.ParseAdd("detect-study/1.0");
		return http;
	}

	public List<Truth> Resolve(GameCatalog catalog)
	{
		var list = new List<Truth>();
		foreach (CatalogGame game in catalog.Games)
		{
			long? appId = game.SteamAppId;
			string source = "catalog";
			if (appId == null)
			{
				appId = Search(game.Name);
				source = "store search";
				if (appId == null)
				{
					Problems.Add($"{game.Name}: no catalog app id and no exact store search match");
					continue;
				}
			}
			JsonElement? info = AppInfo(appId.Value);
			if (info is not { } app)
			{
				Problems.Add($"{game.Name}: appinfo for {appId} unavailable");
				continue;
			}
			string? name = Get(app, "common", "name");
			string? dir = Get(app, "config", "installdir");
			// Launch options for a beta branch only are used when the app has no other Windows option (some apps list their
			// default branch as a beta key: "betakey" "default, ...").
			var launches = new List<Launch>();
			var betaOnly = new List<Launch>();
			if (Child(app, "config", "launch") is { ValueKind: JsonValueKind.Object } launch)
			{
				foreach (JsonProperty entry in launch.EnumerateObject())
				{
					JsonElement e = entry.Value;
					string? exe = Get(e, "executable");
					string? os = Get(e, "config", "oslist");
					string? beta = Get(e, "config", "betakey");
					if (string.IsNullOrWhiteSpace(exe) || (os != null && !os.Contains("windows", StringComparison.OrdinalIgnoreCase)))
					{
						continue;
					}
					var item = new Launch(exe.Trim().Replace('/', '\\').TrimStart('.', '\\'), Get(e, "arguments"), Get(e, "type"), Get(e, "description"), beta);
					(beta == null ? launches : betaOnly).Add(item);
				}
			}
			if (launches.Count == 0)
			{
				launches = betaOnly;
			}
			list.Add(new Truth(game, appId.Value, source, name, dir, launches));
		}
		return list;
	}

	private long? Search(string name)
	{
		string file = Path.Combine(cacheDir, "search", GameCatalog.NormalizeName(name) + ".json");
		string? json = Cached(file, "https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=" + Uri.EscapeDataString(name));
		if (json == null)
		{
			return null;
		}
		using JsonDocument doc = JsonDocument.Parse(json);
		if (!doc.RootElement.TryGetProperty("items", out JsonElement items))
		{
			return null;
		}
		string want = GameCatalog.NormalizeName(name);
		foreach (JsonElement item in items.EnumerateArray())
		{
			if (GameCatalog.NormalizeName(item.GetProperty("name").GetString()) == want && item.TryGetProperty("id", out JsonElement id))
			{
				return id.GetInt64();
			}
		}
		return null;
	}

	private JsonElement? AppInfo(long appId)
	{
		string file = Path.Combine(cacheDir, "steamcmd", appId + ".json");
		string? json = Cached(file, "https://api.steamcmd.net/v1/info/" + appId);
		if (json == null)
		{
			return null;
		}
		using JsonDocument doc = JsonDocument.Parse(json);
		if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.TryGetProperty(appId.ToString(), out JsonElement app))
		{
			return app.Clone();
		}
		return null;
	}

	private string? Cached(string file, string url)
	{
		if (File.Exists(file))
		{
			return File.ReadAllText(file);
		}
		if (offline)
		{
			return null;
		}
		for (int attempt = 0; attempt < 3; attempt++)
		{
			TimeSpan wait = Delay - (DateTime.UtcNow - last);
			if (wait > TimeSpan.Zero)
			{
				Thread.Sleep(wait);
			}
			last = DateTime.UtcNow;
			try
			{
				using HttpResponseMessage response = Http.GetAsync(url).GetAwaiter().GetResult();
				if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
				{
					Thread.Sleep(TimeSpan.FromSeconds(20 * (attempt + 1)));
					continue;
				}
				if (!response.IsSuccessStatusCode)
				{
					Problems.Add($"{url}: HTTP {(int)response.StatusCode}");
					return null;
				}
				string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
				Directory.CreateDirectory(Path.GetDirectoryName(file)!);
				File.WriteAllText(file, json);
				Console.Error.WriteLine("fetched " + url);
				return json;
			}
			catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
			{
				Problems.Add($"{url}: {e.GetType().Name}");
			}
		}
		return null;
	}

	private static JsonElement? Child(JsonElement e, params string[] path)
	{
		foreach (string key in path)
		{
			if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out e))
			{
				return null;
			}
		}
		return e;
	}

	private static string? Get(JsonElement e, params string[] path) =>
		Child(e, path) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
}
