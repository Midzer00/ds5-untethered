// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// The bridge's automatic start (Settings, General group).
public static partial class Text
{
	public static string AutoStartBridge(Language? l = null) => Pick(l, $"Sanal Kumandayı Kendiliğinden Başlat", $"Start The Virtual Controller Automatically");

	public static string AutoStartBridgeHint(Language? l = null) => Pick(l,
		$"Uygulama açılınca ve kumanda yeniden bağlanınca sanal kumanda kendiliğinden başlar. Elle durdurursan kumanda yeniden bağlanana kadar durur.",
		$"The virtual controller starts by itself when the app opens and when the controller connects again. If you stop it, it stays stopped until the controller connects again.");
}
