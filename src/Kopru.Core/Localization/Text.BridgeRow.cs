// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// The virtual controller's row in Settings → General (where stopping it lives; the Overview only offers the start).
public static partial class Text
{
	public static string BridgeRowHint(Language? l = null) => Pick(l,
		$"Kumandayı başka bir programa bırakmak için durdur. Kendiliğinden başlatma açıksa kumanda yeniden bağlanınca yine başlar.",
		$"Stop it to hand the controller to another program. With automatic start on, it starts again when the controller reconnects.");

	public static string BridgeRowStop(Language? l = null) => Pick(l, $"Durdur", $"Stop");

	public static string BridgeRowStart(Language? l = null) => Pick(l, $"Başlat", $"Start");
}
