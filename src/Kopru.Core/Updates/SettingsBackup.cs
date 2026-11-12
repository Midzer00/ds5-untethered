// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Kopru.Core.Diagnostics;

namespace Kopru.Core.Updates;

// On the first start of a version other than the one that ran last (settings.json "LastRunVersion"), settings.json is
// copied to "settings.json.<previous version>.bak" before the new version saves anything; the newest Keep backups stay.
// A file from before LastRunVersion existed is saved as "settings.json.older.bak".
public static class SettingsBackup
{
	public const int Keep = 3;
	public const string UnknownVersion = "older";

	// LastRunVersion from the settings file's text; null when it is missing or the text is not readable JSON.
	public static string? ReadLastRunVersion(string json)
	{
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("LastRunVersion", out JsonElement v)
				&& v.ValueKind == JsonValueKind.String ? v.GetString() : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	// Returns the backup's file name, or null when none was needed (no settings yet, same version) or it failed.
	public static string? Run(IUpdateFiles files, string settingsPath, AppVersion current)
	{
		try
		{
			if (!files.Exists(settingsPath))
			{
				return null;
			}
			string json;
			using (var reader = new StreamReader(files.OpenRead(settingsPath)))
			{
				json = reader.ReadToEnd();
			}
			string? last = ReadLastRunVersion(json);
			if (AppVersion.Parse(last) is { } previous && previous.Equals(current))
			{
				return null;
			}
			string label = AppVersion.Parse(last)?.ToString() ?? UnknownVersion;
			string name = Path.GetFileName(settingsPath) + "." + label + ".bak";
			string folder = Path.GetDirectoryName(settingsPath) ?? string.Empty;
			files.Copy(settingsPath, Path.Combine(folder, name));
			AppLog.Write($"settings backup: {name} written");
			foreach (string old in files.List(folder, Path.GetFileName(settingsPath) + ".*.bak")
				.OrderByDescending(files.LastWriteUtc).Skip(Keep).ToList())
			{
				files.Delete(old);
			}
			return name;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			AppLog.Write($"settings backup: failed ({e.GetType().Name})");
			return null;
		}
	}
}
