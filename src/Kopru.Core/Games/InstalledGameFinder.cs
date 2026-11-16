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
// LaunchId: how its launcher knows it (the Steam app id of the manifest it was found by, or the Epic AppName); LaunchExe: the
// exe its launcher starts (Epic), as a full path. Both null when the source does not say.
public sealed record InstalledGame(CatalogGame Game, InstalledSource Source, string? Folder, string? LaunchId = null, string? LaunchExe = null);

// A supported game the watcher once recognised running, with the folder it ran from (saved with the settings).
public sealed record SeenGame(string Name, string Folder);

// A found game the user hid from the list (FoundGame.Key and its name, to show it again); saved with the settings.
public sealed record HiddenGame(string Key, string Name);

// A game installed on this PC that the catalog does not list, as its launcher reports it (Steam or Epic only: their files
// say plainly that it is a game and where it lives). Id: the Steam app id or the Epic AppName. LaunchExe: Epic's launch exe
// as a full path (null for Steam). Key: what the per-game settings and the hidden list use ("steam:<appid>", "epic:<AppName>").
public sealed record FoundGame(string Name, InstalledSource Source, string Folder, string Id, string? LaunchExe = null)
{
	public string Key => KeyFor(Source, Id);

	public long? SteamAppId => Source == InstalledSource.Steam && long.TryParse(Id, System.Globalization.NumberStyles.None,
		System.Globalization.CultureInfo.InvariantCulture, out long id) ? id : null;

	public static string KeyFor(InstalledSource source, string id) => (source == InstalledSource.Epic ? "epic:" : "steam:") + id;
}

// One scan: the catalog games found (sorted by name), the other games found (FoundGame, sorted by name), how many launcher
// entries were left out as not games, and how long it took.
public sealed record InstalledScan(IReadOnlyList<InstalledGame> Games, TimeSpan Took)
{
	public static InstalledScan Empty { get; } = new([], TimeSpan.Zero);

	public IReadOnlyList<FoundGame> Found { get; init; } = [];

	public int NotGames { get; init; }

	// Catalog games and other found games from this source.
	public int Count(InstalledSource source) => Games.Count(g => g.Source == source) + Found.Count(g => g.Source == source);
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
// GameCatalog.MinNameLength characters. Besides the catalog games, the other games Steam and Epic report installed are
// returned as FoundGame (Steam's own tools and runtimes, SteamNonGames, and Epic add-ons and engines left out); a found game
// that is a catalog game found any way is left to the catalog row. Logs one summary line per scan (no paths).
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
		void Add(CatalogGame? game, InstalledSource source, string? folder, string? launchId = null, string? launchExe = null)
		{
			if (game != null)
			{
				found.TryAdd(game.Name, new InstalledGame(game, source, folder, launchId, launchExe));
			}
		}
		Dictionary<string, CatalogGame> names = NameIndex(catalog);
		var others = new List<FoundGame>();
		int notGames = 0;

