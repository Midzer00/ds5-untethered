// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Kopru.Core.Steam;

namespace Kopru.Core.Games;

// A running process: its id, exe file name and, when readable, the full exe path.
public sealed record ProcessInfo(int Id, string Exe, string? Path = null);

// A game the user added by hand: a display name and the exe (full path, or just the file name).
public sealed record UserGame(string Name, string Path)
{
	[System.Text.Json.Serialization.JsonIgnore]
	public string Exe => GameCatalog.Normalize(Path);
}

// A known game in the foreground: a catalog game (by exe or by Steam library folder), a game the user added or a game found
// installed (Found). Key identifies the game for the per-game settings: the catalog name, else the found game's key, else
// the exe file name.
public sealed record ForegroundGame(string Exe, CatalogGame? Match, int ProcessId = 0, string? Path = null, string? UserName = null,
	FoundGame? Found = null, XboxTriggerGame? Trigger = null)
{
	public string Name => UserName ?? Match?.Name ?? Found?.Name ?? Trigger?.Name ?? DisplayName(Exe);

	// Trigger: recognised only by the Xbox trigger rumble list (XboxTriggerGame.Key).
	public string Key => Match == null && UserName == null && Found != null ? Found.Key
		: Match == null && UserName == null && Trigger != null ? Trigger.Key
		: KeyOf(Match, Exe);

	public static string KeyOf(CatalogGame? match, string exe) => match?.Name ?? GameCatalog.Normalize(exe);

	public static string DisplayName(string exe) =>
		exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
}

// The process side of the watcher (faked in tests).
public interface IProcessSource
{
	// The process of the foreground window, or null.
	ProcessInfo? Foreground();

	// Whether the process still runs (and is still the same exe).
	bool IsRunning(ProcessInfo process);

	// Running apps with a window (to pick a game to add).
	IReadOnlyList<ProcessInfo> WindowedApps();

	// Every running process by id and exe file name (paths left out: reading one opens the process). Used only while a
	// known game that would get another controller type is looked for (GameWatcher.FindRunning).
	IReadOnlyList<ProcessInfo> AllProcesses() => [];

	// The process with its full exe path when it can be read.
	ProcessInfo WithPath(ProcessInfo process) => process;
}

// What the watcher reads about a process beyond its exe name, to recognise a list game whose exe name is unknown (faked
// in tests). Both may return null; neither throws.
public interface IProcessDetails
{
	// The title of the process's window (the foreground window when it belongs to that process), or null.
	string? WindowTitle(ProcessInfo process);

	// ProductName and FileDescription from the version info of the exe at path.
	(string? Product, string? Description) VersionInfo(string path);
}

// Reports the known game in the foreground; GameModeController polls it about once a second. Unknown apps (browsers,
// chat, editors, anything not in the list) are never reported.
public interface IGameWatcher
{
	ForegroundGame? Poll();

	// Whether the game's process still runs.
	bool IsRunning(ForegroundGame game);

	// Running apps that could be added as games.
	IReadOnlyList<ProcessInfo> RunningApps() => [];

	// The games the user added (matched like catalog games).
	IReadOnlyList<UserGame> UserGames { get => []; set { } }

	// The other games found installed (matched by install folder, after every catalog match).
	IReadOnlyList<FoundGame> FoundGames { get => []; set { } }

	// A running process of one of these games (by game key; it need not be in front), or null. GameModeController asks only
	// while no game runs and some known game would get a controller type other than the current one (its own choice or
	// Automatic), so that game gets its type when its process appears, before it looks for controllers.
	ForegroundGame? FindRunning(IReadOnlySet<string> keys) => null;

	// The Steam app id of the library install folder the exe at path lies in ("<library>\steamapps\common\<installdir>"
	// mapped by its appmanifest), or null.
	long? SteamAppIdFor(string? path) => null;
}

