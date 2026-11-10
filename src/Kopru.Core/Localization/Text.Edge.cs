// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the DualSense Edge support (back paddles, presentation).
public static partial class Text
{
	public static string EdgeLeftPaddle(Language? l = null) => Pick(l, $"Sol Arka Tuş", $"Left Back Paddle");

	public static string EdgeRightPaddle(Language? l = null) => Pick(l, $"Sağ Arka Tuş", $"Right Back Paddle");

	public static string EdgeKeepPaddle(Language? l = null) => Pick(l, $"Arka Tuş Olarak Bırak", $"Keep as Paddle");

	public static string EdgePaddlesTitle(Language? l = null) => Pick(l, $"Edge Arka Tuşları", $"Edge Back Paddles");

	public static string EdgePaddlesHint(Language? l = null) => Pick(l,
		$"Arka tuşlara bir DualSense tuşu atayın. Fn tuşları ileride kullanılmak üzere ayrılmıştır.",
		$"Assign a DualSense button to each back paddle. The Fn buttons are reserved for later use.");

	public static string PresentAsEdge(Language? l = null) => Pick(l, $"Edge Olarak Göster", $"Present as Edge");

	public static string PresentAsEdgeHint(Language? l = null) => Pick(l,
		$"Kapalıyken oyunlar kumandayı standart DualSense olarak görür; yalnızca DualSense'i tanıyan oyunlar için. Bir sonraki bağlantıda geçerli olur.",
		$"When off, games see a standard DualSense; for games that only know the DualSense. Takes effect on the next connection.");
}
