// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kopru.Core.Games;
using Kopru.Core.Steam;

namespace Kopru.DetectStudy;

// Part C: the games installed on this PC (InstalledGameFinder and every Steam install), their real exe files and version
// info read from disk (nothing is run), through the real lookup: v1 at their real path with the real Steam library, v2 the
// same files as if copied to D:\Games\<folder> (no Steam). The output names local games: it stays in the out dir.
internal static class Local
{
	// Folders that hold redistributables and tools, not the game.
	public static readonly string[] SkipDirs = ["_CommonRedist", "CommonRedist", "Redist", "Redistributables", "DirectX", "vcredist", "Support", "Installers",
		"EasyAntiCheat", "BattlEye", "__Installer", "ThirdParty", "Prerequisites", "dotNetFx"];

	public static string Report(GameCatalog catalog, string cacheDir)
	{
		var steam = SteamLibrary.CreateDefault();
		IReadOnlyList<(string Folder, long AppId)> steamInstalls = steam.ReadNow();
		InstalledScan scan = InstalledGameFinder.CreateDefault().Scan(catalog);
		var places = new Dictionary<string, (CatalogGame? Expected, string Source, long? AppId)>(StringComparer.OrdinalIgnoreCase);
		foreach ((string folder, long appId) in steamInstalls)
		{
			places[folder] = (catalog.FindBySteamAppId(appId), "Steam", appId);
		}
		foreach (InstalledGame g in scan.Games.Where(g => g.Folder != null))
		{
			if (!places.ContainsKey(g.Folder!))
			{
				places[g.Folder!] = (g.Game, g.Source.ToString(), null);
			}
		}
		var details = new FixedDetails(version: Version);
		var sb = new StringBuilder("# Part C: games installed on this PC\n\n");
		sb.AppendLine($"- InstalledGameFinder: {scan.Games.Count} catalog games ({string.Join(", ", scan.Games.Select(g => $"{g.Game.Name} [{g.Source}]"))})");
		sb.AppendLine($"- Steam installs (any game): {steamInstalls.Count}");
		sb.AppendLine("\n| folder | expected | source | exes | main exe | v1 main | v2 main | v1 any-exe ok | v2 any-exe ok | wrong matches |");
		sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
		foreach (var (folder, info) in places.OrderBy(p => p.Key))
		{
			if (!Directory.Exists(folder))
			{
				continue;
			}
			List<string> exes = Exes(folder, 4);
			string? main = MainExe(folder, info.AppId, cacheDir, exes);
			string name = Path.GetFileName(folder.TrimEnd('\\'));
			int okV1 = 0, okV2 = 0;
			var wrong = new List<string>();
			string v1Main = "-", v2Main = "-";
			foreach (string exe in exes)
			{
				string rel = Path.GetRelativePath(folder, exe);
				Detection v1 = Sim.Detect(catalog, steam, details, Path.GetFileName(exe), exe);
				string copy = Path.Combine(@"D:\Games", name, rel);
				Detection v2 = Sim.Detect(catalog, null, new FixedDetails(version: _ => Version(exe)), Path.GetFileName(exe), copy);
				foreach (Detection d in new[] { v1, v2 })
				{
					if (d.Game != null && d.Game != info.Expected)
					{
						wrong.Add($"{Path.GetFileName(exe)} -> {d.Game.Name} by {d.Signal}");
					}
				}
				okV1 += v1.Game != null && v1.Game == info.Expected ? 1 : 0;
				okV2 += v2.Game != null && v2.Game == info.Expected ? 1 : 0;
				if (string.Equals(exe, main, StringComparison.OrdinalIgnoreCase))
				{
					v1Main = v1.Game == null ? "miss" : v1.Game == info.Expected ? v1.Signal! : "WRONG " + v1.Game.Name;
					v2Main = v2.Game == null ? "miss" : v2.Game == info.Expected ? v2.Signal! : "WRONG " + v2.Game.Name;
				}
			}
			sb.AppendLine($"| {Sim.Md(name)} | {Sim.Md(info.Expected?.Name ?? "(not in catalog)")} | {info.Source} | {exes.Count} | {Sim.Md(main == null ? "-" : Path.GetRelativePath(folder, main))} | {v1Main} | {v2Main} | {okV1} | {okV2} | {Sim.Md(string.Join("; ", wrong.Distinct()))} |");
		}
		sb.AppendLine("\nmain exe: Steam's launch exe from the cached public appinfo when the app was fetched for part B, else the largest exe (a guess).");
		return sb.ToString();
	}

	public static (string?, string?) Version(string path)
	{
		try
		{
			FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
			return (info.ProductName, info.FileDescription);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return (null, null);
		}
	}

	public static List<string> Exes(string folder, int depth)
	{
		var list = new List<string>();
		try
		{
			list.AddRange(Directory.EnumerateFiles(folder, "*.exe"));
			if (depth > 0)
			{
				foreach (string sub in Directory.EnumerateDirectories(folder))
				{
					if (!SkipDirs.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
					{
						list.AddRange(Exes(sub, depth - 1));
					}
				}
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// Unreadable: skipped.
		}
		return list;
	}

	private static string? MainExe(string folder, long? appId, string cacheDir, List<string> exes)
	{
		string cache = Path.Combine(cacheDir, "steamcmd", appId + ".json");
		if (appId != null && File.Exists(cache))
		{
			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(cache));
			if (doc.RootElement.TryGetProperty("data", out JsonElement data) && data.TryGetProperty(appId.ToString()!, out JsonElement app)
				&& app.TryGetProperty("config", out JsonElement config) && config.TryGetProperty("launch", out JsonElement launch) && launch.ValueKind == JsonValueKind.Object)
			{
				foreach (JsonProperty entry in launch.EnumerateObject())
				{
					if (entry.Value.TryGetProperty("executable", out JsonElement exe) && exe.GetString() is { Length: > 0 } rel)
					{
						string full = Path.Combine(folder, rel.Replace('/', '\\').TrimStart('.', '\\'));
						if (File.Exists(full))
						{
							return exes.FirstOrDefault(e => string.Equals(e, full, StringComparison.OrdinalIgnoreCase)) ?? full;
						}
					}
				}
			}
		}
		return exes.OrderByDescending(e => new FileInfo(e).Length).FirstOrDefault();
	}
}
