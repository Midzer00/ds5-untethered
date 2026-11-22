// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the controller's look (how the app draws it: the default, a colour from the built-in pack, or the
// user's own picture): the tile on the Controller page and its picker.
public static partial class Text
{
	// The tile's title and the picker's.
	public static string LookLabel(Language? l = null) => Pick(l, $"Kumanda Rengi", $"Controller Colour");

	public static string LookTileAccessible(string name, Language? l = null) => Pick(l, $"Kumanda Rengi: {name}", $"Controller Colour: {name}");

	public static string LookChangeTip(Language? l = null) => Pick(l,
		$"Bu kumandanın uygulamada nasıl çizileceğini seç.",
		$"Choose how this controller is drawn in the app.");

	public static string LookPickerHint(Language? l = null) => Pick(l,
		$"Yalnızca uygulamadaki çizimi değiştirir; her kumandanın kendi seçimi olur.",
		$"Only changes the drawing in the app; each controller keeps its own choice.");

	public static string LookDefault(Language? l = null) => Pick(l, $"Varsayılan", $"Default");

	public static string LookStandard(Language? l = null) => Pick(l, $"Standart", $"Standard");

	public static string LookSpecial(Language? l = null) => Pick(l, $"Özel Sürümler", $"Special Editions");

	public static string LookOwnAdd(Language? l = null) => Pick(l, $"Görsel Seç…", $"Choose Image…");

	public static string LookOwn(Language? l = null) => Pick(l, $"Kendi Görselin", $"Own Image");

	public static string LookOwnHint(Language? l = null) => Pick(l,
		$"PNG, JPG veya WebP. Olduğu gibi çizilir; ışık ve basılan tuşlar üzerinde görünmez.",
		$"PNG, JPG or WebP. It is drawn as it is, without the light or pressed buttons on it.");

	public static string LookRemove(Language? l = null) => Pick(l, $"Kaldır", $"Remove");

	public static string LookRemoveTip(Language? l = null) => Pick(l,
		$"Kendi görselini siler; kumanda varsayılan çizimine döner.",
		$"Deletes your own image; the controller goes back to the default drawing.");

	public static string LookDone(Language? l = null) => Pick(l, $"Tamam", $"Done");

	public static string LookEdgeNote(Language? l = null) => Pick(l,
		$"Renk seçenekleri standart kumanda içindir; bu kumandaya kendi görselini verebilirsin.",
		$"The colours are for the standard controller; you can give this one your own image.");

	public static string LookImageFailed(Language? l = null) => Pick(l,
		$"Bu dosya resim olarak okunamadı.",
		$"That file could not be read as a picture.");

	public static string LookFileTitle(Language? l = null) => Pick(l, $"Kumanda İçin Bir Resim Seç", $"Choose A Picture For The Controller");

	public static string LookFileTypes(Language? l = null) => Pick(l, $"Resimler", $"Pictures");

	public static string LookSelected(string name, Language? l = null) => Pick(l, $"{name}, seçili", $"{name}, selected");
}
