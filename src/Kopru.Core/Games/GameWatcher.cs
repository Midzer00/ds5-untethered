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

// A known game in the foreground: a catalog game (by exe or by Steam library folder) or a game the user added. Key
// identifies the game for the "always" list: the catalog name, else the exe file name.
public sealed record ForegroundGame(string Exe, CatalogGame? Match, int ProcessId = 0, string? Path = null, string? UserName = null)
{
	public string Name => UserName ?? Match?.Name ?? DisplayName(Exe);

	public string Key => KeyOf(Match, Exe);

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
}

// Watches the foreground window's process and recognises known games only: (a) the exe matches a catalog exe (an Unreal
// Engine "-Win64-Shipping" pair included), (b) the exe runs from a Steam library folder whose app id is in the catalog,
// (c) it is a game the user added, or (d) for a catalog game without exe names, the whole normalised game name equals a
// folder name of the exe's path, the exe's product name or file description, or the window title. Lookups are cached
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

	// Caller holds the lock. A catalog game without exe names: by folder name, version info, then window title.
	private Entry ByName(ProcessInfo process, string exe)
	{
		if (!current.HasNamedGames)
		{
			return new Entry(null);
		}
		foreach (string folder in Folders(process.Path))
		{
			if (current.FindByName(folder) is { } byFolder)
			{
				return new Entry(Found(byFolder, "folder name", process, exe));
			}
		}
		IProcessDetails? source = Details;
		if (source == null)
		{
			return new Entry(null);
		}
		if (!string.IsNullOrEmpty(process.Path))
		{
			(string? product, string? description) = source.VersionInfo(process.Path);
			if (current.FindByName(product) is { } byProduct)
			{
				return new Entry(Found(byProduct, "product name", process, exe));
			}
			if (current.FindByName(description) is { } byDescription)
			{
				return new Entry(Found(byDescription, "file description", process, exe));
			}
		}
		var entry = new Entry(null) { ReadTitle = true };
		entry.Game = ByTitle(process, exe, entry);
		return entry;
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
		return current.FindByName(title) is { } byTitle ? Found(byTitle, "window title", process, exe) : null;
	}

	private ForegroundGame Found(CatalogGame game, string signal, ProcessInfo process, string exe)
	{
		log($"game: {game.Name} recognised by {signal} ({exe})");
		return new ForegroundGame(exe, game, process.Id, process.Path);
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
