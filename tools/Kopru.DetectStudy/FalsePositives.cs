// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using Kopru.Core.Games;
using Kopru.Core.Steam;

namespace Kopru.DetectStudy;

// Part D: every non-game program on this PC through the real lookup, with its real folder names and version info:
// exe files under Program Files, Program Files (x86) and %LOCALAPPDATA%\Programs (game folders left out), uninstall
// registry entries (display name as product name, install folder as the path) and running processes (real window title).
// Any program recognised as a catalog game is a finding. The output names local programs: it stays in the out dir.
internal static class FalsePositives
{
	private static readonly string[] GamePlaces = [@"\steamapps\common\", @"\Epic Games\", @"\WindowsApps\", @"\XboxGames\", @"\GOG Galaxy\Games\", @"\Ubisoft Game Launcher\games\", @"\EA Games\", @"\Riot Games\"];

	public static string Report(GameCatalog catalog, string outDir)
	{
		var steam = SteamLibrary.CreateDefault();
		InstalledScan scan = InstalledGameFinder.CreateDefault().Scan(catalog);
		var gameFolders = scan.Games.Where(g => g.Folder != null).Select(g => g.Folder!.TrimEnd('\\') + "\\").ToList();
		bool IsGamePath(string path) => GamePlaces.Any(p => path.Contains(p, StringComparison.OrdinalIgnoreCase))
			|| gameFolders.Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase));
		var details = new FixedDetails(version: Local.Version);
		var hits = new List<string>();
		var sb = new StringBuilder("# Part D: false positives on this PC\n\n");

		// Exe files.
		var roots = new[]
		{
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
		}.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		int files = 0, skipped = 0;
		var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
		foreach (string root in roots)
		{
			foreach (string exe in Directory.EnumerateFiles(root, "*.exe", options))
			{
				if (IsGamePath(exe))
				{
					skipped++;
					continue;
				}
				files++;
				foreach (string f in GameWatcher.Folders(exe))
				{
					folderNames.Add(f);
				}
				Detection d = Sim.Detect(catalog, steam, details, Path.GetFileName(exe), exe);
				if (d.Game != null)
				{
					(string? product, string? description) = Local.Version(exe);
					hits.Add($"exe file | {Sim.Md(exe)} | {d.Game.Name} | {d.Signal} | product \"{Sim.Md(product)}\", description \"{Sim.Md(description)}\"");
				}
			}
		}

		// Uninstall registry.
		var entries = new SystemUninstallEntries().Read().ToList();
		var allNames = new GameCatalog(catalog.Games.Select(g => g with { Exe = [] }));
		int regChecked = 0;
		foreach (UninstallEntry e in entries)
		{
			string? loc = string.IsNullOrWhiteSpace(e.InstallLocation) ? null : e.InstallLocation.Trim().Trim('"').TrimEnd('\\');
			if (loc != null && IsGamePath(loc + "\\"))
			{
				continue;
			}
			regChecked++;
			string path = Path.Combine(loc ?? @"C:\Program Files\_", "_.exe");
			Detection d = Sim.Detect(catalog, steam, new FixedDetails(version: _ => (e.DisplayName, null)), "_.exe", path);
			if (d.Game != null)
			{
				hits.Add($"uninstall entry (watcher) | {Sim.Md(e.DisplayName)} at {Sim.Md(loc)} | {d.Game.Name} | {d.Signal} | ");
			}
			// InstalledGameFinder's registry pass names every catalog game, with or without exe names.
			if (allNames.FindByName(e.DisplayName) is { } byName)
			{
				hits.Add($"uninstall entry (InstalledGameFinder) | {Sim.Md(e.DisplayName)} at {Sim.Md(loc)} | {byName.Name} | display name | listed as installed{(scan.Games.Any(g => g.Game.Name == byName.Name) ? " (in this scan)" : "")}");
			}
		}

		// Running processes.
		int procs = 0;
		foreach (Process p in Process.GetProcesses())
		{
			using (p)
			{
				string? path = SystemProcessSource.PathOf(p.Id);
				if (path != null && IsGamePath(path))
				{
					continue;
				}
				string? title = null;
				try
				{
					title = string.IsNullOrEmpty(p.MainWindowTitle) ? null : p.MainWindowTitle;
				}
				catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
				{
				}
				procs++;
				Detection d = Sim.Detect(catalog, steam, new FixedDetails(_ => title, Local.Version), p.ProcessName + ".exe", path);
				if (d.Game != null)
				{
					hits.Add($"running process | {Sim.Md(p.ProcessName)}.exe {Sim.Md(path ?? "(path unreadable)")} title \"{Sim.Md(title)}\" | {d.Game.Name} | {d.Signal} | ");
				}
			}
		}

		// Folder names of all checked exes that equal any catalog name (with-exe entries included): would match if the
		// name fallback were extended to every entry.
		var wouldMatch = folderNames.Where(f => allNames.FindByName(f) != null).Select(f => $"{f} -> {allNames.FindByName(f)!.Name}").ToList();

		sb.AppendLine($"- exe files checked: {files} (left out as game folders: {skipped}) under {roots.Count} roots");
		sb.AppendLine($"- distinct folder names among them: {folderNames.Count}");
		sb.AppendLine($"- uninstall entries checked: {regChecked} of {entries.Count}");
		sb.AppendLine($"- running processes checked: {procs}");
		sb.AppendLine($"- hits: {hits.Count}\n");
		sb.AppendLine("| source | program | matched game | signal | note |");
		sb.AppendLine("|---|---|---|---|---|");
		foreach (string h in hits)
		{
			sb.AppendLine($"| {h} |");
		}
		sb.AppendLine($"\nFolder names that equal some catalog name if every entry were name-searchable: {(wouldMatch.Count == 0 ? "none" : string.Join("; ", wouldMatch))}");
		return sb.ToString();
	}
}
