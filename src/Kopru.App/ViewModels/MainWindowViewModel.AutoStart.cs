// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.App.ViewModels;

// The bridge's automatic start (SettingsState.AttachAutoStart): the app turns it on at start; it waits while the setup
// page is open and looks again when that page closes.
public sealed partial class MainWindowViewModel
{
	public void AttachAutoStart() => Settings.AttachAutoStart(() => Setup != null);

	partial void OnSetupChanged(SetupViewModel? value) => Settings.CheckAutoStart();
}
