// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Kopru.Core.Games;

namespace Kopru.Platform.Linux.Games;

// Game detection on Linux without a foreground window (Wayland does not tell other apps which window is in front): the
// "game in front" is a running Steam game, found by scanning /proc for a process whose environment has SteamAppId,
// STEAM_COMPAT_APP_ID or SteamGameId (Steam sets them for the games it starts, Proton ones included). Its exe name is the
// Windows exe from the command line for Proton games ("...\Game.exe"), else the Linux executable's name.
// Cost: the scan runs at most every ScanInterval (the game watcher asks every second); only our own processes are looked
// at (others' environ can't be read anyway); what a process is (its app id and exe) is read once per process, keyed by its
// pid and start time, and the environment is searched as raw bytes. Nothing here needs root. The folder is a parameter so
// tests can use a fake tree.
public sealed class LinuxProcessSource : IProcessSource
{
	public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(3);

	private static readonly byte[][] AppIdKeys = [.. new[] { "SteamAppId=", "STEAM_COMPAT_APP_ID=", "SteamGameId=" }.Select(Encoding.ASCII.GetBytes)];

	// Helpers Steam, its runtime, Proton and common wrappers run next to a game; never the game itself.
	private static readonly HashSet<string> Helpers = new(StringComparer.OrdinalIgnoreCase)
	{
		"steam", "steamwebhelper", "steam-runtime-launcher-service", "steam-runtime-supervisor", "pressure-vessel-wrap", "pv-bwrap",
		"pv-adverb", "bwrap", "srt-bwrap", "reaper", "wineserver", "wine", "wine64", "wine-preloader", "wine64-preloader", "proton",
		"python3", "sh", "bash", "steam-launch-wrapper", "gameoverlayui", "gamescope", "mangohud", "gamemoderun", "explorer.exe",
		"services.exe", "winedevice.exe", "plugplay.exe", "rpcss.exe", "tabtip.exe", "svchost.exe", "conhost.exe", "start.exe",
		"steam.exe", "rundll32.exe", "wineboot.exe", "winemenubuilder.exe"
	};

	private sealed record Known(long? AppId, ProcessInfo? Info);

	private readonly string proc;
	private readonly Func<string, bool> canRead;
	private readonly Func<long> clockMs;
	private readonly object gate = new();
	private readonly Dictionary<(int Pid, long Start), Known> known = [];
	private IReadOnlyList<ProcessInfo> lastScan = [];
	private long lastScanAt = long.MinValue;

	// canRead: whether our user may read /proc/<pid>/environ (by default access(2): only our own processes, or every one
	// as root). clockMs: for the scan interval (tests).
	public LinuxProcessSource(string proc = "/proc", Func<string, bool>? canRead = null, Func<long>? clockMs = null)
	{
		this.proc = proc;
		this.canRead = canRead ?? DefaultCanRead;
		this.clockMs = clockMs ?? (() => Environment.TickCount64);
	}

	private static bool DefaultCanRead(string path) =>
		!OperatingSystem.IsLinux() || LibC.Access(path, LibC.ReadOk) == 0;

	public ProcessInfo? Foreground() => SteamGames().FirstOrDefault();

	public bool IsRunning(ProcessInfo process) =>
		process.Id > 0 && Describe(process.Id) is { } now && string.Equals(now.Exe, process.Exe, StringComparison.OrdinalIgnoreCase);

	public IReadOnlyList<ProcessInfo> WindowedApps() => SteamGames();

	public IReadOnlyList<ProcessInfo> AllProcesses() => [.. Pids().Select(Describe).OfType<ProcessInfo>()];

	public ProcessInfo WithPath(ProcessInfo process) => process.Path != null ? process : Describe(process.Id) ?? process;

	// Every readable process with a Steam app id, newest first (a game's launcher usually starts before the game). Scanned
	// again only after ScanInterval.
	public IReadOnlyList<ProcessInfo> SteamGames()
	{
		lock (gate)
		{
			long now = clockMs();
			if (lastScanAt != long.MinValue && now - lastScanAt < (long)ScanInterval.TotalMilliseconds)
			{
				return lastScan;
			}
			lastScanAt = now;
			lastScan = Scan();
			return lastScan;
		}
	}

	// Caller holds the lock.
	private List<ProcessInfo> Scan()
	{
		List<ProcessInfo> games = [];
		HashSet<(int, long)> alive = [];
		foreach (int pid in Pids().OrderByDescending(p => p))
		{
			if (pid == Environment.ProcessId)
			{
				continue;
			}
			string dir = Path.Combine(proc, pid.ToString(CultureInfo.InvariantCulture));
			if (!canRead(Path.Combine(dir, "environ")))
			{
				continue;
			}
			long start = StartTime(dir);
			(int, long) key = (pid, start);
			alive.Add(key);
			if (!known.TryGetValue(key, out Known? entry))
			{
				long? appId = SteamAppId(pid);
				ProcessInfo? info = appId is long id && Describe(pid) is { } described && !IsHelper(described.Exe) ? described with { SteamAppId = id } : null;
				entry = new Known(appId, info);
				known[key] = entry;
			}
			if (entry.Info != null)
			{
				games.Add(entry.Info);
			}
		}
		foreach ((int, long) gone in known.Keys.Where(k => !alive.Contains(k)).ToList())
		{
			known.Remove(gone);
		}
		return games;
	}

