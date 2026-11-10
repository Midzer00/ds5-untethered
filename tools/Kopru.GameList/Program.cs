// SPDX-License-Identifier: GPL-3.0-or-later
// Refreshes src/Kopru.Core/Games/supported-games.json from PCGamingWiki's DualSense lists (maintainers and the weekly
// update-games workflow). Usage: dotnet run --project tools/Kopru.GameList -- [--from-archive] [--prune] [--dry-run]
//   [--file <supported-games.json>] [--today yyyy-MM-dd]
using Kopru.GameList;

bool fromArchive = args.Contains("--from-archive");
bool prune = args.Contains("--prune");
bool dryRun = args.Contains("--dry-run");
string? Option(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
string file = Option("--file") ?? Path.Combine(FindRepoRoot(), "src", "Kopru.Core", "Games", "supported-games.json");
string today = Option("--today") ?? DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

using HttpClient http = PcgwClient.CreateHttpClient();
var client = new PcgwClient(http, Console.Error);
IReadOnlyList<WikiGame> wiki;
try
{
	wiki = await client.LoadAsync(fromArchive);
}
catch (Exception e) when (e is HttpRequestException or InvalidDataException or System.Text.Json.JsonException or TaskCanceledException)
{
	Console.Error.WriteLine($"Could not read PCGamingWiki: {e.Message}");
	Console.Error.WriteLine(fromArchive ? "The archive snapshots were unreachable too." : "Try again with --from-archive.");
	return 1;
}
if (wiki.Count < 20)
{
	Console.Error.WriteLine($"Only {wiki.Count} games read; refusing to update.");
	return 1;
}

MergeResult result = GameListMerger.Merge(GameListFile.Parse(File.ReadAllText(file)), wiki, today, prune);
Console.WriteLine($"Source: {client.Method}; {wiki.Count} games on the lists; {result.File.Games.Count} in the file.");
Console.WriteLine($"Added ({result.Added.Count}): {string.Join(", ", result.Added)}");
Console.WriteLine($"Removed ({result.Removed.Count}): {string.Join(", ", result.Removed)}");
Console.WriteLine($"Flags changed ({result.Changed.Count}): {string.Join(", ", result.Changed)}");
if (dryRun || !result.HasChanges)
{
	return 0;
}
File.WriteAllText(file, result.File.ToJson(), new System.Text.UTF8Encoding(false));
string sources = Path.Combine(Path.GetDirectoryName(file)!, "SOURCES.md");
if (File.Exists(sources))
{
	File.WriteAllText(sources, SourcesNote.Update(File.ReadAllText(sources), today, client.Method, wiki.Count, result), new System.Text.UTF8Encoding(false));
}
return 0;

static string FindRepoRoot()
{
	for (string? dir = Environment.CurrentDirectory; dir != null; dir = Path.GetDirectoryName(dir))
	{
		if (File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			return dir;
		}
	}
	throw new DirectoryNotFoundException("Run inside the repository (Kopru.sln not found) or pass --file.");
}
