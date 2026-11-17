// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.RegularExpressions;
using Kopru.Core.Games;

namespace Kopru.DetectStudy;

// Makes src/Kopru.Core/Games/playstation-support.json: every Steam game whose store page lists "DualSense Controller
// Support" (categories 57 and 58) or "DualShock Controller Support" (55 and 56), from the public store search
// (store.steampowered.com/search/results/?category1=998&category2=<id>&infinite=1: games only, 100 per page). Sequential,
// at least 1.2 s between requests; sends nothing but the category and the page offset. Pages are cached per day in
// <cache>\playstation\<date>\, so an interrupted run resumes. Fails when a category returns clearly fewer games than the
// search reports (the enumeration no longer works): nothing is written then.
internal static partial class PlayStationSearch
{
	private const int PageSize = 100;
	private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(1200);
	private static readonly HttpClient Http = CreateHttp();
	private static DateTime last = DateTime.MinValue;

	// 57/58: DualSense (USB / Bluetooth); 55/56: DualShock 4 (USB / Bluetooth).
	private static readonly int[] DualSenseCategories = [57, 58];
	private static readonly int[] DualShockCategories = [55, 56];

	private static HttpClient CreateHttp()
	{
		var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
		http.DefaultRequestHeaders.UserAgent.ParseAdd("ds5-untethered-build/1.0");
		return http;
	}

	[GeneratedRegex("data-ds-appid=\"(\\d+)\"")]
	private static partial Regex AppIdPattern();

	// Writes the file to outFile; returns a short report, or throws when the search did not enumerate.
	public static string Run(string cacheDir, string outFile, bool offline)
	{
		string date = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
		string dir = Path.Combine(cacheDir, "playstation", date);
		var report = new List<string>();
		var ds = new HashSet<long>();
		var ds4 = new HashSet<long>();
		foreach (int category in DualSenseCategories)
		{
			ds.UnionWith(Category(category, dir, offline, report));
		}
		foreach (int category in DualShockCategories)
		{
			ds4.UnionWith(Category(category, dir, offline, report));
		}
		ds4.ExceptWith(ds);
		string source = "Steam store search, games with categories 57/58 (DualSense) or 55/56 (DualShock 4) only";
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
		File.WriteAllText(outFile, PlayStationSupport.Format(date, source, ds, ds4));
		report.Add($"wrote {outFile}: ds {ds.Count}, ds4 {ds4.Count}");
		return string.Join(Environment.NewLine, report);
	}

	private static HashSet<long> Category(int category, string dir, bool offline, List<string> report)
	{
		var ids = new HashSet<long>();
		int total = -1;
		for (int start = 0; total < 0 || start < total; start += PageSize)
		{
			string file = Path.Combine(dir, $"{category}-{start}.json");
			string url = $"https://store.steampowered.com/search/results/?query&start={start}&count={PageSize}&category1=998&category2={category}" +
				"&infinite=1&ndl=1&sort_by=Name_ASC&cc=US&l=english";
			string json = Cached(file, url, offline) ?? throw new InvalidOperationException($"category {category}: page at {start} unavailable");
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;
			if (!root.TryGetProperty("total_count", out JsonElement count) || !root.TryGetProperty("results_html", out JsonElement html))
			{
				throw new InvalidOperationException($"category {category}: unexpected answer at {start}");
			}
			total = count.GetInt32();
			int before = ids.Count;
			int rows = 0;
			foreach (Match m in AppIdPattern().Matches(html.GetString() ?? string.Empty))
			{
				rows++;
				ids.Add(long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
			}
			if (rows == 0)
			{
				break;
			}
			Console.Error.WriteLine($"category {category}: {ids.Count} of {total} (+{ids.Count - before})");
		}
		// The search order can shift a little while paging; a large gap means the enumeration is broken.
		if (total <= 0 || ids.Count < total * 0.97)
		{
			throw new InvalidOperationException($"category {category}: only {ids.Count} games of {total} enumerated");
		}
		report.Add($"category {category}: {ids.Count} games (search reports {total})");
		return ids;
	}

	private static string? Cached(string file, string url, bool offline)
	{
		if (File.Exists(file))
		{
			return File.ReadAllText(file);
		}
		if (offline)
		{
			return null;
		}
		for (int attempt = 0; attempt < 4; attempt++)
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
					Thread.Sleep(TimeSpan.FromSeconds(30 * (attempt + 1)));
					continue;
				}
				if (!response.IsSuccessStatusCode)
				{
					return null;
				}
				string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
				// The store sometimes answers 200 with an HTML "Site Error" page: wait and ask again, never cache it.
				if (!json.TrimStart().StartsWith('{'))
				{
					Thread.Sleep(TimeSpan.FromSeconds(30 * (attempt + 1)));
					continue;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(file)!);
				File.WriteAllText(file, json);
				return json;
			}
			catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
			{
				Thread.Sleep(TimeSpan.FromSeconds(10 * (attempt + 1)));
			}
		}
		return null;
	}
}
