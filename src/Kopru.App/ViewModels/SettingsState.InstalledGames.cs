// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Services;
using Kopru.Core.Games;

namespace Kopru.App.ViewModels;

// The supported games list's "Installed" view: the remembered choice, the extra folders searched for games and the
// supported games seen running before (InstalledGameFinder). The lists are replaced, never changed in place, so a scan
// running on another thread reads a stable copy.
public sealed partial class SettingsState
{
	// Games seen running are kept up to this many (the oldest go first).
	public const int SeenGamesLimit = 200;

	[ObservableProperty]
	private bool _installedGamesOnly;

	[ObservableProperty]
	private IReadOnlyList<string> _gameFolders = [];

	[ObservableProperty]
	private IReadOnlyList<SeenGame> _seenGames = [];

	// Found games hidden from the list (and no longer recognised).
	[ObservableProperty]
	private IReadOnlyList<HiddenGame> _hiddenGames = [];

	// The file "Oyna" starts for a game, chosen by the user (game key -> path); it wins over the way the app finds itself.
	[ObservableProperty]
	private IReadOnlyDictionary<string, string> _startFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	public string? StartFileFor(string key) => StartFiles.TryGetValue(key, out string? path) ? path : null;

	// Sets (or with null clears) a game's start file; false when nothing changed.
	public bool SetStartFile(string key, string? path)
	{
		if (string.IsNullOrWhiteSpace(key) || string.Equals(StartFileFor(key), path, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		var next = new Dictionary<string, string>(StartFiles, StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(path))
		{
			next.Remove(key);
		}
		else
		{
			next[key] = path;
		}
		StartFiles = next;
		return true;
	}

	public bool IsGameHidden(string key) => HiddenGames.Any(h => string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase));

	// False when it is blank or hidden already.
	public bool HideGame(string key, string name)
	{
		if (string.IsNullOrWhiteSpace(key) || IsGameHidden(key))
		{
			return false;
		}
		HiddenGames = [.. HiddenGames, new HiddenGame(key, string.IsNullOrWhiteSpace(name) ? key : name)];
		return true;
	}

	public bool ShowGame(string key)
	{
		List<HiddenGame> left = HiddenGames.Where(h => !string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
		if (left.Count == HiddenGames.Count)
		{
			return false;
		}
		HiddenGames = left;
		return true;
	}

	// False when the folder is blank or already there.
	public bool AddGameFolder(string folder)
	{
		// "D:\Games\" and "D:\Games" are one folder; a drive root keeps its separator ("D:\").
		string trimmed = folder.Trim();
		if (trimmed.Length > 3)
		{
			trimmed = trimmed.TrimEnd('\\', '/');
		}
		if (trimmed.Length == 0 || GameFolders.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
		{
			return false;
		}
		GameFolders = [.. GameFolders, trimmed];
		return true;
	}

	public bool RemoveGameFolder(string folder)
	{
		List<string> left = GameFolders.Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)).ToList();
		if (left.Count == GameFolders.Count)
		{
			return false;
		}
		GameFolders = left;
		return true;
	}

	// Remembers a supported game seen running from folder; false when it was already known there.
	public bool RememberSeenGame(string name, string folder)
	{
		if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(folder)
			|| SeenGames.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Folder, folder, StringComparison.OrdinalIgnoreCase)))
		{
			return false;
		}
		List<SeenGame> next = SeenGames.Where(s => !string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
		next.Add(new SeenGame(name, folder));
		SeenGames = next.Count > SeenGamesLimit ? next.Skip(next.Count - SeenGamesLimit).ToList() : next;
		return true;
	}

	private void LoadInstalledGames(AppSettings saved)
	{
		InstalledGamesOnly = saved.InstalledGamesOnly;
		GameFolders = (saved.GameFolders ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		SeenGames = (saved.SeenGames ?? []).Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Folder)).ToList();
		HiddenGames = (saved.HiddenGames ?? []).Where(h => !string.IsNullOrWhiteSpace(h.Key))
			.DistinctBy(h => h.Key, StringComparer.OrdinalIgnoreCase).Select(h => h with { Name = h.Name ?? h.Key }).ToList();
		var startFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach ((string key, string path) in saved.StartFiles ?? [])
		{
			if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(path))
			{
				startFiles[key] = path;
			}
		}
		StartFiles = startFiles;
	}

	private AppSettings WithInstalledGames(AppSettings settings)
	{
		settings.InstalledGamesOnly = InstalledGamesOnly;
		settings.GameFolders = GameFolders.Count > 0 ? GameFolders.ToList() : null;
		settings.SeenGames = SeenGames.Count > 0 ? SeenGames.ToList() : null;
		settings.HiddenGames = HiddenGames.Count > 0 ? HiddenGames.ToList() : null;
		settings.StartFiles = StartFiles.Count > 0 ? new Dictionary<string, string>(StartFiles, StringComparer.OrdinalIgnoreCase) : null;
		return settings;
	}
}
