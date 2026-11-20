// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core;

// Per-user data folder of the app (settings, controller identity, caches): $XDG_CONFIG_HOME/ds5-untethered (default
// ~/.config/ds5-untethered), with the logs under $XDG_STATE_HOME/ds5-untethered (default ~/.local/state/ds5-untethered).
public static class AppPaths
{
	// The folder name in the XDG base directories.
	public const string XdgFolderName = "ds5-untethered";

	public static string DataDirectory { get; } = Path.Combine(XdgConfigHome(Environment.GetEnvironmentVariable, Home()), XdgFolderName);

	public static string LogDirectory { get; } = Path.Combine(XdgStateHome(Environment.GetEnvironmentVariable, Home()), XdgFolderName, "logs");

	public static string Captures => Path.Combine(DataDirectory, "captures");

	public static string Settings => Path.Combine(DataDirectory, "settings.json");

	// The supported games list downloaded by GameCatalogUpdater (used when newer than the embedded one).
	public static string GameList => Path.Combine(DataDirectory, "supported-games.json");

	// $XDG_CONFIG_HOME when it is set to an absolute path (the XDG Base Directory spec ignores relative ones), else ~/.config.
	public static string XdgConfigHome(Func<string, string?> environment, string home) =>
		Absolute(environment("XDG_CONFIG_HOME")) ?? Path.Combine(home, ".config");

	// $XDG_STATE_HOME when absolute, else ~/.local/state.
	public static string XdgStateHome(Func<string, string?> environment, string home) =>
		Absolute(environment("XDG_STATE_HOME")) ?? Path.Combine(home, ".local", "state");

	private static string? Absolute(string? path) => !string.IsNullOrWhiteSpace(path) && path.StartsWith('/') ? path : null;

	private static string Home() =>
		Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
