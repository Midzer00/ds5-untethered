// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Steam;

// Maps an installed game's folder to its Steam app id, from Steam's own files:
//  - HKCU\Software\Valve\Steam "SteamPath": the Steam folder, itself a library.
//  - <Steam>\steamapps\libraryfolders.vdf: the other libraries. Current format: "libraryfolders" { "0" { "path" "D:\\SteamLibrary" } };
//    older format: "LibraryFolders" { "1" "D:\\SteamLibrary" }.
//  - <library>\steamapps\appmanifest_<appid>.acf: "AppState" { "appid" "1091500" "installdir" "Cyberpunk 2077" }.
// A process whose path is under <library>\steamapps\common\<installdir>\ belongs to that app id. The map is read again at
// most every RefreshInterval (games get installed while the app runs).
public sealed class SteamLibrary(ISteamEnvironment environment, Func<long>? clockMs = null)
{
	public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

	private readonly Func<long> clock = clockMs ?? (() => Environment.TickCount64);
	private readonly object gate = new();
	private IReadOnlyList<(string Folder, long AppId)> installs = [];
	private long readAt = long.MinValue;

	public static SteamLibrary CreateDefault() => new(new SystemSteamEnvironment());

	// The library folders: the Steam folder first, then the ones in libraryfolders.vdf (no duplicates).
	public static IReadOnlyList<string> LibraryFolders(string steamPath, VdfNode? libraryFolders)
	{
		var list = new List<string> { Trim(steamPath) };
		VdfNode? root = libraryFolders?["libraryfolders"];
		foreach (VdfNode entry in root?.Children.Values ?? Enumerable.Empty<VdfNode>())
		{
			string? path = entry.Value ?? entry.GetValue("path");
			// Old files mix numbered library entries with settings such as "TimeNextStatsReport".
			if (!string.IsNullOrWhiteSpace(path) && (path.Contains('\\') || path.Contains('/')))
			{
				string folder = Trim(path);
				if (!list.Contains(folder, StringComparer.OrdinalIgnoreCase))
				{
					list.Add(folder);
				}
			}
		}
		return list;
	}

	// The app id and install folder name of one appmanifest_<appid>.acf, or null when it has neither.
	public static (long AppId, string InstallDir)? ParseManifest(string text) =>
		ParseApp(text, string.Empty) is { } app ? (app.AppId, app.InstallDir) : null;

