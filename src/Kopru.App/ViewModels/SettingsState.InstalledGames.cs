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
	}

	private AppSettings WithInstalledGames(AppSettings settings)
	{
		settings.InstalledGamesOnly = InstalledGamesOnly;
		settings.GameFolders = GameFolders.Count > 0 ? GameFolders.ToList() : null;
		settings.SeenGames = SeenGames.Count > 0 ? SeenGames.ToList() : null;
		return settings;
	}
}
