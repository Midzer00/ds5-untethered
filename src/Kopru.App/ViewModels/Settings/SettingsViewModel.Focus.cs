// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// The Settings page's tabs. Every setting is on one tab only; each tab fits the window without scrolling. The selected
// tab is kept while the app runs (not saved); links from other pages, toasts and the Overview open the tab they mean.
public sealed partial class SettingsViewModel
{
	public const int GeneralTab = 0;
	public const int ControllersTab = 1;
	public const int AudioTab = 2;
	public const int ShortcutsTab = 3;
	public const int MouseTab = 4;
	public const int NotificationsTab = 5;

	private int _selectedTab = GeneralTab;

	// Tabs for the segmented switch. The list stays the same on a language switch (only the names change), so the switch
	// never loses its selection.
	public IReadOnlyList<TabOption> Tabs { get; } =
	[
		new(() => Text.General()), new(() => Text.ControllersSection()), new(() => Text.SettingsTabAudio()),
		new(() => Text.SettingsTabShortcuts()), new(() => Text.DesktopModeSection()), new(() => Text.GroupNotifications()),
	];

	// The shown tab; out of range values (-1: nothing selected) are ignored.
	public int SelectedTab
	{
		get => _selectedTab;
		set
		{
			if (value is < GeneralTab or > NotificationsTab || value == _selectedTab)
			{
				return;
			}
			_selectedTab = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(IsGeneralTab));
			OnPropertyChanged(nameof(IsControllersTab));
			OnPropertyChanged(nameof(IsAudioTab));
			OnPropertyChanged(nameof(IsShortcutsTab));
			OnPropertyChanged(nameof(IsMouseTab));
			OnPropertyChanged(nameof(IsNotificationsTab));
		}
	}

	public bool IsGeneralTab => SelectedTab == GeneralTab;
	public bool IsControllersTab => SelectedTab == ControllersTab;
	public bool IsAudioTab => SelectedTab == AudioTab;
	public bool IsShortcutsTab => SelectedTab == ShortcutsTab;
	public bool IsMouseTab => SelectedTab == MouseTab;
	public bool IsNotificationsTab => SelectedTab == NotificationsTab;

	// Opens a tab by number; one that does not exist opens General.
	public void ShowTab(int tab) => SelectedTab = tab is >= GeneralTab and <= NotificationsTab ? tab : GeneralTab;
}
