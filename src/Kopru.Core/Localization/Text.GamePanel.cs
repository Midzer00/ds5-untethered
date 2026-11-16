// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of a game's panel on the Games page.
public static partial class Text
{
	// Where a catalog game comes from: the supported list.
	public static string GameListed(Language? l = null) => Pick(l, $"Listede", $"Listed");

	public static string GamePanelClose(Language? l = null) => Pick(l, $"Kapat", $"Close");

	public static string GameUsesDefaultProfile(Language? l = null) => Pick(l,
		$"Bu oyun Varsayılan profili kullanıyor.",
		$"This game uses the Default profile.");

	public static string GameOwnSettings(Language? l = null) => Pick(l, $"Bu Oyuna Özel Ayarlar", $"Settings For This Game");

	public static string GameOwnSettingsHint(Language? l = null) => Pick(l,
		$"Bu oyuna adını taşıyan bir profil verir; değişiklikler sadece bu oyunu etkiler.",
		$"Gives this game a profile named after it; changes affect only this game.");

	public static string GameResetToDefault(Language? l = null) => Pick(l, $"Varsayılana Dön", $"Back To Default");

	public static string GameResetToDefaultHint(Language? l = null) => Pick(l,
		$"Oyun yine Varsayılan profili kullanır; bu oyunun profili silinmez.",
		$"The game uses the Default profile again; its own profile is kept.");

	// The file "Oyna" starts, chosen by the user.
	public static string StartFileChoose(Language? l = null) => Pick(l, $"Başlatma Dosyası Seç…", $"Choose Start File…");

	public static string StartFileChange(Language? l = null) => Pick(l, $"Başlatma Dosyasını Değiştir", $"Change Start File");

	public static string StartFileRemove(Language? l = null) => Pick(l, $"Kaldır", $"Remove");

	// file: the file's name only.
	public static string StartFileLine(string file, Language? l = null) => Pick(l, $"Başlatma Dosyası: {file}", $"Start File: {file}");

	public static string StartFileHint(Language? l = null) => Pick(l,
		$"Oyna bu dosyayı açar: oyunun exe dosyası ya da bir kısayol.",
		$"Play opens this file: the game's exe or a shortcut.");

	public static string StartFileTypes(Language? l = null) => Pick(l, $"Programlar ve Kısayollar", $"Programs And Shortcuts");
}
