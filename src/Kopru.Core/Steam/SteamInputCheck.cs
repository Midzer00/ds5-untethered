// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.Win32;

namespace Kopru.Core.Steam;

public enum SteamInputStatus
{
	NotInstalled,
	Off,
	OnGlobally,
	OnForApp,
	Unknown
}

public sealed record SteamInputResult(SteamInputStatus Status, bool SteamRunning, string Advice);

// Registry/file/process access; faked in tests.
public interface ISteamEnvironment
{
	string? SteamPath { get; }
	bool IsSteamRunning { get; }
	bool FileExists(string path);
	string ReadAllText(string path);
	IEnumerable<string> GetDirectories(string path);
	DateTime GetLastWriteTimeUtc(string path);

	// Files in a folder matching a pattern such as "appmanifest_*.acf" (none when the folder is missing).
	IEnumerable<string> GetFiles(string path, string pattern) => [];

	// The file's size in bytes, -1 when unknown (with the write time it tells whether a file changed).
	long GetFileLength(string path) => -1;
}

// Whether a process runs, without listing every process each time: once one was found its handle is kept and asked
// (cheap) until it exits; while none runs the processes are listed at most once per ScanInterval.
public sealed class ProcessPresence(Func<Func<bool>?> find, Func<long>? clockMs = null)
{
	public static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(1);

	private readonly Func<long> clock = clockMs ?? (() => Environment.TickCount64);
	private readonly object gate = new();
	private Func<bool>? alive;
	private long nextScanAt = long.MinValue;

	// How many times the processes were listed (tests).
	public int Scans { get; private set; }

	public bool IsRunning
	{
		get
		{
			lock (gate)
			{
				if (alive != null)
				{
					bool running;
					try
					{
						running = alive();
					}
					catch (Exception)
					{
						running = false;
					}
					if (running)
					{
						return true;
					}
					// It exited: look again at once (a restart is found without waiting a whole interval).
					alive = null;
					nextScanAt = long.MinValue;
				}
				long now = clock();
				if (nextScanAt != long.MinValue && now < nextScanAt)
				{
					return false;
				}
				nextScanAt = now + (long)ScanInterval.TotalMilliseconds;
				Scans++;
				try
				{
					alive = find();
				}
				catch (Exception)
				{
					alive = null;
				}
				return alive != null;
			}
		}
	}
}

public sealed class SystemSteamEnvironment : ISteamEnvironment
{
	private readonly ProcessPresence steam = new(FindSteam);

	public string? SteamPath
	{
		get
		{
			if (!OperatingSystem.IsWindows())
			{
				return null;
			}
			try
			{
				using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
				return key?.GetValue("SteamPath") is string path && path.Length > 0 ? path.Replace('/', '\\') : null;
			}
			catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
			{
				return null;
			}
		}
	}

	public bool IsSteamRunning => steam.IsRunning;

	// The first steam.exe found, kept open; its "still running" check is one wait on the process handle.
	private static Func<bool>? FindSteam()
	{
		Process[] processes = Process.GetProcessesByName("steam");
		Process? kept = null;
		foreach (Process p in processes)
		{
			if (kept == null)
			{
				try
				{
					// Opens the handle now.
					_ = p.HasExited;
					kept = p;
					continue;
				}
				catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
				{
					// Running, but its handle cannot be kept: the next check lists the processes again (as before).
					p.Dispose();
					foreach (Process rest in processes)
					{
						rest.Dispose();
					}
					return () => false;
				}
			}
			p.Dispose();
		}
		if (kept == null)
		{
			return null;
		}
		return () =>
		{
			if (!kept.HasExited)
			{
				return true;
			}
			kept.Dispose();
			return false;
		};
	}

	public bool FileExists(string path) => File.Exists(path);
	public long GetFileLength(string path) => new FileInfo(path).Length;
	public string ReadAllText(string path) => File.ReadAllText(path);
	public IEnumerable<string> GetDirectories(string path) => Directory.Exists(path) ? Directory.GetDirectories(path) : [];
	public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
	public IEnumerable<string> GetFiles(string path, string pattern) => Directory.Exists(path) ? Directory.GetFiles(path, pattern) : [];
}

// Detects whether Steam Input may grab the controller. What it relies on (from community knowledge of Steam's
// files, not an official spec; verify on a real install):
//  - HKCU\Software\Valve\Steam "SteamPath": install folder (high confidence).
//  - <Steam>\config\config.vdf, InstallConfigStore/Software/Valve/Steam "SteamController_PSSupport":
//    global "PlayStation controller support": "0" off, "1" on for games without PS support, "2" on for all
//    (medium confidence; key missing on installs that never changed it, so we report Unknown).
//    The same key under UserLocalConfigStore in localconfig.vdf, when present, is treated as the user's value.
//  - <Steam>\userdata\<id>\config\localconfig.vdf, UserLocalConfigStore/Apps/<appid> "UseSteamControllerConfig":
//    per-game override: "0" forced off, "1" follow global (default), "2" forced on (medium-low confidence).
//    With several Steam accounts the most recently written localconfig.vdf is used.
public sealed class SteamInputCheck(ISteamEnvironment environment)
{
	public const string GlobalPsKey = "SteamController_PSSupport";
	public const string PerAppKey = "UseSteamControllerConfig";

