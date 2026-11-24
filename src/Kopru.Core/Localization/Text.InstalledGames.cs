// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;

namespace Kopru.Core.Localization;

// User-facing texts of the "Installed" list on the supported games page.
public static partial class Text
{
	public static string InstalledFilterAll(Language? l = null) => T(l, "Installed.FilterAll");

	public static string InstalledFilterCount(int count, Language? l = null) => T(l, "Installed.FilterCount", A("count", count));

	public static string InstalledFilterLabel(Language? l = null) => T(l, "Installed.FilterLabel");

	// The plain mark on an installed game's row (and the segment before the first scan has finished).
	public static string InstalledMark(Language? l = null) => T(l, "Installed.Mark");

	// The mark's tooltip: where the game was found (the folder follows).
	public static string InstalledFrom(InstalledSource source, Language? l = null) => source switch
	{
		InstalledSource.Steam => T(l, "Installed.From.Steam"),
		InstalledSource.Epic => T(l, "Installed.From.Epic"),
		InstalledSource.Registry => T(l, "Installed.From.Registry"),
		InstalledSource.Folder => T(l, "Installed.From.Folder"),
		_ => T(l, "Installed.From.OpenedBefore")
	};

	public static string InstalledEmpty(Language? l = null) => T(l, "Installed.Empty");

	// The "Installed" view's one line under the list: a game that was not found is added by hand.
	public static string InstalledAddHint(Language? l = null) => T(l, "Installed.AddHint");

	public static string HideGame(Language? l = null) => T(l, "Hide.Game");

	public static string HideGameTip(Language? l = null) => T(l, "Hide.GameTip");

	public static string HiddenGamesHeader(Language? l = null) => T(l, "Hidden.GamesHeader");

	public static string ShowGameAgain(Language? l = null) => T(l, "Show.GameAgain");

	public static string InstalledSearching(Language? l = null) => T(l, "Installed.Searching");

	public static string ScanGamesAgain(Language? l = null) => T(l, "Scan.GamesAgain");

	public static string ScanningGames(Language? l = null) => T(l, "Scanning.Games");

	public static string GameFolders(Language? l = null) => T(l, "Game.Folders");

	public static string AddGameFolder(Language? l = null) => T(l, "Add.GameFolder");

	public static string ChooseGameFolder(Language? l = null) => T(l, "Choose.GameFolder");

	public static string RemoveGameFolder(Language? l = null) => T(l, "Remove.GameFolder");

	public static string NoGameFolders(Language? l = null) => T(l, "No.GameFolders");

	public static string GameFoldersHint(Language? l = null) => T(l, "Game.FoldersHint");
}
