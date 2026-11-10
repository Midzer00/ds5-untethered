// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.App.ViewModels;

// The Settings groups another page can jump to (the name of the group's panel in the view).
public static class SettingsSections
{
	public const string Shortcuts = "ShortcutsGroup";
	public const string Controllers = "ControllersGroup";
}

// Opening Settings at one group: the view scrolls it to the top once it is laid out.
public sealed partial class SettingsViewModel
{
	// The group the view should show next (cleared by the view once shown).
	private string? _pendingSection;

	public bool HasPendingSection => _pendingSection != null;

	public bool TryGetPendingSection(out string section)
	{
		section = _pendingSection ?? string.Empty;
		return _pendingSection != null;
	}

	public void ClearPendingSection() => _pendingSection = null;

	public event Action<string>? SectionRequested;

	public void ShowSection(string section)
	{
		_pendingSection = section;
		SectionRequested?.Invoke(section);
	}
}
