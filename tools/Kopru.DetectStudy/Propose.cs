// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kopru.Core.Games;

namespace Kopru.DetectStudy;

// Proposes catalog exe names from Steam's public launch options (the cached appinfo): every Windows launch exe of a
// catalog game that passes the filter is added to that entry (alongside its existing exe names). Writes
// supported-games.proposed.json (the embedded file with the additions, same order and format) and propose.md (what was
// added and what was rejected, with the reason). Nothing about this PC is read.
internal static class Propose
{
	// Names (or parts of names) of programs that start the game rather than being it: launchers and anti-cheat stubs.
	public static readonly string[] LauncherHints = ["launcher", "start_protected", "bootstrap", "easyanticheat", "eac", "prelauncher", "start.exe",
		"battleye", "anticheat", "be_launcher", "loader"];

	// Tools and extras that ship with a game: installers, redistributables, crash reporters, settings dialogs, art books.
	private static readonly string[] ToolHints = ["setup", "install", "redist", "dxsetup", "crash", "uninst", "settings", "config", "update", "patcher",
		"bonus", "extras", "artandmusic", "artbook", "soundtrack"];

	// Internal or test builds that some apps list for their own branches: not what players run.
	private static readonly string[] BuildHints = ["-test", "_test", "-dev", "_dev", "profile", "pgo-lto", "finaldm", "debug"];

	// Engine binaries several games of one studio or engine ship under the same name.
	private static readonly string[] EngineHints = ["stingray_", "forza_x64", "tortoise", "start_epic_crossplay"];

	// File names that unrelated programs (or Windows itself) use too, or that one launcher or series shares between games.
	public static readonly Dictionary<string, string> Generic = new(StringComparer.OrdinalIgnoreCase)
	{
		["game.exe"] = "generic", ["main.exe"] = "generic", ["start.exe"] = "generic", ["client.exe"] = "generic", ["app.exe"] = "generic",
		["play.exe"] = "generic", ["run.exe"] = "generic", ["setup.exe"] = "generic", ["update.exe"] = "generic", ["engine.exe"] = "generic",
		["player.exe"] = "generic", ["default.exe"] = "generic", ["win64.exe"] = "generic", ["x64.exe"] = "generic", ["shipping.exe"] = "generic",
		["release.exe"] = "generic", ["server.exe"] = "generic", ["editor.exe"] = "generic", ["startup.exe"] = "generic (Sega RGG titles and others)",
		["control.exe"] = "Windows Control Panel", ["system.exe"] = "generic", ["help.exe"] = "generic",
		["HYP.exe"] = "HoYoPlay launcher, shared by HoYoverse games", ["SeasunGame.exe"] = "publisher-wide name",
		["metro.exe"] = "shared by the Metro Redux games", ["TDF.exe"] = "shared by the yearly Tour de France games",
		["DeadzoneSteam.exe"] = "the sequel in the catalog (no Steam data yet) may use it too"
	};

	// Shortest accepted file name stem: a one- or two-letter exe name ("ds.exe") is too easy to share by chance.
	public const int MinStem = 3;

	// Launch option types that are not the game (Steam appinfo "type"; "none" only hides an option from the menu).
	private static readonly HashSet<string> ToolTypes = new(StringComparer.OrdinalIgnoreCase) { "config", "server", "editor", "tool" };

	public static bool IsLauncher(string exe) => LauncherHints.Any(h => Path.GetFileName(exe).Contains(h, StringComparison.OrdinalIgnoreCase));

