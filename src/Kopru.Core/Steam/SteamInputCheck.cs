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
}

public sealed class SystemSteamEnvironment : ISteamEnvironment
{
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

	public bool IsSteamRunning
	{
		get
		{
			Process[] processes = Process.GetProcessesByName("steam");
			foreach (Process p in processes)
			{
				p.Dispose();
			}
			return processes.Length > 0;
		}
	}

	public bool FileExists(string path) => File.Exists(path);
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

	public static SteamInputCheck CreateDefault() => new(new SystemSteamEnvironment());

	public SteamInputResult Check(string? steamAppId = null)
	{
		string? steamPath = environment.SteamPath;
		bool running = environment.IsSteamRunning;
		if (steamPath is null || !environment.FileExists(Path.Combine(steamPath, "steam.exe")))
		{
			return Result(SteamInputStatus.NotInstalled, running);
		}
		VdfNode? local = TryLoad(LatestLocalConfig(steamPath));
		VdfNode? config = TryLoad(Path.Combine(steamPath, "config", "config.vdf"));

		if (!string.IsNullOrEmpty(steamAppId) &&
			local?.GetValue("UserLocalConfigStore", "Apps", steamAppId, PerAppKey) is string perApp)
		{
			switch (perApp.Trim())
			{
				case "0": return Result(SteamInputStatus.Off, running);
				case "2": return Result(SteamInputStatus.OnForApp, running);
			}
		}

		string? global = local?.GetValue("UserLocalConfigStore", GlobalPsKey) ??
			config?.GetValue("InstallConfigStore", "Software", "Valve", "Steam", GlobalPsKey);
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

	private VdfNode? TryLoad(string? path)
	{
		if (path is null || !environment.FileExists(path))
		{
			return null;
		}
		try
		{
			return Vdf.Parse(environment.ReadAllText(path));
		}
		catch (Exception e) when (e is VdfFormatException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
