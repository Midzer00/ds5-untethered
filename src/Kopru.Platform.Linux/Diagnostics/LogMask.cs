// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Kopru.Core.Diagnostics;

namespace Kopru.Platform.Linux.Diagnostics;

// Makes a log line safe to post: Bluetooth addresses masked (DiagnosticsExport.Scrub, the same as the app's diagnostics
// zip), and on top of that the user names in Linux home paths (/home/<name>/...) and wherever they stand alone in a path.
public static partial class LogMask
{
	// The names masked by default: the current user and, under sudo, the user who ran sudo (SUDO_USER).
	public static IReadOnlyList<string> DefaultUserNames()
	{
		List<string> names = [Environment.UserName];
		if (Environment.GetEnvironmentVariable("SUDO_USER") is { Length: > 0 } sudoUser)
		{
			names.Add(sudoUser);
		}
		return names;
	}

	// userNames: the names to mask; none = DefaultUserNames. "root" and one-letter names are left alone.
	public static string Mask(string text, params string?[] userNames)
	{
		string result = DiagnosticsExport.Scrub(text);
		result = HomePath().Replace(result, "/home/<user>");
		IEnumerable<string?> names = userNames.Length > 0 ? userNames : DefaultUserNames().ToArray<string?>();
		foreach (string? name in names.Distinct(StringComparer.Ordinal))
		{
			if (name is { Length: >= 2 } && name != "root")
			{
				result = Regex.Replace(result, "/" + Regex.Escape(name) + @"(?=/|$|\s)", "/<user>");
			}
		}
		return result;
	}

	[GeneratedRegex(@"/home/[^/\s""']+")]
	private static partial Regex HomePath();
}
