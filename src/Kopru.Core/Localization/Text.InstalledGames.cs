// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;

namespace Kopru.Core.Localization;

// User-facing texts of the "Installed" list on the supported games page.
public static partial class Text
{
	public static string InstalledFilterAll(Language? l = null) => Pick(l, $"Tümü", $"All");

	public static string InstalledFilterCount(int count, Language? l = null) => Pick(l, $"Yüklü ({count})", $"Installed ({count})");

	public static string InstalledFilterLabel(Language? l = null) => Pick(l, $"Gösterilen Oyunlar", $"Games Shown");

	// The plain mark on an installed game's row (and the segment before the first scan has finished).
	public static string InstalledMark(Language? l = null) => Pick(l, $"Yüklü", $"Installed");

	// The mark's tooltip: where the game was found (the folder follows).
	public static string InstalledFrom(InstalledSource source, Language? l = null) => source switch
	{
		InstalledSource.Steam => Pick(l, $"Steam ile yüklü", $"Installed with Steam"),
		InstalledSource.Epic => Pick(l, $"Epic Games ile yüklü", $"Installed with Epic Games"),
		InstalledSource.Registry => Pick(l, $"Yüklü programlarda bulundu", $"Found in installed programs"),
		InstalledSource.Folder => Pick(l, $"Oyun klasöründe bulundu", $"Found in a game folder"),
		_ => Pick(l, $"Daha önce bu klasörden açıldı", $"Opened from this folder before")
	};

	public static string InstalledEmpty(Language? l = null) => Pick(l, $"Bu bilgisayarda desteklenen oyun bulunamadı.", $"No supported games were found on this PC.");

	public static string InstalledSearching(Language? l = null) => Pick(l, $"Oyunlar aranıyor…", $"Looking for games…");

	public static string ScanGamesAgain(Language? l = null) => Pick(l, $"Yeniden Tara", $"Scan Again");

	public static string ScanningGames(Language? l = null) => Pick(l, $"Taranıyor…", $"Scanning…");

	public static string GameFolders(Language? l = null) => Pick(l, $"Klasörler", $"Folders");

	public static string AddGameFolder(Language? l = null) => Pick(l, $"Klasör Ekle", $"Add Folder");

	public static string ChooseGameFolder(Language? l = null) => Pick(l, $"Oyun Klasörü Seç", $"Choose Game Folder");

	public static string RemoveGameFolder(Language? l = null) => Pick(l, $"Klasörü Kaldır", $"Remove Folder");

	public static string NoGameFolders(Language? l = null) => Pick(l, $"Eklenen klasör yok.", $"No folders added.");

	public static string GameFoldersHint(Language? l = null) => Pick(l,
		$"Steam, Epic Games, GOG ve disklerdeki Games ya da Oyunlar klasörleri kendiliğinden taranır. Oyunlarınız başka bir yerdeyse klasörünü ekleyin.",
		$"Steam, Epic Games, GOG and the Games folders on your drives are searched by themselves. If your games are somewhere else, add their folder.");
}
