// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// "Start The Bridge Automatically" row of the General group, and the "Virtual Controller" row under it (stop / start).
public sealed partial class SettingsViewModel
{
	public string AutoStartBridgeLabel => Text.AutoStartBridge();
	public string AutoStartBridgeHint => Text.AutoStartBridgeHint();

	// The Overview's view model: the row binds its ToggleBridge command and state, so the busy guard is the same one.
	// Null only when a test builds the page alone (the row is then hidden).
	public OverviewViewModel? Bridge { get; init; }
	public bool HasBridge => Bridge != null;
}
