// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Kopru.App.Services;

// "Start When I Sign In" on Linux: an XDG autostart entry, $XDG_CONFIG_HOME/autostart/ds5-untethered.desktop (default
// ~/.config/autostart), that starts the current executable with --minimized. Turning it off deletes the file.
public sealed class XdgAutostartRegistration(string? directory = null, Func<string?>? executable = null) : IStartupRegistration
{
	public const string FileName = "ds5-untethered.desktop";

	// The autostart copy starts hidden (in the background, with its tray icon).
	public const string MinimizedArgument = "--minimized";

	public string Directory { get; } = directory ??
		Path.Combine(Kopru.Core.AppPaths.XdgConfigHome(Environment.GetEnvironmentVariable,
			Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "autostart");

	public string FilePath => Path.Combine(Directory, FileName);

	public void Set(bool enabled)
	{
		string? exe = (executable ?? (() => Environment.ProcessPath))();
		if (!enabled || exe == null)
		{
			File.Delete(FilePath);
			return;
		}
		System.IO.Directory.CreateDirectory(Directory);
		string temp = FilePath + ".tmp";
		File.WriteAllText(temp, Entry(exe), new UTF8Encoding(false));
		File.Move(temp, FilePath, overwrite: true);
	}

	// The desktop entry (Desktop Entry Specification): Exec quotes the path, which may contain spaces.
	public static string Entry(string exe) =>
		"[Desktop Entry]\n" +
		"Type=Application\n" +
		"Name=" + ViewModels.AppInfo.Name + "\n" +
		"Comment=Bluetooth DualSense as a wired controller\n" +
		"Exec=" + Quote(exe) + " " + MinimizedArgument + "\n" +
		"Icon=ds5-untethered\n" +
		"Terminal=false\n" +
		"X-GNOME-Autostart-enabled=true\n";

	// Exec quoting: the whole path in double quotes, with ", `, $ and \ escaped by a backslash, and % doubled (a lone %
	// starts a field code).
	public static string Quote(string path)
	{
		var b = new StringBuilder("\"");
		foreach (char c in path)
		{
			if (c is '"' or '`' or '$' or '\\')
			{
				b.Append('\\');
			}
			else if (c == '%')
			{
				b.Append('%');
			}
			b.Append(c);
		}
		return b.Append('"').ToString();
	}
}