// Watches the foreground window's process and recognises known games only: (a) the exe matches a catalog exe (an Unreal
// Engine "-Win64-Shipping" pair included), (b) the exe runs from a Steam library folder whose app id is in the catalog,
// (c) it is a game the user added, or (d) for any catalog game (one with exe names too: Steam may start a launcher or
// anti-cheat stub, a copy may run another exe), the whole normalised game name (or the game's Steam install folder name,
// for folders only) equals a folder name of the exe's path, the exe's product name or file description, or the window title,
// (e) a game found installed (FoundGames): Steam ones by an exe anywhere under their install folder, Epic ones by their launch
// exe or an exe under their install folder. (e) comes after every catalog signal: a found game never shadows a catalog game
// (the window title is checked once before a found game is reported). Lookups are cached
// per process (id and path): version info is read once per process; only the window title of an unknown process is read
// again (games often set it after the window opens). Ignores this app and common desktop apps. details defaults to
// processes when that implements IProcessDetails (SystemProcessSource does). Each recognition is logged once per process
// ("game: <name> recognised by <signal> (<exe file name>)", never a path).
public sealed class GameWatcher(GameCatalog catalog, IProcessSource? processes = null, SteamLibrary? steam = null,
	IProcessDetails? details = null, Action<string>? log = null) : IGameWatcher
{
	// Shell and system processes and common desktop apps: never games, even when a list entry names them.
	public static readonly IReadOnlySet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"explorer.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe", "SearchApp.exe",
		"LockApp.exe", "TextInputHost.exe", "dwm.exe", "ApplicationFrameHost.exe", "SystemSettings.exe", "Taskmgr.exe",
		"ShellHost.exe", "WidgetBoard.exe", "Widgets.exe",
		"claude.exe", "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "discord.exe", "steam.exe",
		"steamwebhelper.exe", "EpicGamesLauncher.exe", "Code.exe", "devenv.exe", "rider64.exe", "WindowsTerminal.exe",
		"cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "Spotify.exe", "slack.exe", "Teams.exe", "ms-teams.exe",
		"outlook.exe", "winword.exe", "excel.exe", "obs64.exe", "NVIDIA Overlay.exe", "nvcontainer.exe", "GameBar.exe",
		"XboxPcApp.exe"
	};

	private const int CacheLimit = 256;

	private readonly IProcessSource processes = processes ?? new SystemProcessSource();
	private readonly string ownExe = GameCatalog.Normalize(Environment.ProcessPath);
	private readonly object gate = new();
	private readonly Action<string> log = log ?? Diagnostics.AppLog.Write;
	private readonly Dictionary<(int, string), Entry> cache = [];
	private GameCatalog current = catalog;
	private IReadOnlyList<UserGame> userGames = [];
	private IReadOnlyList<FoundGame> foundGames = [];

	// Found games, deepest install folder first (a folder nested in another one wins).
	public IReadOnlyList<FoundGame> FoundGames
	{
		get
		{
			lock (gate)
			{
				return foundGames;
			}
		}
		set
		{
			lock (gate)
			{
				foundGames = value.Where(g => !string.IsNullOrWhiteSpace(g.Folder)).OrderByDescending(g => g.Folder.TrimEnd('\\').Length).ToList();
				cache.Clear();
			}
		}
	}

	// Replaced when the supported games list is updated (GameModeController.SetCatalog).
	public GameCatalog Catalog
	{
		get
		{
			lock (gate)
			{
				return current;
			}
		}
		set
		{
			lock (gate)
			{
				current = value;
				cache.Clear();
			}
		}
	}

	public IReadOnlyList<UserGame> UserGames
	{
		get
		{
			lock (gate)
			{
				return userGames;
			}
		}
		set
		{
			lock (gate)
			{
				userGames = value.ToList();
				cache.Clear();
			}
		}
	}

	public ForegroundGame? Poll()
	{
		ProcessInfo? process;
		try
		{
			process = processes.Foreground();
		}
		catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
		{
			return null;
		}
		return process == null ? null : Recognize(process);
	}

	// The known game this process is, or null.
	public ForegroundGame? Recognize(ProcessInfo process)
	{
		string exe = GameCatalog.Normalize(process.Exe);
		if (exe.Length == 0 || string.Equals(exe, ownExe, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		(int, string) key = (process.Id, process.Path ?? exe);
		lock (gate)
		{
			if (cache.TryGetValue(key, out Entry? known))
			{
				if (known.Game == null && known.ReadTitle)
				{
					known.Game = ByTitle(process, exe, known);
				}
				return known.Game;
			}
			if (cache.Count >= CacheLimit)
			{
				cache.Clear();
			}
			known = Lookup(process, exe);
			cache[key] = known;
			return known.Game;
		}
	}

	public bool IsRunning(ForegroundGame game)
	{
		try
		{
			return processes.IsRunning(new ProcessInfo(game.ProcessId, game.Exe, game.Path));
		}
		catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
		{
			return false;
		}
	}

	// Cheap by name first: only a process whose exe file name could be one of these games (a catalog exe, a game the user
	// added, a found game's launch exe or an exe in its install folder) has its path read and goes through Recognize.
	public ForegroundGame? FindRunning(IReadOnlySet<string> keys)
	{
		if (keys.Count == 0)
		{
			return null;
		}
		IReadOnlyList<ProcessInfo> all;
		try
		{
			all = processes.AllProcesses();
		}
		catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return null;
		}
		HashSet<string> foundNames = FoundExeNames(keys);
		foreach (ProcessInfo process in all)
		{
			string exe = GameCatalog.Normalize(process.Exe);
			if (exe.Length == 0 || !CouldBe(exe, keys, foundNames))
			{
				continue;
			}
			ForegroundGame? game;
			try
			{
				game = Recognize(process.Path == null ? processes.WithPath(process) : process);
			}
			catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
			{
				continue;
			}
			if (game != null && keys.Contains(game.Key))
			{
				return game;
			}
		}
		return null;
	}

	private bool CouldBe(string exe, IReadOnlySet<string> keys, HashSet<string> foundNames)
	{
		if (foundNames.Contains(exe))
		{
			return true;
		}
		lock (gate)
		{
			CatalogGame? match = current.Find(exe);
			if (match != null && keys.Contains(match.Name))
			{
				return true;
			}
			return userGames.Any(g => string.Equals(g.Exe, exe, StringComparison.OrdinalIgnoreCase) && keys.Contains(ForegroundGame.KeyOf(match, exe)));
		}
	}

	private readonly Dictionary<string, string[]> folderExes = new(StringComparer.OrdinalIgnoreCase);

	// The exe file names the found games with these keys can run as: the launch exe, else the exes in the install folder
	// (read once per folder, three levels deep, at most 200).
	private HashSet<string> FoundExeNames(IReadOnlySet<string> keys)
	{
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<FoundGame> wanted;
		lock (gate)
		{
			wanted = foundGames.Where(g => keys.Contains(g.Key)).ToList();
		}
		foreach (FoundGame game in wanted)
		{
			if (game.LaunchExe != null)
			{
				names.Add(GameCatalog.Normalize(game.LaunchExe));
			}
			foreach (string exe in ExesIn(game.Folder))
			{
				names.Add(exe);
			}
		}
		return names;
	}

	private string[] ExesIn(string folder)
	{
		lock (gate)
		{
			if (folderExes.TryGetValue(folder, out string[]? known))
			{
				return known;
			}
		}
		string[] exes;
		try
		{
			exes = Directory.Exists(folder)
				? Directory.EnumerateFiles(folder, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
					.Take(200).Select(GameCatalog.Normalize).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
				: [];
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
		{
			exes = [];
		}
		lock (gate)
		{
			folderExes[folder] = exes;
		}
		return exes;
	}

	public IReadOnlyList<ProcessInfo> RunningApps()
	{
		try
		{
			return processes.WindowedApps()
				.Where(p => !Ignored.Contains(GameCatalog.Normalize(p.Exe)) && !string.Equals(GameCatalog.Normalize(p.Exe), ownExe, StringComparison.OrdinalIgnoreCase))
				.GroupBy(p => p.Path ?? p.Exe, StringComparer.OrdinalIgnoreCase)
				.Select(g => g.First())
				.OrderBy(p => p.Exe, StringComparer.CurrentCultureIgnoreCase)
				.ToList();
		}
		catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return [];
		}
	}

	// Caller holds the lock.
	private Entry Lookup(ProcessInfo process, string exe)
	{
		foreach (UserGame game in userGames)
		{
			if (UserGameMatches(game, process, exe))
			{
				log($"game: {game.Name} recognised as a game you added ({exe})");
				return new Entry(new ForegroundGame(exe, current.Find(exe), process.Id, process.Path, game.Name));
			}
		}
		if (Ignored.Contains(exe))
		{
			return new Entry(null);
		}
		if (current.Find(exe) is { } match)
		{
			return new Entry(Found(match, "exe name", process, exe));
		}
		if (steam?.AppIdFor(process.Path) is long appId && current.FindBySteamAppId(appId) is { } byApp)
		{
			return new Entry(Found(byApp, "Steam library folder", process, exe));
		}
		return ByName(process, exe);
	}

	private IProcessDetails? Details => details ?? processes as IProcessDetails;

	// Steam's files are read again at most once a minute (SteamLibrary.RefreshInterval); null on any problem.
	public long? SteamAppIdFor(string? path)
	{
		try
		{
			return steam?.AppIdFor(path);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
		{
			return null;
		}
	}

	// Caller holds the lock. A catalog game by its name: folder name, version info, then window title; a found game by its
	// install folder before the window title (the title is still checked once, so a catalog game wins).
	private Entry ByName(ProcessInfo process, string exe)
	{
		(FoundGame Game, string Signal)? found = FoundByFolder(process.Path);
		if (!current.HasNamedGames)
		{
			return new Entry(found is { } only ? FoundOne(only.Game, only.Signal, process, exe) : ByTrigger(process, exe, null, null));
		}
		foreach (string folder in Folders(process.Path))
		{
			if (current.FindByName(folder) is { } byFolder)
			{
				return new Entry(Found(byFolder, "folder name", process, exe));
			}
			if (current.FindByInstallDir(folder) is { } byInstallDir)
			{
				return new Entry(Found(byInstallDir, "install folder name", process, exe));
			}
		}
		IProcessDetails? source = Details;
		string? product = null, description = null;
		if (source != null && !string.IsNullOrEmpty(process.Path))
		{
			(product, description) = source.VersionInfo(process.Path);
			if (current.FindByName(product) is { } byProduct)
			{
				return new Entry(Found(byProduct, "product name", process, exe));
			}
			if (current.FindByName(description) is { } byDescription)
			{
				return new Entry(Found(byDescription, "file description", process, exe));
			}
		}
		if (found is { } other)
		{
			if (current.FindByName(source?.WindowTitle(process)) is { } byTitleFirst)
			{
				return new Entry(Found(byTitleFirst, "window title", process, exe));
			}
			return new Entry(FoundOne(other.Game, other.Signal, process, exe));
		}
		if (ByTrigger(process, exe, product, description) is { } trigger)
		{
			return new Entry(trigger);
		}
		if (source == null)
		{
			return new Entry(null);
		}
		var entry = new Entry(null) { ReadTitle = true };
		entry.Game = ByTitle(process, exe, entry);
		return entry;
	}

	// Caller holds the lock. The found game whose install folder holds the exe (deepest folder first): an Epic launch exe
	// exactly, else any exe under the folder. Needs the full path.
	private (FoundGame, string)? FoundByFolder(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || foundGames.Count == 0 || !System.IO.Path.IsPathRooted(path))
		{
			return null;
		}
		string full = path.Replace('/', '\\');
		foreach (FoundGame game in foundGames)
		{
			if (game.LaunchExe != null && string.Equals(full, game.LaunchExe.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
			{
				return (game, "Epic launch exe");
			}
		}
		foreach (FoundGame game in foundGames)
		{
			if (full.StartsWith(game.Folder.Replace('/', '\\').TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
			{
				return (game, game.Source == InstalledSource.Epic ? "Epic install folder" : "Steam install folder");
			}
		}
		return null;
	}

	private ForegroundGame FoundOne(FoundGame game, string signal, ProcessInfo process, string exe)
	{
		log($"game: {game.Name} recognised by {signal} ({exe})");
		return new ForegroundGame(exe, null, process.Id, process.Path, Found: game);
	}

	// Caller holds the lock. Reads the window title again and checks it only when it changed.
	private ForegroundGame? ByTitle(ProcessInfo process, string exe, Entry entry)
	{
		string? title = Details?.WindowTitle(process);
		if (string.Equals(title, entry.Title, StringComparison.Ordinal))
		{
			return null;
		}
		entry.Title = title;
		return current.FindByName(title) is { } byTitle ? Found(byTitle, "window title", process, exe)
			: triggers.FindByName(title) is { } trigger ? TriggerOne(trigger, "window title", process, exe)
			: null;
	}

	private ForegroundGame Found(CatalogGame game, string signal, ProcessInfo process, string exe)
	{
		log($"game: {game.Name} recognised by {signal} ({exe})");
		return new ForegroundGame(exe, game, process.Id, process.Path);
	}

	// The games that send Xbox trigger rumble (XboxTriggerGames): recognised after every catalog and found-game signal, by
	// exe name, the Steam library folder's app id, a folder name, the exe's product name or file description, and (in
	// ByTitle) the window title. Set by GameModeController.
	public XboxTriggerGames TriggerGames
	{
		get
		{
			lock (gate)
			{
				return triggers;
			}
		}
		set
		{
			lock (gate)
			{
				triggers = value;
				cache.Clear();
			}
		}
	}

	private XboxTriggerGames triggers = XboxTriggerGames.Empty;

	// Caller holds the lock.
	private ForegroundGame? ByTrigger(ProcessInfo process, string exe, string? product, string? description)
	{
		if (triggers.Count == 0)
		{
			return null;
		}
		if (triggers.FindByExe(exe) is { } byExe)
		{
			return TriggerOne(byExe, "exe name", process, exe);
		}
		if (steam?.AppIdFor(process.Path) is long appId && triggers.FindBySteamAppId(appId) is { } byApp)
		{
			return TriggerOne(byApp, "Steam library folder", process, exe);
		}
		foreach (string folder in Folders(process.Path))
		{
			if (triggers.FindByName(folder) is { } byFolder)
			{
				return TriggerOne(byFolder, "folder name", process, exe);
			}
		}
		return triggers.FindByName(product) is { } byProduct ? TriggerOne(byProduct, "product name", process, exe)
			: triggers.FindByName(description) is { } byDescription ? TriggerOne(byDescription, "file description", process, exe)
			: null;
	}

	private ForegroundGame TriggerOne(XboxTriggerGame game, string signal, ProcessInfo process, string exe)
	{
		log($"game: {game.Name} recognised from the Xbox trigger rumble list by {signal} ({exe})");
		return new ForegroundGame(exe, null, process.Id, process.Path, Trigger: game);
	}

	// The folder names of an exe path, innermost first, without the drive (C:\A\B\x.exe gives B, then A).
	public static IEnumerable<string> Folders(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			yield break;
		}
		string? folder = System.IO.Path.GetDirectoryName(path.Replace('/', '\\'));
		while (!string.IsNullOrEmpty(folder))
		{
			string name = System.IO.Path.GetFileName(folder);
			if (name.Length == 0)
			{
				yield break;
			}
			yield return name;
			folder = System.IO.Path.GetDirectoryName(folder);
		}
	}

	// One cached lookup. ReadTitle: an unknown process whose window title is checked again on each poll.
	private sealed class Entry(ForegroundGame? game)
	{
		public ForegroundGame? Game { get; set; } = game;

		public bool ReadTitle { get; init; }

		public string? Title { get; set; }
	}

	// A full path must match exactly; a bare file name (or a process whose path is unknown) matches by file name.
	public static bool UserGameMatches(UserGame game, ProcessInfo process, string exe)
	{
		bool full = System.IO.Path.IsPathRooted(game.Path);
		return full && process.Path != null
			? string.Equals(Full(game.Path), Full(process.Path), StringComparison.OrdinalIgnoreCase)
			: string.Equals(game.Exe, exe, StringComparison.OrdinalIgnoreCase);
	}

	private static string Full(string path)
	{
		try
		{
			return System.IO.Path.GetFullPath(path.Replace('/', '\\'));
		}
		catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return path;
		}
	}
}

// Windows: GetForegroundWindow, GetWindowThreadProcessId, then the process (name and, with limited query rights, the path).
public sealed class SystemProcessSource : IProcessSource, IProcessDetails
{
	private const uint QueryLimitedInformation = 0x1000;
	private const uint StillActive = 259;

	public ProcessInfo? Foreground()
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}
		IntPtr window = GetForegroundWindow();
		if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out uint pid) == 0 || pid == 0
			|| pid == (uint)Environment.ProcessId)
		{
			return null;
		}
		using Process process = Process.GetProcessById((int)pid);
		return new ProcessInfo((int)pid, process.ProcessName + ".exe", PathOf((int)pid));
	}

	public bool IsRunning(ProcessInfo process)
	{
		if (!OperatingSystem.IsWindows() || process.Id <= 0)
		{
			return false;
		}
		IntPtr handle = OpenProcess(QueryLimitedInformation, false, (uint)process.Id);
		if (handle == IntPtr.Zero)
		{
			return false;
		}
		try
		{
			if (!GetExitCodeProcess(handle, out uint code) || code != StillActive)
			{
				return false;
			}
			// A reused process id belongs to another exe.
			string? path = Read(handle);
			return path == null || process.Path == null || string.Equals(path, process.Path, StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	public IReadOnlyList<ProcessInfo> WindowedApps()
	{
		var list = new List<ProcessInfo>();
		foreach (Process p in Process.GetProcesses())
		{
			using (p)
			{
				try
				{
					if (p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero)
					{
						list.Add(new ProcessInfo(p.Id, p.ProcessName + ".exe", PathOf(p.Id)));
					}
				}
				catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
				{
					// Exited meanwhile or not readable.
				}
			}
		}
		return list;
	}

	public IReadOnlyList<ProcessInfo> AllProcesses()
	{
		var list = new List<ProcessInfo>();
		foreach (Process p in Process.GetProcesses())
		{
			using (p)
			{
				try
				{
					if (p.Id != Environment.ProcessId && p.Id > 4)
					{
						list.Add(new ProcessInfo(p.Id, p.ProcessName + ".exe"));
					}
				}
				catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
				{
					// Exited meanwhile.
				}
			}
		}
		return list;
	}

	public ProcessInfo WithPath(ProcessInfo process) => process with { Path = PathOf(process.Id) };

	public string? WindowTitle(ProcessInfo process)
	{
		if (!OperatingSystem.IsWindows() || process.Id <= 0)
		{
			return null;
		}
		IntPtr window = GetForegroundWindow();
		if (window != IntPtr.Zero && GetWindowThreadProcessId(window, out uint pid) != 0 && pid == (uint)process.Id)
		{
			var buffer = new StringBuilder(512);
			int length = GetWindowText(window, buffer, buffer.Capacity);
			return length > 0 ? buffer.ToString(0, length) : null;
		}
		try
		{
			using Process p = Process.GetProcessById(process.Id);
			return string.IsNullOrEmpty(p.MainWindowTitle) ? null : p.MainWindowTitle;
		}
		catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
		{
			return null;
		}
	}

	public (string? Product, string? Description) VersionInfo(string path)
	{
		try
		{
			FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
			return (info.ProductName, info.FileDescription);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return (null, null);
		}
	}

	// The full exe path, or null when the process can't be queried.
	public static string? PathOf(int pid)
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}
		IntPtr handle = OpenProcess(QueryLimitedInformation, false, (uint)pid);
		if (handle == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			return Read(handle);
		}
		finally
		{
			CloseHandle(handle);
		}
	}

	private static string? Read(IntPtr handle)
	{
		var buffer = new StringBuilder(1024);
		int size = buffer.Capacity;
		return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
	}

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
	private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);
}