	// The values this check needs from one config file, kept until the file's write time or size changes (the files are
	// large; reading and parsing them on every check was the main cost).
	private sealed record ConfigValues(string Path, DateTime WriteTime, long Length, string? Global, Dictionary<string, string> PerApp);

	private readonly object gate = new();
	private ConfigValues? localCache;
	private ConfigValues? configCache;

	// How many times a config file was read and parsed (tests).
	public int Parses { get; private set; }

	public static SteamInputCheck CreateDefault() => new(new SystemSteamEnvironment());

	public SteamInputResult Check(string? steamAppId = null)
	{
		string? steamPath = environment.SteamPath;
		bool running = environment.IsSteamRunning;
		if (steamPath is null || !environment.FileExists(Path.Combine(steamPath, "steam.exe")))
		{
			return Result(SteamInputStatus.NotInstalled, running);
		}
		ConfigValues? local;
		ConfigValues? config;
		lock (gate)
		{
			local = localCache = Load(LatestLocalConfig(steamPath), localCache, ReadLocal);
			config = configCache = Load(Path.Combine(steamPath, "config", "config.vdf"), configCache, ReadConfig);
		}

		if (!string.IsNullOrEmpty(steamAppId) && local != null && local.PerApp.TryGetValue(steamAppId, out string? perApp))
		{
			switch (perApp.Trim())
			{
				case "0": return Result(SteamInputStatus.Off, running);
				case "2": return Result(SteamInputStatus.OnForApp, running);
			}
		}

		string? global = local?.Global ?? config?.Global;
		return global?.Trim() switch
		{
			"0" => Result(SteamInputStatus.Off, running),
			"1" or "2" => Result(SteamInputStatus.OnGlobally, running),
			_ => Result(SteamInputStatus.Unknown, running)
		};
	}

	private static SteamInputResult Result(SteamInputStatus status, bool running) =>
		new(status, running, Localization.Text.SteamInputAdvice(status));

	private string? LatestLocalConfig(string steamPath)
	{
		string? best = null;
		DateTime bestTime = DateTime.MinValue;
		foreach (string user in environment.GetDirectories(Path.Combine(steamPath, "userdata")))
		{
			string file = Path.Combine(user, "config", "localconfig.vdf");
			if (!environment.FileExists(file))
			{
				continue;
			}
			DateTime time = environment.GetLastWriteTimeUtc(file);
			if (best is null || time > bestTime)
			{
				best = file;
				bestTime = time;
			}
		}
		return best;
	}

	// The cached values while the file's path, write time and size are unchanged; else the file is read again. Null when
	// the file is missing or unreadable.
	private ConfigValues? Load(string? path, ConfigValues? cached, Func<VdfNode, (string? Global, Dictionary<string, string> PerApp)> read)
	{
		if (path is null || !environment.FileExists(path))
		{
			return null;
		}
		DateTime writeTime;
		long length;
		try
		{
			writeTime = environment.GetLastWriteTimeUtc(path);
			length = environment.GetFileLength(path);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return null;
		}
		if (cached != null && string.Equals(cached.Path, path, StringComparison.OrdinalIgnoreCase) && cached.WriteTime == writeTime &&
			cached.Length == length)
		{
			return cached;
		}
		Parses++;
		Diagnostics.BackgroundWork.SteamConfigParse();
		try
		{
			(string? global, Dictionary<string, string> perApp) = read(Vdf.Parse(environment.ReadAllText(path)));
			return new ConfigValues(path, writeTime, length, global, perApp);
		}
		catch (VdfFormatException)
		{
			// Kept as "no values" until the file changes.
			return new ConfigValues(path, writeTime, length, null, []);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// Read again next time (Steam may be writing it).
			return null;
		}
	}

	private static (string?, Dictionary<string, string>) ReadLocal(VdfNode root)
	{
		Dictionary<string, string> perApp = new(StringComparer.OrdinalIgnoreCase);
		if (root.Get("UserLocalConfigStore", "Apps") is { } apps)
		{
			foreach ((string id, VdfNode app) in apps.Children)
			{
				if (app.GetValue(PerAppKey) is string value)
				{
					perApp[id] = value;
				}
			}
		}
		return (root.GetValue("UserLocalConfigStore", GlobalPsKey), perApp);
	}

	private static (string?, Dictionary<string, string>) ReadConfig(VdfNode root) =>
		(root.GetValue("InstallConfigStore", "Software", "Valve", "Steam", GlobalPsKey), []);
}
