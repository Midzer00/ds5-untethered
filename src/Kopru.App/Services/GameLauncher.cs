// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Kopru.Core.Diagnostics;
using Kopru.Core.Games;

namespace Kopru.App.Services;

public enum LaunchVia
{
	Steam,
	Epic,
	Exe,

	// A file the user chose (an exe or a shortcut), opened by the shell.
	File
}

// How a game is started: a launcher URL (Steam, Epic) or its exe (WorkingDirectory: the exe's folder).
public sealed record GameLaunch(LaunchVia Via, string Target, string? WorkingDirectory = null)
{
	// For the log: "steam", "epic" or "exe".
	public string ViaName => Via switch { LaunchVia.Steam => "steam", LaunchVia.Epic => "epic", LaunchVia.File => "file", _ => "exe" };
}

// Starts a game from the games list ("Oyna"). Only for games we know how to start: a Steam game by its app id, an Epic game
// by its AppName (through the launchers, which handle sign-in and updates), and an added game or an installed one whose exe
// is known and exists by starting that exe in its folder.
public static class GameLauncher
{
	public static GameLaunch Steam(long appId) => new(LaunchVia.Steam, FormattableString.Invariant($"steam://rungameid/{appId}"));

	public static GameLaunch Epic(string appName) =>
		new(LaunchVia.Epic, $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch&silent=true");

	public static GameLaunch Exe(string path) => new(LaunchVia.Exe, path, Path.GetDirectoryName(path));

	public static GameLaunch File(string path) => new(LaunchVia.File, path, Path.GetDirectoryName(path));

	// How to start this row's game, or null when we don't know. found: a game found by its launcher; installed: a catalog
	// game found on this PC; fileExists: File.Exists (tests pass their own).
	public static GameLaunch? For(KnownGame game, FoundGame? found, InstalledGame? installed, Func<string, bool> fileExists) =>
		Explain(game, found, installed, fileExists).Launch;

	// The same, with why there is none (null reason: not on this PC, nothing to say).
	// startFile: the file the user chose for this game; it wins while it exists.
	public static (GameLaunch? Launch, string? Reason) Explain(KnownGame game, FoundGame? found, InstalledGame? installed, Func<string, bool> fileExists,
		string? startFile = null)
	{
		if (startFile is { Length: > 0 } && Path.IsPathRooted(startFile))
		{
			if (fileExists(startFile))
			{
				return (File(startFile), null);
			}
			(GameLaunch? instead, string? why) = Explain(game, found, installed, fileExists);
			return (instead, instead == null ? $"start file missing: {Path.GetFileName(startFile)}" : why);
		}
		if (found != null)
		{
			return found.Source switch
			{
				InstalledSource.Steam => found.SteamAppId is long id ? (Steam(id), null) : (null, "steam: no app id"),
				InstalledSource.Epic => !string.IsNullOrWhiteSpace(found.Id) ? (Epic(found.Id), null) : ExeOrWhy(found.LaunchExe, fileExists, "epic: no AppName"),
				_ => ExeOrWhy(found.LaunchExe, fileExists, "found: no exe")
			};
		}
		if (game.IsUserAdded)
		{
			return ExeOrWhy(game.Path, fileExists, "added: no exe path");
		}
		if (installed == null)
		{
			return (null, null);
		}
		switch (installed.Source)
		{
			// The manifest's app id first (the catalog's may be another edition's or missing), then the catalog's.
			case InstalledSource.Steam:
				if (long.TryParse(installed.LaunchId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long appId))
				{
					return (Steam(appId), null);
				}
				if (installed.Game.SteamAppId is long catalogId)
				{
					return (Steam(catalogId), null);
				}
				break;
			case InstalledSource.Epic:
				if (!string.IsNullOrWhiteSpace(installed.LaunchId))
				{
					return (Epic(installed.LaunchId), null);
				}
				if (installed.LaunchExe is { } epicExe && Path.IsPathRooted(epicExe) && fileExists(epicExe))
				{
					return (Exe(epicExe), null);
				}
				break;
		}
		// Found in a folder, in the uninstall registry or seen running there (or a launcher that did not say): one of the
		// catalog's exe names in that folder, at its top or where games usually keep it.
		if (installed.Folder is { Length: > 0 } folder && Path.IsPathRooted(folder))
		{
			foreach (string exe in installed.Game.Exe)
			{
				foreach (string place in ExePlaces)
				{
					string candidate = Path.Combine(folder, place, exe);
					if (fileExists(candidate))
					{
						return (Exe(candidate), null);
					}
				}
			}
			return (null, $"{SourceName(installed.Source)}: no catalog exe in {folder}");
		}
		return (null, $"{SourceName(installed.Source)}: no folder");
	}

	// Where under a game's folder its exe usually is: the top, the Unreal layout, a bin folder.
	private static readonly string[] ExePlaces = ["", Path.Combine("Binaries", "Win64"), Path.Combine("bin", "x64"), "bin"];

	private static (GameLaunch?, string?) ExeOrWhy(string? path, Func<string, bool> fileExists, string noPath) =>
		path is not { Length: > 0 } || !Path.IsPathRooted(path) ? (null, noPath)
		: fileExists(path) ? (Exe(path), null)
		: (null, $"exe missing: {path}");

	private static string SourceName(InstalledSource source) => source.ToString().ToLowerInvariant();

	// What Process.Start gets: the shell opens the URL scheme or the exe.
	public static ProcessStartInfo StartInfo(GameLaunch launch) => new(launch.Target)
	{
		UseShellExecute = true,
		WorkingDirectory = launch.WorkingDirectory ?? string.Empty
	};

	// Starts it; false (logged) when it could not be started.
	public static bool Start(GameLaunch launch, string name)
	{
		try
		{
			using Process? _ = Process.Start(StartInfo(launch));
			AppLog.Write($"games: launched {name} via {launch.ViaName}");
			return true;
		}
		catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
		{
			AppLog.Write($"games: launching {name} via {launch.ViaName} failed: {e.GetType().Name}: {e.Message}");
			return false;
		}
	}
}
