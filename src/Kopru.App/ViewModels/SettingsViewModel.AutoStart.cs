// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// "Start The Bridge Automatically" row of the General group.
public sealed partial class SettingsViewModel
{
	public string AutoStartBridgeLabel => Text.AutoStartBridge();
	public string AutoStartBridgeHint => Text.AutoStartBridgeHint();
}