	// Why a launch exe file name may not become a catalog exe name, or null when it may (the per-name rules only; the
	// sharing rules need the whole data set).
	public static string? NameRejection(string file)
	{
		string stem = Path.GetFileNameWithoutExtension(file);
		if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.Contains(':') || file.Contains('?'))
		{
			return "not an exe file";
		}
		if (IsLauncher(file))
		{
			return "launcher or anti-cheat stub";
		}
		if (ToolHints.Any(h => stem.Contains(h, StringComparison.OrdinalIgnoreCase)))
		{
			return "installer, settings, crash tool or extras";
		}
		if (BuildHints.Any(h => stem.Contains(h, StringComparison.OrdinalIgnoreCase)))
		{
			return "internal or test build";
		}
		if (EngineHints.Any(h => stem.Contains(h, StringComparison.OrdinalIgnoreCase)))
		{
			return "engine binary name shared between games";
		}
		if (Generic.TryGetValue(file, out string? why))
		{
			return "generic or shared name: " + why;
		}
		if (stem.Length < MinStem)
		{
			return "name shorter than 3 characters";
		}
		if (GameWatcher.Ignored.Contains(file))
		{
			return "in GameWatcher.Ignored";
		}
		return null;
	}

	private sealed record Candidate(CatalogGame Game, long AppId, string Exe, string? Rejected);

	public static string Report(GameCatalog catalog, List<Truth> truths, string outDir)
	{
		var usable = truths.Where(t => !string.IsNullOrWhiteSpace(t.InstallDir) && t.Launches.Count > 0).ToList();
		// Steam apps that launch each exe file name (from every catalog game's appinfo).
		var owners = usable.SelectMany(t => t.Launches.Select(l => (Exe: Path.GetFileName(l.Executable), t.AppId))).Distinct()
			.GroupBy(x => x.Exe, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(x => x.AppId).Distinct().Count(), StringComparer.OrdinalIgnoreCase);
		var candidates = new List<Candidate>();
		foreach (Truth t in usable)
		{
			// Some apps list every option, the public one included, under beta keys only; then none is left out for its branch.
			bool hasPublic = t.Launches.Any(l => IsPublicBranch(l.BetaKey));
			foreach (Launch l in t.Launches)
			{
				string file = Path.GetFileName(l.Executable);
				CatalogGame? owner = catalog.Find(file);
				string? why = owner == t.Game ? "already in the entry"
					: hasPublic && !IsPublicBranch(l.BetaKey) ? "beta or internal branch only"
					: l.Type != null && ToolTypes.Contains(l.Type) ? $"launch option type \"{l.Type}\""
					: NameRejection(file);
				if (why == null && owners[file] > 1)
				{
					why = $"launched by {owners[file]} Steam apps";
				}
				if (why == null && owner != null)
				{
					why = $"already an exe name of {owner.Name}";
				}
				if (why == null && YearSuffix.Match(t.Game.Name) is { Success: true } year
					&& GameCatalog.NormalizeName(Path.GetFileNameWithoutExtension(file)) == GameCatalog.NormalizeName(t.Game.Name[..year.Index]))
				{
					why = "remake named like the older game (the catalog name needs its year)";
				}
				candidates.Add(new Candidate(t.Game, t.AppId, file, why));
			}
		}
		// A name proposed for two entries (two entries on one Steam app) belongs to neither.
		var accepted = candidates.Where(c => c.Rejected == null).ToList();
		foreach (var shared in accepted.GroupBy(c => c.Exe, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(c => c.Game).Distinct().Count() > 1).ToList())
		{
			foreach (Candidate c in shared)
			{
				candidates[candidates.IndexOf(c)] = c with { Rejected = "proposed for more than one catalog entry" };
			}
		}
		accepted = candidates.Where(c => c.Rejected == null).GroupBy(c => (c.Game, Key: c.Exe.ToLowerInvariant())).Select(g => g.First()).ToList();
		var added = accepted.GroupBy(c => c.Game).ToDictionary(g => g.Key, g => g.Select(c => c.Exe).ToList());

		// Install folder names that differ from the game's name and pass InstallDirRejection.
		var dirOwners = usable.GroupBy(t => GameCatalog.NormalizeName(t.InstallDir), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(t => t.AppId).Distinct().Count(), StringComparer.Ordinal);
		var dirs = new Dictionary<CatalogGame, string>();
		var dirRejected = new List<(CatalogGame Game, string Dir, string Why)>();
		foreach (Truth t in usable)
		{
			string dir = t.InstallDir!.Trim();
			if (GameCatalog.NormalizeName(dir) == GameCatalog.NormalizeName(t.Game.Name))
			{
				continue;
			}
			string? why = InstallDirRejection(dir, catalog) ?? (dirOwners[GameCatalog.NormalizeName(dir)] > 1 ? "used by more than one Steam app" : null);
			if (why == null)
			{
				dirs[t.Game] = dir;
			}
			else
			{
				dirRejected.Add((t.Game, dir, why));
			}
		}

		// The proposed file: the embedded one in its own order, exe names appended, install folder names set.
		CatalogFile file0 = ReadEmbedded();
		var games = file0.Games.Select(g =>
		{
			CatalogGame? live = catalog.Games.FirstOrDefault(x => x.Name == g.Name);
			if (live != null && g.SteamInstallDir == null && dirs.TryGetValue(live, out string? dir))
			{
				g = g with { SteamInstallDir = dir };
			}
			if (live == null || !added.TryGetValue(live, out List<string>? more))
			{
				return g;
			}
			bool hadExe = g.Exe.Count > 0;
			return g with
			{
				Exe = g.Exe.Concat(more).ToList(),
				ExeConfidence = g.ExeConfidence ?? "medium",
				Notes = hadExe ? AppendNote(g.Notes, "launch exe added from Steam's launch options")
					: g.Notes == "Added from PCGamingWiki; exe name unknown" ? "Added from PCGamingWiki; exe name from Steam's launch options"
					: AppendNote(g.Notes, "exe name from Steam's launch options")
			};
		}).ToList();
		File.WriteAllText(Path.Combine(outDir, "supported-games.proposed.json"), Write(file0 with { Games = games }));

		var rejected = candidates.Where(c => c.Rejected != null && !c.Rejected.StartsWith("already in", StringComparison.Ordinal)).ToList();
		var sb = new StringBuilder("# Proposed catalog exe names from Steam launch options\n\n");
		sb.AppendLine($"- catalog games with Steam launch data: {usable.Count}; launch options: {candidates.Count}");
		sb.AppendLine($"- entries gaining exe names: {added.Count} ({added.Keys.Count(g => g.Exe.Count == 0)} had none, {added.Keys.Count(g => g.Exe.Count > 0)} had some); names added: {added.Values.Sum(v => v.Count)}");
		sb.AppendLine($"- entries without exe names before: {catalog.Games.Count(g => g.Exe.Count == 0)}; after: {games.Count(g => g.Exe.Count == 0)}");
		sb.AppendLine($"- launch exes already in their entry: {candidates.Count(c => c.Rejected == "already in the entry")}");
		sb.AppendLine($"- candidate names rejected: {rejected.Count} ({rejected.Select(c => c.Exe.ToLowerInvariant()).Distinct().Count()} distinct)");
		sb.AppendLine("\n## Rejected by reason\n");
		foreach (var g in rejected.GroupBy(c => c.Rejected!.StartsWith("launched by", StringComparison.Ordinal) ? "launched by more than one Steam app"
			: c.Rejected!.StartsWith("already an exe", StringComparison.Ordinal) ? "already another entry's exe name" : c.Rejected!).OrderByDescending(g => g.Count()))
		{
			sb.AppendLine($"- {g.Key}: {g.Count()} ({string.Join(", ", g.GroupBy(c => c.Exe, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Count() > 1 ? $"{x.Key} x{x.Count()}" : x.Key))})");
		}
		sb.AppendLine("\n## Rejected names, most frequent\n");
		foreach (var g in rejected.GroupBy(c => c.Exe, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Take(20))
		{
			sb.AppendLine($"- {g.Key}: {g.Count()} ({g.First().Rejected})");
		}
		sb.AppendLine("\n## Added\n");
		foreach (var (game, list) in added.OrderBy(p => p.Key.Name, StringComparer.OrdinalIgnoreCase))
		{
			sb.AppendLine($"- {Sim.Md(game.Name)}: {string.Join(", ", list.Select(e => $"`{e}`"))}{(game.Exe.Count > 0 ? $" (alongside `{string.Join(", ", game.Exe)}`)" : "")}");
		}
		sb.AppendLine($"\n## Steam install folder names (differing from the game's name): {dirs.Count} set, {dirRejected.Count} rejected\n");
		foreach (var (game, dir) in dirs.OrderBy(p => p.Key.Name, StringComparer.OrdinalIgnoreCase))
		{
			sb.AppendLine($"- {Sim.Md(game.Name)}: \"{Sim.Md(dir)}\"");
		}
		sb.AppendLine("\nRejected:\n");
		foreach (var (game, dir, why) in dirRejected.OrderBy(r => r.Why).ThenBy(r => r.Game.Name, StringComparer.OrdinalIgnoreCase))
		{
			sb.AppendLine($"- {Sim.Md(game.Name)}: \"{Sim.Md(dir)}\" ({why})");
		}
		sb.AppendLine("\n## Entries with exe names whose Steam launch exe was rejected (catalog exe kept)\n");
		foreach (var g in rejected.Where(c => c.Game.Exe.Count > 0).GroupBy(c => c.Game))
		{
			sb.AppendLine($"- {Sim.Md(g.Key.Name)}: {string.Join(", ", g.Select(c => $"`{c.Exe}` ({c.Rejected})"))}; catalog `{string.Join(", ", g.Key.Exe)}`");
		}
		return sb.ToString();
	}

	private static readonly System.Text.RegularExpressions.Regex YearSuffix = new(@"\s*\((19|20)\d\d\)\s*$");

	// A plain single word ("Sparta", "Season", "Sackboy"): too likely to be an unrelated folder of that name.
	private static readonly System.Text.RegularExpressions.Regex PlainWord = new(@"^[A-Z]?[a-z]+$");

	// Folder names every install path has.
	public static readonly HashSet<string> CommonFolders = new(StringComparer.Ordinal)
	{
		"programfiles", "programfilesx86", "steamapps", "common", "steamlibrary", "binaries", "win64", "win32", "x64", "bin", "game",
		"games", "epicgames", "windowsapps", "xboxgames", "windows", "system32", "content", "client", "launcher", "data", "runtime"
	};

	// Why a Steam install folder name may not be stored for an entry, or null when it may (sharing between apps is
	// checked by the caller).
	public static string? InstallDirRejection(string dir, GameCatalog catalog)
	{
		string key = GameCatalog.NormalizeName(dir);
		if (key.Length < GameCatalog.MinInstallDirLength)
		{
			return "shorter than 3 characters";
		}
		if (PlainWord.IsMatch(dir))
		{
			return "a plain single word";
		}
		if (CommonFolders.Contains(key))
		{
			return "a common install folder name";
		}
		if (catalog.FindByName(dir) is { } other)
		{
			return $"the name of {other.Name}";
		}
		if (catalog.Games.FirstOrDefault(g => YearSuffix.Match(g.Name) is { Success: true } m && GameCatalog.NormalizeName(g.Name[..m.Index]) == key) is { } dated)
		{
			return $"the name of {dated.Name} without its year (an older game of that name installs there)";
		}
		return null;
	}

	// An option for every branch, or one whose branch list includes the public "default" branch.
	private static bool IsPublicBranch(string? betaKey) =>
		betaKey == null || betaKey.Split(',', StringSplitOptions.TrimEntries).Contains("default", StringComparer.OrdinalIgnoreCase);

	private static string? AppendNote(string? notes, string text) => string.IsNullOrEmpty(notes) ? text : notes + "; " + text;

	internal sealed record CatalogFile(int Version, string? Updated, List<CatalogGame> Games);

	private static readonly JsonSerializerOptions Json = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	public static CatalogFile ReadEmbedded()
	{
		using Stream stream = typeof(GameCatalog).Assembly.GetManifestResourceStream(GameCatalog.ResourceName)!;
		using var reader = new StreamReader(stream);
		return JsonSerializer.Deserialize<CatalogFile>(reader.ReadToEnd(), Json)!;
	}

	// The catalog file format: two-space indent, CRLF, a trailing newline.
	public static string Write(CatalogFile file) => JsonSerializer.Serialize(file, Json).Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
}
