// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Kopru.Core.Games;

namespace Kopru.DetectStudy;

// Part A: a static audit of the catalog as GameCatalog indexes it.
internal static class Audit
{
	// Exe names generic enough to be used by unrelated programs.
	private static readonly HashSet<string> GenericExe = new(StringComparer.OrdinalIgnoreCase)
	{
		"game.exe", "launcher.exe", "main.exe", "start.exe", "client.exe", "app.exe", "play.exe", "run.exe", "setup.exe",
		"update.exe", "updater.exe", "bootstrapper.exe", "start_protected_game.exe", "unitycrashhandler64.exe", "win64.exe",
		"shipping.exe", "x64.exe", "engine.exe", "player.exe", "default.exe"
	};

	// Normalised words that are also common folder, product or file-description names outside games.
	private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
	{
		"control", "returnal", "deathloop", "ghostrunner", "stray", "hades", "inside", "limbo", "journey", "abzu", "tunic",
		"outlast", "anthem", "prey", "dirt", "remnant", "lies", "ready", "forspoken", "immortals", "sifu", "neon",
		"atomic", "minecraft", "fortnite", "destiny", "warframe", "palworld", "lethal", "phasmophobia", "manager", "monitor",
		"studio", "player", "launcher", "update", "service", "settings", "driver", "toolkit", "runtime", "server", "client",
		"capture", "assistant", "terminal", "console", "network", "bridge", "connect", "control", "center", "centre", "hub",
		"helper", "mirror", "echo", "pulse", "ascent", "evolve", "nobody", "after", "atlas", "matter", "active", "animal",
		"wells", "paradise", "outer", "wilds", "cyber", "first", "light", "biomutant", "dying", "lords", "fallen", "spirit",
		"enshrouded", "satisfactory", "squad", "unknown", "observer", "darkest", "endfield", "visions", "horizon", "rift"
	};

