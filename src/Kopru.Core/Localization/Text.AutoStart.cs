// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// The bridge's automatic start (Settings, General group).
public static partial class Text
{
	public static string AutoStartBridge(Language? l = null) => T(l, "Auto.StartBridge");

	public static string AutoStartBridgeHint(Language? l = null) => T(l, "Auto.StartBridgeHint");
}
