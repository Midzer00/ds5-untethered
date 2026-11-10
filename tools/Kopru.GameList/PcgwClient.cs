// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kopru.GameList;

// Reads PCGamingWiki's two DualSense lists. Order of attempts: the Cargo API (action=cargoquery on Game + Input, fields
// Input.DualSense_haptic_feedback_support and Input.DualSense_adaptive_trigger_support; anonymous use is refused with
// "permissiondenied" as of 2026-11), then the rendered list pages through the MediaWiki parse API (the pages are
// Cargo queries themselves), or, with fromArchive, the Internet Archive's latest snapshots of those pages.
public sealed partial class PcgwClient(HttpClient http, TextWriter log)
{
	public const string Api = "https://www.pcgamingwiki.com/w/api.php";
	public const string HapticsPage = "List_of_games_that_support_Dualsense_haptic_feedback";
	public const string TriggersPage = "List_of_games_that_support_PlayStation_adaptive_triggers";
	public const string UserAgent = "DS5UntetheredGameList/1.0 (supported games list of DS5 Untethered)";
	// Values of the two Input fields that the list pages count as support.
	public static readonly string[] Listed = ["true", "limited", "hackable"];

	public static HttpClient CreateHttpClient()
	{
		var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
		return client;
	}

	public string Method { get; private set; } = string.Empty;

	public async Task<IReadOnlyList<WikiGame>> LoadAsync(bool fromArchive)
	{
		if (!fromArchive)
		{
			IReadOnlyList<WikiGame>? cargo = await TryCargoAsync();
			if (cargo != null)
			{
				Method = "PCGamingWiki Cargo API (action=cargoquery)";
				return cargo;
			}
			List<string> haptics = ParseListTable(await ParsedPageAsync(HapticsPage));
			List<string> triggers = ParseListTable(await ParsedPageAsync(TriggersPage));
			Method = "PCGamingWiki MediaWiki API (action=parse of the two list pages)";
			return GameListMerger.Combine(haptics, triggers, await SteamIdsAsync(haptics.Concat(triggers).Distinct().ToList()));
		}
		List<string> archivedHaptics = ParseListTable(await GetAsync(ArchiveUrl(HapticsPage)));
		List<string> archivedTriggers = ParseListTable(await GetAsync(ArchiveUrl(TriggersPage)));
		Method = "Internet Archive snapshots of the two PCGamingWiki list pages";
		return GameListMerger.Combine(archivedHaptics, archivedTriggers);
	}

	public static string ArchiveUrl(string page) => "https://web.archive.org/web/2026/https://www.pcgamingwiki.com/wiki/" + page;

	// Game names of the first sortable table of a list page (the main list; later tables repeat it per controller model).
	public static List<string> ParseListTable(string html)
	{
		int start = html.IndexOf("wikitable sortable", StringComparison.Ordinal);
		if (start < 0)
		{
			throw new InvalidDataException("No game table on the list page.");
		}
		int end = html.IndexOf("</table>", start, StringComparison.Ordinal);
		string table = html[start..(end < 0 ? html.Length : end)];
		var names = new List<string>();
		foreach (Match row in RowPattern().Matches(table))
		{
			Match title = TitlePattern().Match(row.Groups[1].Value);
			if (title.Success)
			{
				string name = WebUtility.HtmlDecode(title.Groups[1].Value).Trim();
				if (name.Length > 0 && !names.Contains(name))
				{
					names.Add(name);
				}
			}
		}
		return names;
	}

