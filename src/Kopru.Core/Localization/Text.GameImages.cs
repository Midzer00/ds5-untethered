// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the game pictures on the supported games list.
public static partial class Text
{
	public static string DownloadGameImages(Language? l = null) => Pick(l, $"Oyun Görsellerini İndir", $"Download Pictures");

	public static string DownloadGameImagesHint(Language? l = null) => Pick(l,
		$"Bilgisayarda olmayan oyun görselleri Steam'den indirilir ve yalnızca bu bilgisayarda saklanır. Kapalıyken yalnızca Steam kütüphanenizdeki görseller ve eklediğiniz oyunların simgeleri gösterilir.",
		$"Game pictures not already on this PC are downloaded from Steam and stored only on this PC. When off, only the pictures in your Steam library and the icons of games you added are shown.");}
