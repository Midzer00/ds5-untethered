// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.App.ViewModels;

public sealed partial class MainWindowViewModel
{
	// Opens the Settings page on one of its tabs (SettingsViewModel.GeneralTab...).
	public void OpenSettingsAt(int tab)
	{
		SettingsPage.ShowTab(tab);
		Navigate(SettingsPage);
	}
}
