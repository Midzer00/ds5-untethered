// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Strings of bridge robustness (watchdog).
public static partial class Text
{
	public static string BridgeWatchdog(Language? l = null) => Pick(l,
		$"Sanal kumanda yanıt vermeyi bıraktı ve yeniden başlatıldı",
		$"The virtual controller stopped responding and was restarted");
}
