// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of profiles and the controller shortcuts.
public static partial class Text
{
	public static string ProfileDefault(Language? l = null) => T(l, "Profile.Default");

	public static string ProfilesSection(Language? l = null) => T(l, "Profiles.Section");

	public static string ProfilesHint(Language? l = null) => T(l, "Profiles.Hint");

	public static string ProfileLabel(Language? l = null) => T(l, "Profile.Label");

	public static string ProfileNew(Language? l = null) => T(l, "Profile.New");

	public static string ProfileImport(Language? l = null) => T(l, "Profile.Import");

	public static string ProfileExport(Language? l = null) => T(l, "Profile.Export");

	public static string ProfileRename(Language? l = null) => T(l, "Profile.Rename");

	public static string ProfileDuplicate(Language? l = null) => T(l, "Profile.Duplicate");

	public static string ProfileDelete(Language? l = null) => T(l, "Profile.Delete");

	public static string ProfileActivate(Language? l = null) => T(l, "Profile.Activate");

	public static string ProfileActive(Language? l = null) => T(l, "Profile.Active");

	public static string ProfileSave(Language? l = null) => T(l, "Profile.Save");

	public static string ProfileCancel(Language? l = null) => T(l, "Profile.Cancel");

	public static string ProfileMore(Language? l = null) => T(l, "Profile.More");

	public static string ProfileNewName(int number, Language? l = null) => T(l, "Profile.NewName", A("number", number));

	public static string ProfileCopyName(string name, Language? l = null) => T(l, "Profile.CopyName", A("name", name));

	public static string ProfileFileType(Language? l = null) => T(l, "Profile.FileType");

	public static string ProfileImportFailed(Language? l = null) => T(l, "Profile.ImportFailed");

	public static string ProfileImported(string name, Language? l = null) => T(l, "Profile.Imported", A("name", name));

	public static string ProfileExported(string name, Language? l = null) => T(l, "Profile.Exported", A("name", name));

	public static string ProfileGames(int count, Language? l = null) => T(l, "Profile.Games", A("count", count));

	public static string ProfileNoGames(Language? l = null) => T(l, "Profile.NoGames");

	public static string ProfileDefaultNote(Language? l = null) => T(l, "Profile.DefaultNote");

	public static string GameProfileTip(Language? l = null) => T(l, "Game.ProfileTip");

	// Toasts after a shortcut.
	public static string ShortcutProfileTitle(Language? l = null) => T(l, "Shortcut.ProfileTitle");

	public static string ShortcutProfileBody(int index, string name, Language? l = null) => T(l, "Shortcut.ProfileBody", A("index", index), A("name", name));

	public static string ShortcutHapticsTitle(Language? l = null) => T(l, "Shortcut.HapticsTitle");

	public static string ShortcutHapticsBody(double percent, Language? l = null) => T(l, "Shortcut.HapticsBody", A("percent", percent, "F0"));

	// Settings: the switch and the legend.
	public static string ShortcutsSection(Language? l = null) => T(l, "Shortcuts.Section");

	public static string ShortcutsHint(Language? l = null) => T(l, "Shortcuts.Hint");

	public static string ShortcutKeysProfile(Language? l = null) => T(l, "Shortcut.KeysProfile");

	public static string ShortcutKeysHaptics(Language? l = null) => T(l, "Shortcut.KeysHaptics");

	public static string ShortcutKeysPower(Language? l = null) => T(l, "Shortcut.KeysPower");

	public static string ShortcutDoesProfile(Language? l = null) => T(l, "Shortcut.DoesProfile");

	public static string ShortcutDoesHaptics(Language? l = null) => T(l, "Shortcut.DoesHaptics");

	public static string ShortcutDoesPower(Language? l = null) => T(l, "Shortcut.DoesPower");
}
