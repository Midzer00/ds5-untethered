// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.App.ViewModels;

public sealed partial class MainWindowViewModel
{
	// Opens the Settings page scrolled to one of its groups (see SettingsSections).
	public void OpenSettingsAt(string section)
	{
		Navigate(SettingsPage);
		SettingsPage.ShowSection(section);
	}
}
