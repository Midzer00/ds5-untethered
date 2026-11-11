// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Steam;

namespace Kopru.Core.Games;

// Finds the header picture (460x215) Steam itself already keeps for an app in <Steam>\appcache\librarycache. Read only.
// The layout depends on the Steam client version:
//  - current: <appid>\header.jpg (or library_header.jpg);
//  - current, newer assets: <appid>\<40-hex hash>\header.jpg (or library_header.jpg), the newest folder first;
//  - older clients (before the 2024 library update): <appid>_header.jpg directly in librarycache.
// Other files there (library_600x900.jpg, library_hero.jpg, logo.png, <hash>.jpg icons) have other shapes and are not used.
public sealed class SteamArtwork(ISteamEnvironment environment)
{
	private static readonly string[] HeaderNames = ["header.jpg", "library_header.jpg"];

	private readonly Lazy<string?> cacheFolder = new(() =>
	{
		try
		{
			string? steam = environment.SteamPath;
			return string.IsNullOrEmpty(steam) ? null : Path.Combine(steam, "appcache", "librarycache");
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return null;
		}
	});

	public static SteamArtwork CreateDefault() => new(new SystemSteamEnvironment());

	// The local header picture of appId, or null when Steam has none on this PC.
	public string? Find(long appId)
	{
		string? root = cacheFolder.Value;
		if (root == null || appId <= 0)
		{
			return null;
		}
		try
		{
			string id = appId.ToString(System.Globalization.CultureInfo.InvariantCulture);
			string folder = Path.Combine(root, id);
			foreach (string name in HeaderNames)
			{
				string file = Path.Combine(folder, name);
				if (environment.FileExists(file))
				{
					return file;
				}
			}
			foreach (string sub in environment.GetDirectories(folder).OrderByDescending(environment.GetLastWriteTimeUtc))
			{
				foreach (string name in HeaderNames)
				{
					string file = Path.Combine(sub, name);
					if (environment.FileExists(file))
					{
						return file;
					}
				}
			}
			string legacy = Path.Combine(root, id + "_header.jpg");
			return environment.FileExists(legacy) ? legacy : null;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return null;
		}
	}
}