		foreach (SteamApp app in SteamApps())
		{
			// The catalog by app id, else (an installed app) by its whole name or install folder name.
			CatalogGame? game = catalog.FindBySteamAppId(app.AppId)
				?? (app.Installed ? ByName(names, app.Name) ?? catalog.FindByInstallDir(app.InstallDir) : null);
			if (game != null)
			{
				Add(game, InstalledSource.Steam, app.Folder, app.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture));
			}
			else if (!app.Installed || !Safe(() => files.DirectoryExists(app.Folder)))
			{
				// Being downloaded, or the folder is gone: not on this PC yet.
			}
			else if (SteamNonGames.IsNonGame(app.AppId, app.Name))
			{
				notGames++;
			}
			else
			{
				others.Add(new FoundGame(app.Name, InstalledSource.Steam, app.Folder, app.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
			}
		}
		foreach (EpicEntry entry in EpicEntries())
		{
			// The launch exe first, then the display name, then the install folder's name.
			string? folderName = entry.Location == null ? null : Path.GetFileName(entry.Location);
			if ((catalog.Find(entry.Launch) ?? ByName(names, entry.Name) ?? ByName(names, folderName)) is { } game)
			{
				Add(game, InstalledSource.Epic, entry.Location, entry.AppName,
					entry.Location != null && !string.IsNullOrWhiteSpace(entry.Launch) ? Path.Combine(entry.Location, entry.Launch.TrimStart('\\')) : null);
			}
			else if (entry.NotGame)
			{
				notGames++;
			}
			else if (entry.Location != null && !string.IsNullOrWhiteSpace(entry.Launch) && !string.IsNullOrWhiteSpace(entry.Name))
			{
				others.Add(new FoundGame(entry.Name, InstalledSource.Epic, entry.Location, entry.AppName ?? GameCatalog.NormalizeName(entry.Name),
					Path.Combine(entry.Location, entry.Launch.TrimStart('\\'))));
			}
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

		// A found game that is a catalog game found another way (same folder or name) is shown once, as the catalog game.
		var catalogFolders = new HashSet<string>(found.Values.Where(g => g.Folder != null).Select(g => Trim(g.Folder!)), StringComparer.OrdinalIgnoreCase);
		var catalogNames = new HashSet<string>(found.Values.Select(g => GameCatalog.NormalizeName(g.Game.Name)), StringComparer.Ordinal);
		List<FoundGame> rest = others
			.Where(g => !catalogFolders.Contains(Trim(g.Folder)) && !catalogNames.Contains(GameCatalog.NormalizeName(g.Name)))
			.GroupBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
			.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

		watch.Stop();
		var scan = new InstalledScan(found.Values.OrderBy(g => g.Game.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), watch.Elapsed)
		{
			Found = rest,
			NotGames = notGames
		};
		log($"installed games: {scan.Games.Count + scan.Found.Count} found (steam {scan.Count(InstalledSource.Steam)}, epic {scan.Count(InstalledSource.Epic)}, " +
			$"registry {scan.Count(InstalledSource.Registry)}, folders {scan.Count(InstalledSource.Folder)}, seen {scan.Count(InstalledSource.Seen)}; " +
			$"{scan.Games.Count} in the list, {scan.Found.Count} others, {notGames} not games left out) in {(long)watch.Elapsed.TotalMilliseconds} ms");
		return scan;
	}

	private static string Trim(string path) => path.Replace('/', '\\').TrimEnd('\\');

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

	private IReadOnlyList<SteamApp> SteamApps()
	{
		try
		{
			return steam?.ReadAppsNow() ?? [];
		}
		catch (Exception e) when (IsSkippable(e))
		{
			return [];
		}
	}

	// One Epic Games Launcher manifest (*.item). Location: the install folder (null when the manifest has none); Launch: the
	// launch exe relative to it. NotGame: an add-on (MainGameAppName names another app), an engine or plug-in (AppCategories
	// without "games"), or an entry that cannot be started (bIsApplication or bIsExecutable false, no LaunchExecutable).
	private sealed record EpicEntry(string? Name, string? Location, string? Launch, string? AppName, bool NotGame);

	// Epic manifests whose install folder exists (or is not given) and that are fully installed.
	private IEnumerable<EpicEntry> EpicEntries()
	{
		if (string.IsNullOrEmpty(epicManifests))
		{
			yield break;
		}
		foreach (string manifest in List(() => files.Files(epicManifests, "*.item")))
		{
			EpicEntry? entry;
			try
			{
				entry = ParseEpic(files.ReadAllText(manifest));
			}
			catch (Exception e) when (e is JsonException || IsSkippable(e))
			{
				continue;
			}
			if (entry == null || (entry.Location != null && !Safe(() => files.DirectoryExists(entry.Location))))
			{
				continue;
			}
			yield return entry;
		}
	}

	// Null for a manifest that is not an object or whose install is incomplete. Throws JsonException on malformed JSON.
	private static EpicEntry? ParseEpic(string text)
	{
		using JsonDocument doc = JsonDocument.Parse(text);
		JsonElement root = doc.RootElement;
		if (root.ValueKind != JsonValueKind.Object || Flag(root, "bIsIncompleteInstall") == true)
		{
			return null;
		}
		string? location = Read(root, "InstallLocation") is { } l && !string.IsNullOrWhiteSpace(l) ? Trim(l.Trim()) : null;
		string? launch = Read(root, "LaunchExecutable")?.Trim().Replace('/', '\\');
		string? appName = Read(root, "AppName");
		string? mainGame = Read(root, "MainGameAppName");
		bool addOn = !string.IsNullOrWhiteSpace(mainGame) && !string.Equals(mainGame, appName, StringComparison.OrdinalIgnoreCase);
		bool notGameCategory = root.TryGetProperty("AppCategories", out JsonElement categories) && categories.ValueKind == JsonValueKind.Array
			&& categories.GetArrayLength() > 0
			&& !categories.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && string.Equals(c.GetString(), "games", StringComparison.OrdinalIgnoreCase));
		bool notGame = addOn || notGameCategory || Flag(root, "bIsApplication") == false || Flag(root, "bIsExecutable") == false
			|| string.IsNullOrWhiteSpace(launch);
		return new EpicEntry(Read(root, "DisplayName")?.Trim(), location, launch, string.IsNullOrWhiteSpace(appName) ? null : appName.Trim(), notGame);
	}

	private static string? Read(JsonElement root, string property) =>
		root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static bool? Flag(JsonElement root, string property) =>
		root.TryGetProperty(property, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

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
