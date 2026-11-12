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

	// Light effects (missing in older files: static colour, the player LED pattern from PlayerLeds). Breathing levels in
	// percent of the brightness, the period in seconds, rainbow speed 1-10.
	public Kopru.Core.Lighting.LightMode LightMode { get; set; }
	public double BreathingPeriod { get; set; } = 4;
	public double BreathingMin { get; set; } = 10;
	public double BreathingMax { get; set; } = 100;
	public double RainbowSpeed { get; set; } = 3;
	public Kopru.Core.Lighting.PlayerLedPattern? PlayerLedPattern { get; set; }
	public Kopru.Core.Lighting.LedBrightness PlayerLedBrightness { get; set; } = Kopru.Core.Lighting.LedBrightness.High;
	public double IdlePowerOffMinutes { get; set; } = 15;
	public bool StartWithWindows { get; set; }

	// Experimental controller speaker (missing in older files: off); volumes in percent.
	public bool SpeakerEnabled { get; set; }
	public double SpeakerVolume { get; set; } = 80;
	public double HeadphoneVolume { get; set; } = 60;

	// Controller microphone (missing in older files: off); gain in percent.
	public bool MicEnabled { get; set; }
	public double MicGain { get; set; } = 100;
	public bool MicNoiseGate { get; set; } = true;

	// What the controller's mic button does (missing in older files: turn the controller microphone off, as before). The old
	// MicMuteWindows option is gone: the button now mutes a selected PC microphone itself.
	public Kopru.Core.Audio.MicButtonMode MicButtonMode { get; set; }

	// Turn the controller off after IdlePowerOffMinutes (missing in older files: off).
	public bool PowerOffWhenIdle { get; set; }

	// Low Power mode (only the switch; it never changes the profile) and switching to it below 20 % battery.
	public bool LowPowerOn { get; set; }
	public bool AutoLowPower { get; set; }

	// Stick and trigger tuning (missing in older files: no change to the input).
	public Kopru.Core.Input.InputTuning? Tuning { get; set; }

	// Button remapping and gyro aiming (missing in older files: none).
	public Kopru.Core.Input.RemapSettings? Remap { get; set; }
	public Kopru.Core.Input.GyroSettings? Gyro { get; set; }

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
	public bool NotifyLinkQuality { get; set; } = true;
	public bool QuietWhilePlaying { get; set; }
	public bool KeepInTray { get; set; } = true;

	// Start the bridge by itself when the app starts and the controller connects (missing in older files: on).
	public bool AutoStartBridge { get; set; } = true;

	// Keep Windows from making the virtual controller the default speaker or microphone (missing in older files: on), and
	// the flows where the user made the controller the default on purpose (Core DefaultAudioGuard keeps those).
	public bool ProtectDefaultAudio { get; set; } = true;
	public bool ControllerSpeakerChosen { get; set; }
	public bool ControllerMicChosen { get; set; }

	// The required setup steps were all done once (or the virtual controller ran): the setup page no longer opens by
	// itself. Null = a file from before this setting (decided at the next start, see MainWindowViewModel).
	public bool? SetupCompleted { get; set; }

	// The "still running in the tray" hint was shown once.
	public bool TrayHintShown { get; set; }
	// Supported games list: the weekly automatic check and when it last ran.
	public bool AutoUpdateGameList { get; set; } = true;
	public DateTimeOffset? GameListCheckedAt { get; set; }
	// In-app updates (missing in older files: automatic check on, never checked, no toast shown, unknown last version).
	public bool AutoCheckUpdates { get; set; } = true;
	public DateTimeOffset? UpdateCheckedAt { get; set; }
	public string? UpdateToastedVersion { get; set; }
	public string? LastRunVersion { get; set; }
	// Supported games list: download game pictures from Steam (else only pictures already on this PC).
	public bool DownloadGameImages { get; set; } = true;
	// Supported games list: "Installed" chosen instead of "All", the extra folders searched for games and the supported
	// games seen running before with their folders (missing in older files: All, none, none).
	public bool InstalledGamesOnly { get; set; }
	public List<string>? GameFolders { get; set; }
	public List<SeenGame>? SeenGames { get; set; }

	// DualSense supported mode: the switch when the user turned it on without a game, whether to ask when a known game
	// starts, the games that turn it on without asking ("always", by game key), the games never asked about and the games
	// the user added. The mode is on by default (missing in older files: on; a saved "off" is kept).
	public bool SupportedModeOn { get; set; } = true;
	public bool AskOnKnownGame { get; set; } = true;
	public List<string> AlwaysGames { get; set; } = [];
	public List<string> NeverAskGames { get; set; } = [];
	public List<UserGame> UserGames { get; set; } = [];

	// The controller type per game key, only games not on DualSense (missing in older files: DualSense everywhere).
	public Dictionary<string, Kopru.Core.Virtual.VirtualPadType>? GamePadTypes { get; set; }

	// The controller type forced for every game ("Kumanda Türü"; missing in older files: null = Automatic).
	public Kopru.Core.Virtual.VirtualPadType? ForcedPadType { get; set; }

	// Profiles (missing in older files: the settings above become the default profile), the active one, the profile per
	// game key, and the controller shortcuts switch. The settings above always hold the active profile's values.
	public List<Kopru.Core.Profiles.ControllerProfile>? Profiles { get; set; }
	public string? ActiveProfile { get; set; }
	public Dictionary<string, string>? GameProfiles { get; set; }
	public bool ShortcutsEnabled { get; set; } = true;
	// "PS: Open The App" (missing in older files: on).
	public bool PsOpensApp { get; set; } = true;
	// "Also In Games" (missing in older files: on).
	public bool PsOpensAppInGames { get; set; } = true;
	// Controller id (device instance id) to the profile id it runs instead of the global settings.
	public Dictionary<string, string>? ControllerProfiles { get; set; }
	// The controllers the app has seen, by the same id, with the user's names (missing in older files: none yet).
	public List<KnownController>? KnownControllers { get; set; }

	// Desktop mode tuning (missing in older files: the defaults). Whether it is on is not saved.
	public Kopru.Core.Input.DesktopModeSettings? DesktopMode { get; set; }

	// Older versions: the per-exe mode choice and games learned as native. Read once to migrate, never written again.
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public Dictionary<string, GameOverride>? GameOverrides { get; set; }

	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public Dictionary<string, bool>? LearnedGames { get; set; }

	// The game memory, with old per-exe choices migrated: Supported -> always (a game not in the list is added as the
	// user's game), Unsupported -> never ask (games in the list only). Learned results are dropped.
	public GameMemory ToGameMemory(GameCatalog catalog)
	{
		var always = new List<string>(AlwaysGames ?? []);
		var never = new List<string>(NeverAskGames ?? []);
		var users = new List<UserGame>(UserGames ?? []);
		foreach ((string exe, GameOverride choice) in GameOverrides ?? [])
		{
			string file = GameCatalog.Normalize(exe);
			if (file.Length == 0)
			{
				continue;
			}
			CatalogGame? match = catalog.Find(file);
			string key = ForegroundGame.KeyOf(match, file);
			if (choice == GameOverride.Supported)
			{
				if (match == null && !users.Any(u => string.Equals(u.Exe, file, StringComparison.OrdinalIgnoreCase)))
				{
					users.Add(new UserGame(ForegroundGame.DisplayName(file), file));
				}
				AddOnce(always, key);
			}
			else if (choice == GameOverride.Unsupported && match != null)
			{
				AddOnce(never, key);
			}
		}
		return new GameMemory(SupportedModeOn, AskOnKnownGame, always, never, users, GamePadTypes, ForcedPadType);
	}

	// Takes the game memory to save (the old per-exe fields are left out from now on).
	public AppSettings WithGames(GameMemory memory)
	{
		SupportedModeOn = memory.SupportedModeOn;
		AskOnKnownGame = memory.AskOnKnownGame;
		AlwaysGames = memory.Always.ToList();
		NeverAskGames = memory.NeverAsk.ToList();
		UserGames = memory.UserGames.ToList();
		GamePadTypes = memory.PadTypes is { Count: > 0 } pads ? new(pads, StringComparer.OrdinalIgnoreCase) : null;
		ForcedPadType = memory.ForcedPad;
		GameOverrides = null;
		LearnedGames = null;
		return this;
	}

	private static void AddOnce(List<string> list, string key)
	{
		if (!list.Contains(key, StringComparer.OrdinalIgnoreCase))
		{
			list.Add(key);
		}
	}
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
