// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core;
using Microsoft.Win32;

namespace Kopru.App.Services;

// Everything the user can change, as saved to settings.json. Language: "tr", "en" or null (follow Windows).
public sealed class AppSettings
{
	public string? Language { get; set; }
	public double GameHapticsLevel { get; set; } = 150;
	public double RumbleLevel { get; set; } = 100;
	public double TriggerStrength { get; set; } = 100;
	public uint LightbarColour { get; set; } = 0x2F6BFF;
	public double Brightness { get; set; } = 80;
	public bool PlayerLeds { get; set; } = true;
	public double IdlePowerOffMinutes { get; set; } = 15;
	public bool StartWithWindows { get; set; }

	// Turn the controller off after IdlePowerOffMinutes (missing in older files: off).
	public bool PowerOffWhenIdle { get; set; }

	// Stick and trigger tuning (missing in older files: no change to the input).
	public Kopru.Core.Input.InputTuning? Tuning { get; set; }

	// Supported games profile (with GameHapticsLevel and TriggerStrength, the game's trigger effects, 0-100 %).
	public bool LightFromGame { get; set; } = true;

	// Other games profile (with RumbleLevel).
	public RumbleFeel RumbleFeel { get; set; } = RumbleFeel.Soft;
	public FallbackTrigger FallbackTrigger { get; set; } = FallbackTrigger.Off;
	public double FallbackStrength { get; set; } = 50;

	// Notifications (missing in older files: all on, quiet while playing off) and the tray.
	public bool NotificationsEnabled { get; set; } = true;
	public bool NotifyConnection { get; set; } = true;
	public bool NotifyBattery { get; set; } = true;
	public bool NotifyGame { get; set; } = true;
	public bool NotifySteamInput { get; set; } = true;
	public bool NotifyPowerOff { get; set; } = true;
	public bool NotifyErrors { get; set; } = true;
	public bool NotifyHidHide { get; set; } = true;
	public bool QuietWhilePlaying { get; set; }
	public bool KeepInTray { get; set; } = true;

	// The "still running in the tray" hint was shown once.
	public bool TrayHintShown { get; set; }

	// Per exe: the user's mode choice (Auto is not stored) and whether the game was detected as native here.
	public Dictionary<string, GameOverride> GameOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	public Dictionary<string, bool> LearnedGames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface ISettingsStore
{
	// Null when nothing was saved yet (first run).
	AppSettings? Load();

	void Save(AppSettings settings);
}

// %LOCALAPPDATA%\Untethered\settings.json
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
	private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

	public string Path { get; } = path;

	public static JsonSettingsStore CreateDefault() => new(AppPaths.Settings);

	public AppSettings? Load()
	{
		try
		{
			return File.Exists(Path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new AppSettings() : null;
		}
		catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
		{
			// Unreadable: start from the defaults (but not as a first run).
			return new AppSettings();
		}
	}

	public void Save(AppSettings settings)
	{
		string? dir = System.IO.Path.GetDirectoryName(Path);
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		string temp = Path + ".tmp";
		File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
		File.Move(temp, Path, overwrite: true);
	}
}

// Keeps settings in memory (tests; or when no store is given).
public sealed class MemorySettingsStore(AppSettings? initial = null) : ISettingsStore
{
	public AppSettings? Saved { get; private set; } = initial;

	public int Saves { get; private set; }

	public AppSettings? Load() => Saved;

	public void Save(AppSettings settings)
	{
		Saved = settings;
		Saves++;
	}
}

// "Start with Windows".
public interface IStartupRegistration
{
	void Set(bool enabled);
}

// HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value "Untethered" = the current exe with --minimized (starts in
// the tray).
public sealed class RunKeyStartupRegistration : IStartupRegistration
{
	public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	public const string ValueName = "Untethered";
	public const string MinimizedArgument = "--minimized";

	public static string CommandLine(string exe) => "\"" + exe + "\" " + MinimizedArgument;

	public void Set(bool enabled)
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}
		using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
		if (enabled && Environment.ProcessPath is { } exe)
		{
			key.SetValue(ValueName, CommandLine(exe));
		}
		else
		{
			key.DeleteValue(ValueName, throwOnMissingValue: false);
		}
	}
}