	public static string Report(GameCatalog catalog)
	{
		var sb = new StringBuilder("# Part A: catalog audit\n\n");
		IReadOnlyList<CatalogGame> games = catalog.Games;
		var withExe = games.Where(g => g.Exe.Any(e => GameCatalog.Normalize(e).Length > 0)).ToList();
		var noExe = games.Except(withExe).ToList();
		sb.AppendLine($"- entries: {games.Count}");
		sb.AppendLine($"- with exe names: {withExe.Count} ({withExe.Sum(g => g.Exe.Count)} exe names)");
		sb.AppendLine($"- without exe names (name fallback only, plus Steam folder): {noExe.Count}");
		sb.AppendLine($"- with a Steam app id: {games.Count(g => g.SteamAppId != null)}; without: {games.Count(g => g.SteamAppId == null)} ({string.Join(", ", games.Where(g => g.SteamAppId == null).Select(g => g.Name))})");
		sb.AppendLine($"- no exe and no app id (name fallback is the only signal): {noExe.Count(g => g.SteamAppId == null)} ({string.Join(", ", noExe.Where(g => g.SteamAppId == null).Select(g => g.Name))})");
		sb.AppendLine($"- exeConfidence: {string.Join(", ", games.GroupBy(g => g.ExeConfidence ?? "null").Select(g => $"{g.Key} {g.Count()}"))}");

		sb.AppendLine("\n## Names shorter than MinNameLength after normalisation\n");
		foreach (CatalogGame g in games.Where(g => GameCatalog.NormalizeName(g.Name).Length < GameCatalog.MinNameLength))
		{
			bool exe = withExe.Contains(g);
			sb.AppendLine($"- {g.Name} -> `{GameCatalog.NormalizeName(g.Name)}` ({(exe ? "has exe names: still found by exe" : "NO exe: undetectable by name")}{(g.SteamAppId != null ? ", Steam folder works" : ", no app id")})");
		}

		sb.AppendLine("\n## Name-fallback reach: catalog games whose name FindByName answers\n");
		int reachable = games.Count(g => catalog.FindByName(g.Name) == g);
		sb.AppendLine($"- {reachable} of {games.Count} entries are reachable by their own name ({noExe.Count(g => catalog.FindByName(g.Name) == g)} of {noExe.Count} without exe names).");
		foreach (CatalogGame g in games.Where(g => catalog.FindByName(g.Name) is { } hit && hit != g))
		{
			sb.AppendLine($"- {g.Name}: its name finds {catalog.FindByName(g.Name)!.Name} instead (collision)");
		}

		sb.AppendLine("\n## Normalised name collisions and containment (all entries)\n");
		var norm = games.Select(g => (Game: g, Key: GameCatalog.NormalizeName(g.Name))).ToList();
		int pairs = 0;
		foreach (var a in norm)
		{
			foreach (var b in norm)
			{
				if (a.Game == b.Game || a.Key.Length == 0 || b.Key.Length == 0)
				{
					continue;
				}
				if (a.Key == b.Key && string.CompareOrdinal(a.Game.Name, b.Game.Name) < 0)
				{
					sb.AppendLine($"- EQUAL: {a.Game.Name} = {b.Game.Name} (`{a.Key}`)");
					pairs++;
				}
				else if (a.Key != b.Key && b.Key.Contains(a.Key, StringComparison.Ordinal))
				{
					string kind = b.Key.StartsWith(a.Key, StringComparison.Ordinal) ? "prefix" : "substring";
					sb.AppendLine($"- {kind}: `{a.Key}` ({a.Game.Name}) in `{b.Key}` ({b.Game.Name})");
					pairs++;
				}
			}
		}
		sb.AppendLine(pairs == 0 ? "- none" : $"\n{pairs} pairs. FindByName needs the whole name, so containment does not cause a wrong match today; it matters only if a partial or prefix match is ever added.");

		sb.AppendLine("\n## Generic names (no-exe entries reachable by name; a folder, product or window of that name in unrelated software matches)\n");
		foreach (CatalogGame g in noExe)
		{
			string key = GameCatalog.NormalizeName(g.Name);
			bool singleWord = !g.Name.Trim().Contains(' ');
			if (key.Length >= GameCatalog.MinNameLength && (CommonWords.Contains(key) || singleWord))
			{
				sb.AppendLine($"- {g.Name} (`{key}`){(CommonWords.Contains(key) ? " - common word" : " - single word")}");
			}
		}

		sb.AppendLine("\n## Exe names\n");
		var exeOwners = games.SelectMany(g => g.Exe.Select(e => (Exe: GameCatalog.Normalize(e), Game: g))).Where(x => x.Exe.Length > 0)
			.GroupBy(x => x.Exe, StringComparer.OrdinalIgnoreCase).ToList();
		var dup = exeOwners.Where(g => g.Select(x => x.Game).Distinct().Count() > 1).ToList();
		sb.AppendLine($"- distinct exe names: {exeOwners.Count}; shared by more than one entry: {dup.Count}");
		foreach (var d in dup)
		{
			sb.AppendLine($"  - {d.Key}: {string.Join(", ", d.Select(x => x.Game.Name).Distinct())} (first wins: {catalog.Find(d.Key)?.Name})");
		}
		foreach (var d in exeOwners.Where(g => GenericExe.Contains(g.Key) || GameWatcher.Ignored.Contains(g.Key) || g.Key.Length <= 8))
		{
			sb.AppendLine($"- short/generic/ignored exe: {d.Key} ({string.Join(", ", d.Select(x => x.Game.Name).Distinct())}){(GameWatcher.Ignored.Contains(d.Key) ? " - IN Ignored" : "")}");
		}
		var shipping = exeOwners.Count(g => g.Key.EndsWith(GameCatalog.ShippingSuffix, StringComparison.OrdinalIgnoreCase));
		sb.AppendLine($"- exe names ending in {GameCatalog.ShippingSuffix}: {shipping}");
		var pairCross = exeOwners.Where(g => GameCatalog.ShippingPair(g.Key) is { } p && catalog.Find(p) is { } other && other != g.First().Game).ToList();
		foreach (var d in pairCross)
		{
			sb.AppendLine($"- Unreal pair of {d.Key} belongs to another entry: {catalog.Find(GameCatalog.ShippingPair(d.Key))!.Name}");
		}

		var launcherLike = exeOwners.Where(g => Propose.IsLauncher(g.Key)).ToList();
		sb.AppendLine($"- launcher-like exe names: {(launcherLike.Count == 0 ? "none" : string.Join(", ", launcherLike.Select(g => $"{g.Key} ({g.First().Game.Name})")))}");
		var filtered = exeOwners.Select(g => (g.Key, Game: g.First().Game, Why: Propose.NameRejection(g.Key))).Where(x => x.Why != null).ToList();
		sb.AppendLine($"- exe names the Steam-data filter would reject (curated by hand, kept): {(filtered.Count == 0 ? "none" : string.Join(", ", filtered.Select(x => $"{x.Key} ({x.Game.Name}: {x.Why})")))}");

		sb.AppendLine("\n## Steam install folder names\n");
		var dirs = games.Where(g => g.SteamInstallDir != null).ToList();
		sb.AppendLine($"- entries with one: {dirs.Count}");
		foreach (CatalogGame g in dirs.Where(g => catalog.FindByInstallDir(g.SteamInstallDir) != g))
		{
			sb.AppendLine($"- {g.Name}: \"{g.SteamInstallDir}\" does not find its entry (shared, too short or equal to a game's name)");
		}

		sb.AppendLine("\n## Steam app ids\n");
		var dupIds = games.Where(g => g.SteamAppId != null).GroupBy(g => g.SteamAppId).Where(g => g.Count() > 1).ToList();
		sb.AppendLine(dupIds.Count == 0 ? "- no duplicate app ids" : string.Join("\n", dupIds.Select(d => $"- {d.Key}: {string.Join(", ", d.Select(g => g.Name))}")));
		return sb.ToString();
	}
}
