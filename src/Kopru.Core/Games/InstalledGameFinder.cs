// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;
using Kopru.Core.Steam;

namespace Kopru.Core.Games;

// Where an installed supported game was found (the first source wins when several find it).
public enum InstalledSource
{
	Steam,
	Epic,
	Registry,
	Folder,
	Seen
}

// A supported game installed on this PC: the catalog game, where it was found and its install folder (null when the
// source does not say).
public sealed record InstalledGame(CatalogGame Game, InstalledSource Source, string? Folder);

// A supported game the watcher once recognised running, with the folder it ran from (saved with the settings).
public sealed record SeenGame(string Name, string Folder);

// One scan: the games found (sorted by name) and how long it took.
public sealed record InstalledScan(IReadOnlyList<InstalledGame> Games, TimeSpan Took)
{
	public static InstalledScan Empty { get; } = new([], TimeSpan.Zero);

	public int Count(InstalledSource source) => Games.Count(g => g.Source == source);
}

// The file system as the finder reads it: directory listings and small text files only (faked in tests). Every call may
// throw IOException, UnauthorizedAccessException, SecurityException or ArgumentException; the finder skips that place.
public interface IInstallFileSystem
{
	// The roots of the fixed drives ("C:\", "D:\").
	IEnumerable<string> FixedDrives();

	bool DirectoryExists(string path);

	// The folders directly inside path (full paths).
	IEnumerable<string> Directories(string path);

	// The files directly inside path matching a pattern such as "*.exe" (full paths).
	IEnumerable<string> Files(string path, string pattern);

	string ReadAllText(string path);
}

// One entry under the Windows uninstall registry keys.
public sealed record UninstallEntry(string? DisplayName, string? InstallLocation);

// The Windows uninstall registry keys (faked in tests): HKLM 64- and 32-bit and HKCU. Never throws.
public interface IUninstallEntries
{
	IEnumerable<UninstallEntry> Read();
}