	// The "steam appid" of each game page's infobox (first id when several), read 50 pages per request.
	public async Task<Dictionary<string, long>> SteamIdsAsync(IReadOnlyList<string> pages)
	{
		var ids = new Dictionary<string, long>();
		for (int i = 0; i < pages.Count; i += 50)
		{
			string titles = string.Join("|", pages.Skip(i).Take(50));
			string json = await GetAsync($"{Api}?action=query&prop=revisions&rvprop=content&rvslots=main&redirects=1&format=json&formatversion=2&titles={Uri.EscapeDataString(titles)}");
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement query = doc.RootElement.GetProperty("query");
			var redirects = new Dictionary<string, string>();
			foreach (string key in new[] { "normalized", "redirects" })
			{
				if (query.TryGetProperty(key, out JsonElement list))
				{
					foreach (JsonElement r in list.EnumerateArray())
					{
						redirects[r.GetProperty("to").GetString()!] = r.GetProperty("from").GetString()!;
					}
				}
			}
			foreach (JsonElement page in query.GetProperty("pages").EnumerateArray())
			{
				if (!page.TryGetProperty("revisions", out JsonElement revisions))
				{
					continue;
				}
				string text = revisions[0].GetProperty("slots").GetProperty("main").GetProperty("content").GetString() ?? string.Empty;
				Match id = SteamIdPattern().Match(text);
				string title = page.GetProperty("title").GetString()!;
				while (redirects.TryGetValue(title, out string? from))
				{
					title = from;
				}
				if (id.Success && long.TryParse(id.Groups[1].Value, out long appId))
				{
					ids[title] = appId;
				}
			}
			await Task.Delay(500);
		}
		return ids;
	}

	private async Task<IReadOnlyList<WikiGame>?> TryCargoAsync()
	{
		try
		{
			var haptics = await CargoAsync("Input.DualSense_haptic_feedback_support");
			var triggers = await CargoAsync("Input.DualSense_adaptive_trigger_support");
			if (haptics == null || triggers == null)
			{
				return null;
			}
			var ids = haptics.Concat(triggers).Where(r => r.SteamAppId != null).GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First().SteamAppId!.Value);
			return GameListMerger.Combine(haptics.Select(r => r.Name), triggers.Select(r => r.Name), ids);
		}
		catch (HttpRequestException e)
		{
			log.WriteLine($"Cargo API unavailable: {e.Message}");
			return null;
		}
	}

	private async Task<List<(string Name, long? SteamAppId)>?> CargoAsync(string field)
	{
		string where = string.Join(" OR ", Listed.Select(v => $"{field}='{v}'"));
		string url = $"{Api}?action=cargoquery&format=json&limit=500&tables=Infobox_game,Input&join_on=Infobox_game._pageID=Input._pageID"
			+ $"&fields=Infobox_game._pageName=Page,Infobox_game.Steam_AppID=Steam&where={Uri.EscapeDataString(where)}";
		using JsonDocument doc = JsonDocument.Parse(await GetAsync(url));
		if (doc.RootElement.TryGetProperty("error", out JsonElement error))
		{
			log.WriteLine($"Cargo API refused: {error.GetProperty("code").GetString()} ({error.GetProperty("info").GetString()})");
			return null;
		}
		var rows = new List<(string, long?)>();
		foreach (JsonElement item in doc.RootElement.GetProperty("cargoquery").EnumerateArray())
		{
			JsonElement title = item.GetProperty("title");
			string name = WebUtility.HtmlDecode(title.GetProperty("Page").GetString() ?? string.Empty);
			string steam = title.TryGetProperty("Steam", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty;
			rows.Add((name, long.TryParse(steam.Split(',')[0].Trim(), out long id) ? id : null));
		}
		return rows;
	}

	private async Task<string> ParsedPageAsync(string page)
	{
		string json = await GetAsync($"{Api}?action=parse&prop=text&format=json&formatversion=2&page={page}");
		using JsonDocument doc = JsonDocument.Parse(json);
		if (doc.RootElement.TryGetProperty("error", out JsonElement error))
		{
			throw new HttpRequestException($"parse refused: {error.GetProperty("code").GetString()}");
		}
		return doc.RootElement.GetProperty("parse").GetProperty("text").GetString() ?? string.Empty;
	}

	private async Task<string> GetAsync(string url)
	{
		log.WriteLine($"GET {url}");
		using HttpResponseMessage response = await http.GetAsync(url);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsStringAsync();
	}

	[GeneratedRegex(@"<tr>(.*?)</tr>", RegexOptions.Singleline)]
	private static partial Regex RowPattern();

	[GeneratedRegex(@"<td>\s*<a [^>]*title=""([^""]*)""")]
	private static partial Regex TitlePattern();

	[GeneratedRegex(@"\|\s*steam appid\s*=\s*(\d+)", RegexOptions.IgnoreCase)]
	private static partial Regex SteamIdPattern();
}
