// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;
using Kopru.Core.Steam;

namespace Kopru.DetectStudy;

// No foreground process: the harness calls GameWatcher.Recognize directly.
internal sealed class NoProcesses : IProcessSource
{
	public ProcessInfo? Foreground() => null;

	public bool IsRunning(ProcessInfo process) => false;

	public IReadOnlyList<ProcessInfo> WindowedApps() => [];
}

// Window title and version info supplied by the harness (null: not available).
internal sealed class FixedDetails(Func<ProcessInfo, string?>? title = null, Func<string, (string?, string?)>? version = null) : IProcessDetails
{
	public string? WindowTitle(ProcessInfo process) => title?.Invoke(process);

	public (string? Product, string? Description) VersionInfo(string path) => version?.Invoke(path) ?? (null, null);
}

// A Steam installation that exists only in memory: one library at SteamPath with one appmanifest per game.
internal sealed class FakeSteam : ISteamEnvironment
{
	public const string Root = @"S:\Steam";

	private readonly Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);

	public void Add(long appId, string installDir)
	{
		string dir = installDir.Replace("\\", "\\\\").Replace("\"", "\\\"");
		files[Path.Combine(Root, "steamapps", $"appmanifest_{appId}.acf")] =
			$"\"AppState\"\n{{\n\t\"appid\"\t\t\"{appId}\"\n\t\"installdir\"\t\t\"{dir}\"\n}}\n";
	}

	public string? SteamPath => Root;

	public bool IsSteamRunning => false;

	public bool FileExists(string path) => files.ContainsKey(path);

	public string ReadAllText(string path) => files[path];

	public IEnumerable<string> GetDirectories(string path) => [];

	public DateTime GetLastWriteTimeUtc(string path) => default;

	public IEnumerable<string> GetFiles(string path, string pattern) =>
		files.Keys.Where(k => string.Equals(Path.GetDirectoryName(k), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
			&& Path.GetFileName(k).StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase)).ToList();
}

internal readonly record struct Detection(CatalogGame? Game, string? Signal);

internal static class Sim
{
	private static int nextId = 1000;

	// Runs the real GameWatcher lookup once for one simulated process; the signal is read from the watcher's own log line.
	public static Detection Detect(GameCatalog catalog, SteamLibrary? steam, IProcessDetails details, string exe, string? path)
	{
		string? signal = null;
		void Log(string line)
		{
			int i = line.IndexOf(" recognised by ", StringComparison.Ordinal);
			if (i >= 0)
			{
				int end = line.LastIndexOf(" (", StringComparison.Ordinal);
				signal = line[(i + 15)..(end > i ? end : line.Length)];
			}
		}
		var watcher = new GameWatcher(catalog, new NoProcesses(), steam, details, Log);
		ForegroundGame? game = watcher.Recognize(new ProcessInfo(Interlocked.Increment(ref nextId), exe, path));
		return new Detection(game?.Match, game == null ? null : signal ?? "?");
	}

	public static string Csv(params object?[] cells) =>
		string.Join(",", cells.Select(c => c?.ToString() is { } s && (s.Contains(',') || s.Contains('"')) ? "\"" + s.Replace("\"", "\"\"") + "\"" : c?.ToString() ?? ""));

	public static string Md(string? text) => (text ?? "").Replace("|", "\\|");
}