	// One appmanifest_<appid>.acf in full ("name", and "StateFlags" bit 4: fully installed), with its install folder under
	// the library's steamapps folder; null when it has no app id or install folder name.
	public static SteamApp? ParseApp(string text, string steamApps)
	{
		VdfNode? state = Vdf.Parse(text)["AppState"];
		string? dir = state?.GetValue("installdir");
		if (!long.TryParse(state?.GetValue("appid"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long id)
			|| string.IsNullOrWhiteSpace(dir))
		{
			return null;
		}
		// No flags (an old or hand-made file) counts as installed.
		bool installed = !long.TryParse(state?.GetValue("StateFlags"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long flags)
			|| (flags & FullyInstalled) != 0;
		string name = state?.GetValue("name") is { Length: > 0 } n && !string.IsNullOrWhiteSpace(n) ? n.Trim() : dir.Trim();
		return new SteamApp(id, name, dir.Trim(), Trim(Path.Combine(steamApps, "common", dir.Trim())), installed);
	}

	// "StateFlags" bit: the app is fully installed (updates pending keep it).
	public const long FullyInstalled = 4;

	// The full install folders ("<library>\steamapps\common\<installdir>") with their app ids.
	public IReadOnlyList<(string Folder, long AppId)> Installs()
	{
		lock (gate)
		{
			long now = clock();
			if (readAt == long.MinValue || now - readAt >= RefreshInterval.TotalMilliseconds)
			{
				readAt = now;
				installs = Read();
			}
			return installs;
		}
	}

	// Reads Steam's files again now (not waiting for RefreshInterval), e.g. for a scan the user asked for.
	public IReadOnlyList<(string Folder, long AppId)> ReadNow()
	{
		ReadAppsNow();
		lock (gate)
		{
			return installs;
		}
	}

	// Every app manifest in every library, read now (installs follow it too).
	public IReadOnlyList<SteamApp> ReadAppsNow()
	{
		lock (gate)
		{
			readAt = clock();
			List<SteamApp> apps = ReadApps();
			installs = ToInstalls(apps);
			return apps;
		}
	}

	// The Steam app id of the game the exe at path belongs to, or null.
	public long? AppIdFor(string? exePath)
	{
		if (string.IsNullOrWhiteSpace(exePath))
		{
			return null;
		}
		string path = exePath.Replace('/', '\\');
		foreach ((string folder, long appId) in Installs())
		{
			if (path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
			{
				return appId;
			}
		}
		return null;
	}

	private List<(string, long)> Read() => ToInstalls(ReadApps());

	private static List<(string, long)> ToInstalls(List<SteamApp> apps)
	{
		var found = apps.Select(a => (a.Folder, a.AppId)).ToList();
		// Longest first, so a folder nested in another one wins.
		found.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
		return found;
	}

	private List<SteamApp> ReadApps()
	{
		var found = new List<SteamApp>();
		try
		{
			string? steamPath = environment.SteamPath;
			if (string.IsNullOrEmpty(steamPath))
			{
				return found;
			}
			VdfNode? folders = null;
			string foldersFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
			if (environment.FileExists(foldersFile))
			{
				try
				{
					folders = Vdf.Parse(environment.ReadAllText(foldersFile));
				}
				catch (VdfFormatException)
				{
					// The Steam folder alone then.
				}
			}
			foreach (string library in LibraryFolders(steamPath, folders))
			{
				string apps = Path.Combine(library, "steamapps");
				foreach (string manifest in environment.GetFiles(apps, "appmanifest_*.acf"))
				{
					try
					{
						if (ParseApp(environment.ReadAllText(manifest), apps) is { } app)
						{
							found.Add(app);
						}
					}
					catch (Exception e) when (e is VdfFormatException or IOException or UnauthorizedAccessException)
					{
						// Skips a broken or locked manifest.
					}
				}
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
		{
			// Unreadable library: nothing maps.
		}
		return found;
	}

	private static string Trim(string path) => path.Replace('/', '\\').TrimEnd('\\');
}

// One Steam app manifest: id, name, install folder name, full install folder and whether it is fully installed.
public sealed record SteamApp(long AppId, string Name, string InstallDir, string Folder, bool Installed);

// Steam's own entries that are not games: the shared redistributables, the Linux/Proton runtimes, SteamVR, SDKs, dedicated
// servers and soundtracks. Steam marks the type only in its binary appinfo cache (not read), so this is a short list of
// known app ids and name patterns; anything else Steam installed counts as a game (the user can hide it).
public static class SteamNonGames
{
	// Steamworks Common Redistributables, SteamVR, Steam Linux Runtime 1.0/2.0/3.0, Proton Experimental/Hotfix/EasyAntiCheat
	// Runtime/BattlEye Runtime.
	public static readonly IReadOnlySet<long> AppIds = new HashSet<long>
	{
		228980, 250820, 1070560, 1391110, 1628350, 1493710, 2180100, 1826330, 1161040
	};

	private static readonly System.Text.RegularExpressions.Regex Names = new(
		@"^(Steamworks\b|SteamVR\b|Steam Linux Runtime\b|Proton (\d|Experimental|Hotfix|Next|EasyAntiCheat|BattlEye))" +
		@"|\b(Redistributables?|Dedicated Server|SDK|Soundtrack|OST)\b",
		System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	public static bool IsNonGame(long appId, string? name) => AppIds.Contains(appId) || (name != null && Names.IsMatch(name));
}
