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
	public static (long AppId, string InstallDir)? ParseManifest(string text)
	{
		VdfNode? state = Vdf.Parse(text)["AppState"];
		string? dir = state?.GetValue("installdir");
		return long.TryParse(state?.GetValue("appid"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long id)
			&& !string.IsNullOrWhiteSpace(dir) ? (id, dir.Trim()) : null;
	}

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
		lock (gate)
		{
			readAt = clock();
			installs = Read();
			return installs;
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

	private List<(string, long)> Read()
	{
		var found = new List<(string, long)>();
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
						if (ParseManifest(environment.ReadAllText(manifest)) is (long id, string dir))
						{
							found.Add((Trim(Path.Combine(apps, "common", dir)), id));
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
		// Longest first, so a folder nested in another one wins.
		found.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
		return found;
	}

	private static string Trim(string path) => path.Replace('/', '\\').TrimEnd('\\');
}