	// Field 22 of /proc/<pid>/stat (start time in clock ticks since boot): with the pid, it names one process for good.
	// 0 when it can't be read (the pid alone then).
	private static long StartTime(string dir)
	{
		byte[]? stat = ReadBytes(Path.Combine(dir, "stat"));
		if (stat == null)
		{
			return 0;
		}
		string text = Encoding.ASCII.GetString(stat);
		// The command name in parentheses may hold spaces: count the fields after its closing parenthesis.
		int close = text.LastIndexOf(')');
		string[] fields = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
		// After ")": state is field 3, so start time (22) is index 19.
		return fields.Length > 19 && long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out long start) ? start : 0;
	}

	private static bool IsHelper(string exe) => Helpers.Contains(exe) || Helpers.Contains(Path.GetFileNameWithoutExtension(exe));

	private List<int> Pids()
	{
		List<int> pids = [];
		try
		{
			foreach (string entry in Directory.EnumerateDirectories(proc))
			{
				if (int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
				{
					pids.Add(pid);
				}
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// No /proc: nothing found.
		}
		return pids;
	}

	public long? SteamAppId(int pid)
	{
		byte[]? environ = ReadBytes(Path.Combine(proc, pid.ToString(CultureInfo.InvariantCulture), "environ"));
		return environ == null ? null : ParseSteamAppId(environ);
	}

	public static long? ParseSteamAppId(string environ) => ParseSteamAppId(Encoding.UTF8.GetBytes(environ));

	// The first of SteamAppId, STEAM_COMPAT_APP_ID, SteamGameId (a positive number) in a NUL-separated environment block,
	// searched as bytes (no strings are made for the other variables).
	public static long? ParseSteamAppId(ReadOnlySpan<byte> environ)
	{
		foreach (byte[] key in AppIdKeys)
		{
			int from = 0;
			while (from < environ.Length)
			{
				int at = environ[from..].IndexOf(key);
				if (at < 0)
				{
					break;
				}
				at += from;
				// Only at the start of a variable (the block's start or right after a NUL).
				if (at == 0 || environ[at - 1] == 0)
				{
					ReadOnlySpan<byte> value = environ[(at + key.Length)..];
					int end = value.IndexOf((byte)0);
					value = end < 0 ? value : value[..end];
					if (long.TryParse(Encoding.ASCII.GetString(value), NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0)
					{
						return id;
					}
					break;
				}
				from = at + 1;
			}
		}
		return null;
	}

	// The exe name and path of a process: a Windows exe in its command line (Proton), else /proc/<pid>/exe, else comm.
	public ProcessInfo? Describe(int pid)
	{
		string dir = Path.Combine(proc, pid.ToString(CultureInfo.InvariantCulture));
		byte[]? cmdline = ReadBytes(Path.Combine(dir, "cmdline"));
		if (cmdline is { Length: > 0 } && WindowsExe(Encoding.UTF8.GetString(cmdline)) is { } windows)
		{
			return new ProcessInfo(pid, windows.Name, windows.Path);
		}
		string? exe = null;
		try
		{
			exe = new FileInfo(Path.Combine(dir, "exe")).LinkTarget;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// Another user's process.
		}
		if (exe == null)
		{
			string? comm = ReadBytes(Path.Combine(dir, "comm")) is { } c ? Encoding.UTF8.GetString(c).Trim() : null;
			return string.IsNullOrEmpty(comm) ? null : new ProcessInfo(pid, comm);
		}
		return new ProcessInfo(pid, Path.GetFileName(exe), exe);
	}

	// The first argument ending in ".exe" (not a Wine or Steam helper) in a NUL-separated command line: its file name, and
	// its path with Wine's "Z:" drive turned back into the Linux path (other drives are kept, with forward slashes).
	public static (string Name, string Path)? WindowsExe(string cmdline)
	{
		foreach (string arg in cmdline.Split('\0', StringSplitOptions.RemoveEmptyEntries))
		{
			string trimmed = arg.Trim().Trim('"');
			if (!trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string unix = trimmed.Length > 2 && trimmed[0] is ('Z' or 'z') && trimmed[1] == ':' ? trimmed[2..] : trimmed;
			unix = unix.Replace('\\', '/');
			int slash = unix.LastIndexOf('/');
			string name = slash >= 0 ? unix[(slash + 1)..] : unix;
			if (name.Length == 0 || IsHelper(name))
			{
				continue;
			}
			return (name, unix);
		}
		return null;
	}

	private static byte[]? ReadBytes(string path)
	{
		try
		{
			return File.ReadAllBytes(path);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
