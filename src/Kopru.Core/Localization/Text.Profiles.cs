// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of profiles and the controller shortcuts.
public static partial class Text
{
	public static string ProfileDefault(Language? l = null) => Pick(l, $"Varsayılan", $"Default");

	public static string ProfilesSection(Language? l = null) => Pick(l, $"Profiller", $"Profiles");

	public static string ProfilesHint(Language? l = null) => Pick(l,
		$"Profil; desteklenen oyunlar, diğer oyunlar, analog/tetik, tuş atama, gyro ve ışık (efektler dahil) ayarlarını birlikte saklar. Yaptığın değişiklikler etkin profile kaydedilir. Desteklenen Oyunlar sayfasında her oyuna bir profil seçebilirsin; seçilmeyen oyunlarda Varsayılan kullanılır.",
		$"A profile keeps the supported games, other games, sticks/triggers, remap, gyro and light (effects included) settings together. Your changes are saved to the active profile. On the Supported Games page you can pick a profile for each game; games without one use Default.");

	public static string ProfileLabel(Language? l = null) => Pick(l, $"Profil", $"Profile");

	public static string ProfileNew(Language? l = null) => Pick(l, $"Yeni Profil", $"New Profile");

	public static string ProfileImport(Language? l = null) => Pick(l, $"İçe Aktar", $"Import");

	public static string ProfileExport(Language? l = null) => Pick(l, $"Dışa Aktar", $"Export");

	public static string ProfileRename(Language? l = null) => Pick(l, $"Yeniden Adlandır", $"Rename");

	public static string ProfileDuplicate(Language? l = null) => Pick(l, $"Çoğalt", $"Duplicate");

	public static string ProfileDelete(Language? l = null) => Pick(l, $"Sil", $"Delete");

	public static string ProfileActivate(Language? l = null) => Pick(l, $"Etkinleştir", $"Activate");

	public static string ProfileActive(Language? l = null) => Pick(l, $"Etkin", $"Active");

	public static string ProfileSave(Language? l = null) => Pick(l, $"Kaydet", $"Save");

	public static string ProfileCancel(Language? l = null) => Pick(l, $"Vazgeç", $"Cancel");

	public static string ProfileMore(Language? l = null) => Pick(l, $"Diğer İşlemler", $"More Actions");

	public static string ProfileNewName(int number, Language? l = null) => Pick(l, $"Profil {number}", $"Profile {number}");

	public static string ProfileCopyName(string name, Language? l = null) => Pick(l, $"{name} Kopyası", $"{name} Copy");

	public static string ProfileFileType(Language? l = null) => Pick(l, $"Untethered profili", $"Untethered profile");

	public static string ProfileImportFailed(Language? l = null) => Pick(l,
		$"Bu dosya bir profil dosyası değil.",
		$"This file is not a profile file.");

	public static string ProfileImported(string name, Language? l = null) => Pick(l,
		$"\"{name}\" profili eklendi.",
		$"Profile \"{name}\" was added.");

	public static string ProfileExported(string name, Language? l = null) => Pick(l,
		$"\"{name}\" profili kaydedildi.",
		$"Profile \"{name}\" was saved.");

	public static string ProfileGames(int count, Language? l = null) => count == 1
		? Pick(l, $"1 oyunda kullanılıyor", $"Used by 1 game")
		: Pick(l, $"{count} oyunda kullanılıyor", $"Used by {count} games");

	public static string ProfileNoGames(Language? l = null) => Pick(l, $"Oyuna atanmadı", $"Not assigned to a game");

	public static string ProfileDefaultNote(Language? l = null) => Pick(l,
		$"Profil seçilmemiş oyunlarda ve oyun yokken kullanılır",
		$"Used for games without a profile and when no game runs");

	public static string GameProfileTip(Language? l = null) => Pick(l,
		$"Bu oyun tanınınca etkinleşecek profil",
		$"The profile that becomes active when this game is recognised");

	// Toasts after a shortcut.
	public static string ShortcutProfileTitle(Language? l = null) => Pick(l, $"Profil Değişti", $"Profile Changed");

	public static string ShortcutProfileBody(int index, string name, Language? l = null) => Pick(l,
		$"{index}. profil: {name}",
		$"Profile {index}: {name}");

	public static string ShortcutHapticsTitle(Language? l = null) => Pick(l, $"Oyun Haptiği", $"Game Haptics");

	public static string ShortcutHapticsBody(double percent, Language? l = null) => Pick(l,
		$"Seviye %{percent:F0}",
		$"Level {percent:F0}%");

	public static string ShortcutModeTitle(Language? l = null) => Pick(l, $"DualSense Modu", $"DualSense Mode");

	public static string ShortcutModeBody(bool on, Language? l = null) => on
		? Pick(l, $"Açıldı", $"Turned on")
		: Pick(l, $"Kapatıldı", $"Turned off");

	// Settings: the switch and the legend.
	public static string ShortcutsSection(Language? l = null) => Pick(l, $"Kumanda Kısayolları", $"Controller Shortcuts");

	public static string ShortcutsHint(Language? l = null) => Pick(l,
		$"PS tuşunu basılı tutup aşağıdaki tuşlara bas. Kısayol sırasında PS ve kullanılan tuş oyuna gitmez; oyun açıkken PS'ye tek başına kısa basmak oyuna her zamanki gibi ulaşır.",
		$"Hold the PS button and press one of the buttons below. During a shortcut the game doesn't see PS or the button used; in a game, a plain short PS press still reaches the game.");

	public static string ShortcutKeysProfile(Language? l = null) => Pick(l, $"PS + Yön Sol / Sağ", $"PS + D-pad Left / Right");

	public static string ShortcutKeysHaptics(Language? l = null) => Pick(l, $"PS + Yön Yukarı / Aşağı", $"PS + D-pad Up / Down");

	public static string ShortcutKeysMode(Language? l = null) => Pick(l, $"PS + Kare", $"PS + Square");

	public static string ShortcutKeysPower(Language? l = null) => Pick(l, $"PS + Üçgen (1 sn basılı)", $"PS + Triangle (hold 1 s)");

	public static string ShortcutDoesProfile(Language? l = null) => Pick(l,
		$"Önceki / sonraki profil (oyuncu ışıkları profil numarasını gösterir)",
		$"Previous / next profile (the player LEDs show its number)");

	public static string ShortcutDoesHaptics(Language? l = null) => Pick(l,
		$"Oyun haptiğini %10 artır / azalt",
		$"Game haptics 10% up / down");

	public static string ShortcutDoesMode(Language? l = null) => Pick(l,
		$"DualSense modunu aç / kapat",
		$"Turn DualSense mode on / off");

	public static string ShortcutDoesPower(Language? l = null) => Pick(l,
		$"Kumandayı kapat",
		$"Turn the controller off");
}
