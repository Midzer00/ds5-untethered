// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Kopru.Core.Games;
using Kopru.Core.Steam;

namespace Kopru.DetectStudy;

// Part B: each catalog game with public Steam metadata, simulated as a process at its launch exe:
//  v1  <Steam library>\steamapps\common\<installdir>\<exe> with the game's appmanifest in that library;
//  v2  D:\Games\<installdir>\<exe>, no Steam library, no version info, no window title (folder and exe name only);
//  v2t like v2 but the window title is Steam's store name (an optimistic assumption, not measured).
internal static class Recall
{
	private static readonly string[] LauncherHints = ["launcher", "start_protected", "bootstrap", "easyanticheat", "eac", "prelauncher", "start.exe", "battleye"];

	public static string Report(GameCatalog catalog, List<Truth> truths, List<string> problems, string outDir)
	{
		var usable = truths.Where(t => !string.IsNullOrWhiteSpace(t.InstallDir) && t.Launches.Count > 0).ToList();
		var env = new FakeSteam();
		foreach (Truth t in usable)
		{
			env.Add(t.AppId, t.InstallDir!);
		}
		var steam = new SteamLibrary(env);
		var none = new FixedDetails();

		// What-if catalogs (not product behaviour): (h1) Steam's launch exe names added to entries without exe names;
		// (h2) every entry also searchable by name, with or without exe names.
		var h1 = new GameCatalog(catalog.Games.Select(g => g.Exe.Count > 0 ? g
			: g with { Exe = usable.Where(t => t.Game == g).SelectMany(t => t.Launches.Select(l => Path.GetFileName(l.Executable))).Distinct(StringComparer.OrdinalIgnoreCase).ToList() }));
		var nameOnly = new GameCatalog(catalog.Games.Select(g => g with { Exe = [] }));
		// (h1b) like h1 but only exe names that are not launcher-like and belong to one app only; (h3) h1b plus h2.
		var launchOwners = usable.SelectMany(t => t.Launches.Select(l => (Exe: Path.GetFileName(l.Executable), t.AppId))).Distinct()
			.GroupBy(x => x.Exe, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
		var h1b = new GameCatalog(catalog.Games.Select(g => g.Exe.Count > 0 ? g
			: g with { Exe = usable.Where(t => t.Game == g).SelectMany(t => t.Launches.Select(l => Path.GetFileName(l.Executable)))
				.Where(e => !IsLauncher(e) && launchOwners[e] == 1 && catalog.Find(e) == null).Distinct(StringComparer.OrdinalIgnoreCase).ToList() }));
		var h1bNames = new GameCatalog(h1b.Games.Select(g => g with { Exe = [] }));

		var csv = new StringBuilder(Sim.Csv("game", "appid", "appid_source", "steam_name", "name_matches", "installdir", "launch_exe", "primary",
			"catalog_exes", "v1", "v1_signal", "v2", "v2_signal", "v2t", "v2t_signal", "h1", "h2") + "\n");
		var rows = new List<Row>();
		foreach (Truth t in usable)
		{
			bool nameOk = NamesMatch(t.Game.Name, t.SteamName);
			for (int i = 0; i < t.Launches.Count; i++)
			{
				Launch l = t.Launches[i];
				string exe = Path.GetFileName(l.Executable);
				string p1 = Path.Combine(FakeSteam.Root, "steamapps", "common", t.InstallDir!, l.Executable);
				string p2 = Path.Combine(@"D:\Games", t.InstallDir!, l.Executable);
				Detection v1 = Sim.Detect(catalog, steam, none, exe, p1);
				Detection v2 = Sim.Detect(catalog, null, none, exe, p2);
				Detection v2t = Sim.Detect(catalog, null, new FixedDetails(_ => t.SteamName), exe, p2);
				Detection hx1 = Sim.Detect(h1, null, none, exe, p2);
				Detection hx2 = catalog.Find(exe) is { } byExe ? new Detection(byExe, "exe name")
					: GameWatcher.Folders(p2).Select(nameOnly.FindByName).FirstOrDefault(g => g != null) is { } byFolder ? new Detection(catalog.Games.First(g => g.Name == byFolder.Name), "folder name") : default;
				Detection hb = Sim.Detect(h1b, null, none, exe, p2);
				Detection hx3 = h1b.Find(exe) is { } byExe3 ? new Detection(byExe3, "exe name")
					: GameWatcher.Folders(p2).Select(h1bNames.FindByName).FirstOrDefault(g => g != null) is { } byFolder3 ? new Detection(catalog.Games.First(g => g.Name == byFolder3.Name), "folder name") : default;
				var row = new Row(t, l, i == 0, nameOk, v1, v2, v2t, hx1, hx2, hb, hx3);
				rows.Add(row);
				csv.AppendLine(Sim.Csv(t.Game.Name, t.AppId, t.AppIdSource, t.SteamName, nameOk, t.InstallDir, l.Executable, i == 0, string.Join(";", t.Game.Exe),
					row.Outcome(v1), v1.Signal, row.Outcome(v2), v2.Signal, row.Outcome(v2t), v2t.Signal, row.Outcome(hx1), row.Outcome(hx2)));
			}
		}
		File.WriteAllText(Path.Combine(outDir, "recall.csv"), csv.ToString());

		var primary = rows.Where(r => r.Primary).ToList();
		var sb = new StringBuilder("# Part B: simulated recall against public Steam metadata\n\n");
		sb.AppendLine($"- catalog entries: {catalog.Games.Count}");
		sb.AppendLine($"- resolved to a Steam app with appinfo: {truths.Count}; with an install folder and at least one Windows launch exe (ground truth): {usable.Count}");
		sb.AppendLine($"- launch options simulated: {rows.Count} (primary = the first Windows launch option)");
		sb.AppendLine($"- Steam store name differs from catalog name (possible wrong app id; checked by hand below): {usable.Count(t => !NamesMatch(t.Game.Name, t.SteamName))}");
		sb.AppendLine("\n## Recall (primary launch exe; a game counts when the right catalog entry is returned)\n");
		sb.AppendLine("| variant | recognised | wrong game | missed | by exe name | by Steam folder | by folder name | by install folder name | by product/description | by window title |");
		sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
		void Line(string name, Func<Row, Detection> pick)
		{
			int ok = primary.Count(r => r.Outcome(pick(r)) == "ok"), wrong = primary.Count(r => r.Outcome(pick(r)) == "wrong");
			int Sig(string s) => primary.Count(r => r.Outcome(pick(r)) == "ok" && pick(r).Signal == s);
			sb.AppendLine($"| {name} | {ok}/{primary.Count} ({100.0 * ok / primary.Count:F1}%) | {wrong} | {primary.Count - ok - wrong} | {Sig("exe name")} | {Sig("Steam library folder")} | {Sig("folder name")} | {Sig("install folder name")} | {Sig("product name") + Sig("file description")} | {Sig("window title")} |");
		}
		Line("v1 Steam library", r => r.V1);
		Line("v2 non-Steam folder, folder+exe only", r => r.V2);
		Line("v2t v2 + window title = Steam name (assumed)", r => r.V2t);
		Line("what-if h1: Steam launch exe added to no-exe entries (v2)", r => r.H1);
		Line("what-if h2: name fallback for every entry (v2)", r => r.H2);
		Line("what-if h1b: h1 without launcher-like or shared exe names (v2)", r => r.H1b);
		Line("what-if h3: h1b + h2 (v2)", r => r.H3);
		int anyV2 = usable.Count(t => rows.Any(r => r.T == t && r.Outcome(r.V2) == "ok"));
		sb.AppendLine($"\nv2 with any Windows launch option counted: {anyV2}/{usable.Count}.");

		sb.AppendLine("\n## v1 misses\n");
		foreach (Row r in primary.Where(r => r.Outcome(r.V1) != "ok"))
		{
			sb.AppendLine($"- {r.T.Game.Name}: {r.Outcome(r.V1)} {(r.V1.Game != null ? "-> " + r.V1.Game.Name : "")}; app id {r.T.AppId} ({r.T.AppIdSource}), Steam name \"{r.T.SteamName}\"");
		}

		sb.AppendLine("\n## v2 misses with cause (primary launch exe)\n");
		var groups = primary.Where(r => r.Outcome(r.V2) != "ok").GroupBy(r => Cause(r, catalog)).OrderByDescending(g => g.Count());
		foreach (var g in groups)
		{
			sb.AppendLine($"\n### {g.Key} ({g.Count()})\n");
			foreach (Row r in g)
			{
				sb.AppendLine($"- {Sim.Md(r.T.Game.Name)} (`{GameCatalog.NormalizeName(r.T.Game.Name)}`): installdir \"{Sim.Md(r.T.InstallDir)}\", launch `{Sim.Md(r.L.Executable)}`{(r.T.Game.Exe.Count > 0 ? $", catalog exe `{string.Join(", ", r.T.Game.Exe)}`" : "")}{(r.V2.Game != null ? $", matched {r.V2.Game.Name}" : "")}{(r.Outcome(r.V2t) == "ok" ? " [title would catch it]" : "")}");
			}
		}

		sb.AppendLine("\n## Steam name vs catalog name differences (app id check)\n");
		foreach (Truth t in usable.Where(t => !NamesMatch(t.Game.Name, t.SteamName)))
		{
			sb.AppendLine($"- {t.Game.Name} -> app {t.AppId} is \"{t.SteamName}\"");
		}

		sb.AppendLine("\n## Launch exe shapes (primary)\n");
		sb.AppendLine($"- nested in a sub-folder: {primary.Count(r => r.L.Executable.Contains('\\'))}");
		sb.AppendLine($"- in Binaries\\Win64 (Unreal layout): {primary.Count(r => r.L.Executable.Contains(@"Binaries\Win64", StringComparison.OrdinalIgnoreCase))}");
		sb.AppendLine($"- -Win64-Shipping.exe: {primary.Count(r => r.L.Executable.EndsWith(GameCatalog.ShippingSuffix, StringComparison.OrdinalIgnoreCase))}");
		sb.AppendLine($"- launcher-like name ({string.Join(", ", LauncherHints)}): {primary.Count(r => IsLauncher(r.L.Executable))} ({string.Join(", ", primary.Where(r => IsLauncher(r.L.Executable)).Select(r => $"{r.T.Game.Name}: {Path.GetFileName(r.L.Executable)}"))})");
		sb.AppendLine($"- games with more than one Windows launch option: {usable.Count(t => t.Launches.Count > 1)}");
		var exeGames = primary.Where(r => r.T.Game.Exe.Count > 0).ToList();
		sb.AppendLine($"- catalog entries with exe names: {exeGames.Count} with ground truth; catalog exe equals a Steam launch exe (or its Unreal pair): {exeGames.Count(r => r.T.Launches.Any(l => catalog.Find(Path.GetFileName(l.Executable)) == r.T.Game))}");

		sb.AppendLine("\n## Resolution problems\n");
		sb.AppendLine(problems.Count == 0 ? "- none" : string.Join("\n", problems.Select(p => "- " + p)));
		foreach (Truth t in truths.Where(t => string.IsNullOrWhiteSpace(t.InstallDir) || t.Launches.Count == 0))
		{
			sb.AppendLine($"- {t.Game.Name} (app {t.AppId}): appinfo has {(string.IsNullOrWhiteSpace(t.InstallDir) ? "no installdir" : "no Windows launch exe")}");
		}
		return sb.ToString();
	}

	private static bool IsLauncher(string exe) => LauncherHints.Any(h => Path.GetFileName(exe).Contains(h, StringComparison.OrdinalIgnoreCase));

	public static bool NamesMatch(string a, string? b)
	{
		string x = GameCatalog.NormalizeName(a), y = GameCatalog.NormalizeName(b);
		return y.Length > 0 && (x == y || x.Contains(y) || y.Contains(x));
	}

	// Why v2 missed (from the data only; folder names compared like FindByName).
	private static string Cause(Row r, GameCatalog catalog)
	{
		string name = GameCatalog.NormalizeName(r.T.Game.Name);
		if (r.V2.Game != null)
		{
			return "matched another catalog game";
		}
		// Both lookups missed: the launch exe is not a catalog exe, and no folder carries the name or a stored install folder name.
		string file = Path.GetFileName(r.L.Executable);
		string exeWhy = Propose.NameRejection(file) is { } rejected ? $"launch exe not in the catalog ({rejected})" : "launch exe not in the catalog";
		return exeWhy + "; " + FolderCause(r, name, catalog);
	}

	private static string FolderCause(Row r, string name, GameCatalog catalog)
	{
		if (Propose.InstallDirRejection(r.T.InstallDir!, catalog) is { } dirWhy && GameCatalog.NormalizeName(r.T.InstallDir) != name)
		{
			return $"install folder \"{r.T.InstallDir}\" not stored: {dirWhy}";
		}
		if (name.Length < GameCatalog.MinNameLength)
		{
			return "name shorter than MinNameLength";
		}
		var folders = GameWatcher.Folders(Path.Combine(@"D:\Games", r.T.InstallDir!, r.L.Executable)).Select(GameCatalog.NormalizeName).Where(f => f.Length > 0).ToList();
		string dir = GameCatalog.NormalizeName(r.T.InstallDir);
		if (dir.Length > 0 && (name.Contains(dir) || dir.Contains(name)))
		{
			return name.Contains(dir) ? "install folder is a shortened form of the name (words dropped)" : "install folder adds words to the name (subtitle, edition, publisher)";
		}
		if (folders.Any(f => f.Length >= 4 && (name.Contains(f) || f.Contains(name))))
		{
			return "a sub-folder partly matches the name";
		}
		string exe = GameCatalog.NormalizeName(Path.GetFileNameWithoutExtension(r.L.Executable));
		return exe == name ? "exe file name equals the name (exe names are not matched by name)" : "install folder unrelated to the name (abbreviation, codename, other language)";
	}

	private sealed record Row(Truth T, Launch L, bool Primary, bool NameOk, Detection V1, Detection V2, Detection V2t, Detection H1, Detection H2, Detection H1b, Detection H3)
	{
		public string Outcome(Detection d) => d.Game == null ? "miss" : d.Game.Name == T.Game.Name ? "ok" : "wrong";
	}
}
