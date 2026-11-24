// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// The virtual controller's row in Settings → General (where stopping it lives; the Overview only offers the start).
public static partial class Text
{
	public static string BridgeRowHint(Language? l = null) => T(l, "Bridge.RowHint");

	public static string BridgeRowStop(Language? l = null) => T(l, "Bridge.RowStop");

	public static string BridgeRowStart(Language? l = null) => T(l, "Bridge.RowStart");
}