// Finds the supported games installed on this PC, from (1) the Steam libraries (appmanifest_<id>.acf), (2) the Epic Games
// Launcher manifests (*.item), (3) the Windows uninstall keys (GOG and others, by the whole normalised name), (4) game
// folders: one level inside a "Games", "Oyunlar", "Game" or "Oyun" folder at the root of each fixed drive and inside the
// folders the user added (by folder name or a catalog exe at the top or in Binaries\Win64), (5) games the watcher saw
// running before, while their folder still exists. Never walks a whole drive: directory listings only; unreadable places
// are skipped quietly. Names match like the watcher's name fallback: GameCatalog.NormalizeName, the whole name, at least
// GameCatalog.MinNameLength characters. Logs one summary line per scan (no paths).
public sealed class InstalledGameFinder(IInstallFileSystem files, IUninstallEntries? registry = null, SteamLibrary? steam = null,
	string? epicManifests = null, Action<string>? log = null)
{
	// The folder names at a drive's root that hold games (compared ignoring case).
	public static readonly IReadOnlyList<string> RootFolderNames = ["Games", "Oyunlar", "Game", "Oyun"];

	private readonly Action<string> log = log ?? Diagnostics.AppLog.Write;

	public static InstalledGameFinder CreateDefault() => new(new SystemInstallFileSystem(), new SystemUninstallEntries(), SteamLibrary.CreateDefault(),
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests"));

	public InstalledScan Scan(GameCatalog catalog, IReadOnlyList<string>? extraFolders = null, IReadOnlyList<SeenGame>? seen = null)
	{
		var watch = Stopwatch.StartNew();
		var found = new Dictionary<string, InstalledGame>(StringComparer.OrdinalIgnoreCase);
		void Add(CatalogGame? game, InstalledSource source, string? folder)
		{
			if (game != null)
			{
				found.TryAdd(game.Name, new InstalledGame(game, source, folder));
			}
		}
		Dictionary<string, CatalogGame> names = NameIndex(catalog);

		foreach ((string folder, long appId) in SteamInstalls())
		{
			Add(catalog.FindBySteamAppId(appId), InstalledSource.Steam, folder);
		}
		foreach ((CatalogGame game, string? folder) in Epic(catalog, names))
		{
			Add(game, InstalledSource.Epic, folder);
		}
		foreach ((CatalogGame game, string? folder) in Registry(names))
		{
			Add(game, InstalledSource.Registry, folder);
		}
		foreach ((CatalogGame game, string folder) in Folders(catalog, names, extraFolders ?? []))
		{
			Add(game, InstalledSource.Folder, folder);
		}
		foreach (SeenGame entry in seen ?? [])
		{
			if (!string.IsNullOrWhiteSpace(entry.Folder) && catalog.Games.FirstOrDefault(g => string.Equals(g.Name, entry.Name, StringComparison.OrdinalIgnoreCase)) is { } game
				&& Safe(() => files.DirectoryExists(entry.Folder)))
			{
				Add(game, InstalledSource.Seen, entry.Folder);
			}
		}

		watch.Stop();
		var scan = new InstalledScan(found.Values.OrderBy(g => g.Game.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), watch.Elapsed);
		log($"installed games: {scan.Games.Count} found (steam {scan.Count(InstalledSource.Steam)}, epic {scan.Count(InstalledSource.Epic)}, " +
			$"registry {scan.Count(InstalledSource.Registry)}, folders {scan.Count(InstalledSource.Folder)}, seen {scan.Count(InstalledSource.Seen)}) " +
			$"in {(long)watch.Elapsed.TotalMilliseconds} ms");
		return scan;
	}

	// The install folder of a game whose exe runs at exePath: the exe's folder, or for an Unreal Engine game binary
	// ("<Game>\<Project>\Binaries\Win64\X-Win64-Shipping.exe") the game's folder. Null for a bare file name.
	public static string? InstallFolderOf(string? exePath)
	{
		if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathRooted(exePath))
		{
			return null;
		}
		string? folder = Path.GetDirectoryName(exePath.Replace('/', '\\'));
		for (string? up = folder; !string.IsNullOrEmpty(up); up = Path.GetDirectoryName(up))
		{
			if (string.Equals(Path.GetFileName(up), "Binaries", StringComparison.OrdinalIgnoreCase))
			{
				string? project = Path.GetDirectoryName(up);
				string? game = project == null ? null : Path.GetDirectoryName(project);
				return !string.IsNullOrEmpty(game) && Path.GetFileName(game).Length > 0 ? game : project ?? folder;
			}
		}
		return folder;
	}

	// Every catalog game by its whole normalised name (long enough to be safe).
	private static Dictionary<string, CatalogGame> NameIndex(GameCatalog catalog)
	{
		var index = new Dictionary<string, CatalogGame>(StringComparer.Ordinal);
		foreach (CatalogGame game in catalog.Games)
		{
			string key = GameCatalog.NormalizeName(game.Name);
			if (key.Length >= GameCatalog.MinNameLength)
			{
				index.TryAdd(key, game);
			}
		}
		return index;
	}

	private static CatalogGame? ByName(Dictionary<string, CatalogGame> names, string? text)
	{
		string key = GameCatalog.NormalizeName(text);
		return key.Length >= GameCatalog.MinNameLength && names.TryGetValue(key, out CatalogGame? game) ? game : null;
	}

	private IReadOnlyList<(string Folder, long AppId)> SteamInstalls()
	{
		try
		{
			return steam?.ReadNow() ?? [];
		}
		catch (Exception e) when (IsSkippable(e))
		{
			return [];
		}
	}

	// Epic manifests: the launch exe first, then the display name, then the install folder's name.
	private IEnumerable<(CatalogGame, string?)> Epic(GameCatalog catalog, Dictionary<string, CatalogGame> names)
	{
		if (string.IsNullOrEmpty(epicManifests))
		{
			yield break;
		}
		foreach (string manifest in List(() => files.Files(epicManifests, "*.item")))
		{
			string? name = null, location = null, launch = null;
			try
			{
				using JsonDocument doc = JsonDocument.Parse(files.ReadAllText(manifest));
				if (doc.RootElement.ValueKind != JsonValueKind.Object)
				{
					continue;
				}
				name = Read(doc.RootElement, "DisplayName");
				location = Read(doc.RootElement, "InstallLocation");
				launch = Read(doc.RootElement, "LaunchExecutable");
			}
			catch (Exception e) when (e is JsonException || IsSkippable(e))
			{
				continue;
			}
			if (!string.IsNullOrWhiteSpace(location) && !Safe(() => files.DirectoryExists(location)))
			{
				continue;
			}
			string? folderName = string.IsNullOrWhiteSpace(location) ? null : Path.GetFileName(location.Replace('/', '\\').TrimEnd('\\'));
			CatalogGame? game = catalog.Find(launch?.Replace('/', '\\')) ?? ByName(names, name) ?? ByName(names, folderName);
			if (game != null)
			{
				yield return (game, string.IsNullOrWhiteSpace(location) ? null : location);
			}
		}
	}

	private static string? Read(JsonElement root, string property) =>
		root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	// Uninstall keys: the whole display name; an entry whose install folder is gone is left out.
	private IEnumerable<(CatalogGame, string?)> Registry(Dictionary<string, CatalogGame> names)
	{
		if (registry == null)
		{
			yield break;
		}
		foreach (UninstallEntry entry in List(registry.Read))
		{
			if (ByName(names, entry.DisplayName) is not { } game)
			{
				continue;
			}
			string? location = string.IsNullOrWhiteSpace(entry.InstallLocation) ? null : entry.InstallLocation.Trim().Trim('"');
			if (location != null && !Safe(() => files.DirectoryExists(location)))
			{
				continue;
			}
			yield return (game, location);
		}
	}

	// Game folders: the sub-folders of each root (and an added folder itself), by name or by a catalog exe inside.
	private IEnumerable<(CatalogGame, string)> Folders(GameCatalog catalog, Dictionary<string, CatalogGame> names, IReadOnlyList<string> extra)
	{
		var roots = new List<string>();
		foreach (string drive in List(files.FixedDrives))
		{
			foreach (string folder in List(() => files.Directories(drive)))
			{
				if (RootFolderNames.Contains(Path.GetFileName(folder.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase))
				{
					roots.Add(folder);
				}
			}
		}
		var candidates = new List<string>();
		foreach (string added in extra.Where(f => !string.IsNullOrWhiteSpace(f)))
		{
			if (Safe(() => files.DirectoryExists(added)))
			{
				candidates.Add(added);
				roots.Add(added);
			}
		}
		foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			candidates.AddRange(List(() => files.Directories(root)));
		}
		foreach (string folder in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if ((ByName(names, Path.GetFileName(folder.TrimEnd('\\'))) ?? ByExe(catalog, folder)) is { } game)
			{
				yield return (game, folder);
			}
		}
	}

	// A catalog exe at the top of the folder or in the Unreal layout's Binaries\Win64.
	private CatalogGame? ByExe(GameCatalog catalog, string folder)
	{
		foreach (string place in new[] { folder, Path.Combine(folder, "Binaries", "Win64") })
		{
			foreach (string exe in List(() => files.Files(place, "*.exe")))
			{
				if (catalog.Find(exe) is { } game)
				{
					return game;
				}
			}
		}
		return null;
	}

	// The whole listing, or nothing when the place cannot be read.
	private static List<string> List(Func<IEnumerable<string>> list)
	{
		try
		{
			return list().ToList();
		}
		catch (Exception e) when (IsSkippable(e))
		{
			return [];
		}
	}

	private static List<UninstallEntry> List(Func<IEnumerable<UninstallEntry>> list)
	{
		try
		{
			return list().ToList();
		}
		catch (Exception e) when (IsSkippable(e))
		{
			return [];
		}
	}

	private static bool Safe(Func<bool> check)
	{
		try
		{
			return check();
		}
		catch (Exception e) when (IsSkippable(e))
		{
			return false;
		}
	}

	private static bool IsSkippable(Exception e) =>
		e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;
}

