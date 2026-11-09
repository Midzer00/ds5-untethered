// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core;

// Per-user data folder of the app: %LOCALAPPDATA%\Untethered (settings, hiding state, controller identity).
public static class AppPaths
{
	public const string FolderName = "Untethered";

	public static string DataDirectory { get; } =
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

	public static string Captures => Path.Combine(DataDirectory, "captures");

	public static string Settings => Path.Combine(DataDirectory, "settings.json");
}
