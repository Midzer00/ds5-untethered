// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;
using Kopru.DetectStudy;

// Usage: Kopru.DetectStudy <out-dir> [fetch|audit|recall|propose|local|fp|all] [--offline] [--cache=<dir>]
// Everything written (downloaded metadata in <out-dir>\cache, results as .md/.csv) goes to <out-dir>; keep it outside the repo.
if (args.Length < 1)
{
	Console.Error.WriteLine("usage: Kopru.DetectStudy <out-dir> [fetch|audit|recall|propose|local|fp|all] [--offline] [--cache=<dir>]");
	return 2;
}
string outDir = Path.GetFullPath(args[0]);
string command = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "all";
bool offline = args.Contains("--offline");
Directory.CreateDirectory(outDir);
// Library code that logs (InstalledGameFinder's scan summary) must not write into the app's own log.
Kopru.Core.Diagnostics.AppLog.Directory = Path.Combine(outDir, "applog");
GameCatalog catalog = GameCatalog.LoadEmbedded();
// --cache=<dir> reads (and fills) the metadata cache elsewhere, so several result folders can share one download.
string cacheDir = args.FirstOrDefault(a => a.StartsWith("--cache=", StringComparison.Ordinal)) is { } c ? Path.GetFullPath(c[8..]) : Path.Combine(outDir, "cache");
var truth = new GroundTruth(cacheDir, offline);

bool Run(string name) => command == "all" || command == name;

List<Truth>? truths = null;
List<Truth> Truths() => truths ??= truth.Resolve(catalog);

if (Run("fetch"))
{
	Console.WriteLine($"ground truth for {Truths().Count} of {catalog.Games.Count} games");
}
if (Run("audit"))
{
	File.WriteAllText(Path.Combine(outDir, "audit.md"), Audit.Report(catalog));
}
if (Run("recall"))
{
	File.WriteAllText(Path.Combine(outDir, "recall.md"), Recall.Report(catalog, Truths(), truth.Problems, outDir));
}
if (command == "propose")
{
	File.WriteAllText(Path.Combine(outDir, "propose.md"), Propose.Report(catalog, Truths(), outDir));
}
if (Run("local"))
{
	File.WriteAllText(Path.Combine(outDir, "local.md"), Local.Report(catalog, cacheDir));
}
if (Run("fp"))
{
	File.WriteAllText(Path.Combine(outDir, "fp.md"), FalsePositives.Report(catalog, outDir));
}
Console.WriteLine("done: " + outDir);
return 0;