// The real file system: top-level listings only.
public sealed class SystemInstallFileSystem : IInstallFileSystem
{
	public IEnumerable<string> FixedDrives() =>
		DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName);

	public bool DirectoryExists(string path) => Directory.Exists(path);

	public IEnumerable<string> Directories(string path) => Directory.Exists(path) ? Directory.EnumerateDirectories(path) : [];

	public IEnumerable<string> Files(string path, string pattern) => Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern) : [];

	public string ReadAllText(string path) => File.ReadAllText(path);
}

// HKLM (64- and 32-bit views) and HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\*": DisplayName and
// InstallLocation. Read only.
public sealed class SystemUninstallEntries : IUninstallEntries
{
	private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

	public IEnumerable<UninstallEntry> Read()
	{
		if (!OperatingSystem.IsWindows())
		{
			return [];
		}
		var list = new List<UninstallEntry>();
		foreach ((Microsoft.Win32.RegistryHive hive, Microsoft.Win32.RegistryView view) in new[]
		{
			(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
			(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
			(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default)
		})
		{
			try
			{
				using Microsoft.Win32.RegistryKey root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
				using Microsoft.Win32.RegistryKey? uninstall = root.OpenSubKey(UninstallKey);
				if (uninstall == null)
				{
					continue;
				}
				foreach (string name in uninstall.GetSubKeyNames())
				{
					try
					{
						using Microsoft.Win32.RegistryKey? app = uninstall.OpenSubKey(name);
						if (app?.GetValue("DisplayName") is string display && display.Length > 0)
						{
							list.Add(new UninstallEntry(display, app.GetValue("InstallLocation") as string));
						}
					}
					catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
					{
						// One unreadable entry: skipped.
					}
				}
			}
			catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
			{
				// This view cannot be read: the others still count.
			}
		}
		return list;
	}
}

// The latest scan and running it off the calling thread; a scan asked for while one runs runs once more after it.
public sealed class InstalledGames(InstalledGameFinder finder)
{
	private readonly object gate = new();
	private Task<InstalledScan>? running;
	private bool again;

	public InstalledScan Latest { get; private set; } = InstalledScan.Empty;

	public bool HasScanned { get; private set; }

	public Task<InstalledScan> ScanAsync(Func<(GameCatalog Catalog, IReadOnlyList<string> Folders, IReadOnlyList<SeenGame> Seen)> input)
	{
		lock (gate)
		{
			if (running is { IsCompleted: false })
			{
				again = true;
				return running;
			}
			running = Task.Run(() => Loop(input));
			return running;
		}
	}

	private InstalledScan Loop(Func<(GameCatalog Catalog, IReadOnlyList<string> Folders, IReadOnlyList<SeenGame> Seen)> input)
	{
		while (true)
		{
			(GameCatalog catalog, IReadOnlyList<string> folders, IReadOnlyList<SeenGame> seen) = input();
			InstalledScan scan = finder.Scan(catalog, folders, seen);
			lock (gate)
			{
				Latest = scan;
				HasScanned = true;
				if (!again)
				{
					return scan;
				}
				again = false;
			}
		}
	}
}
