// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.App.ViewModels;

public sealed partial class SettingsState
{
	private LiveLight? _live;

	// The light the selected controller shows now (the controller drawings bind to it); MainWindowViewModel runs it.
	public LiveLight Live => _live ??= new LiveLight(_service);
}
