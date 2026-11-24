// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the controller's look (how the app draws it: the default, a colour from the built-in pack, or the
// user's own picture): the tile on the Controller page and its picker.
public static partial class Text
{
	// The tile's title and the picker's.
	public static string LookLabel(Language? l = null) => T(l, "Look.Label");

	public static string LookTileAccessible(string name, Language? l = null) => T(l, "Look.TileAccessible", A("name", name));

	public static string LookChangeTip(Language? l = null) => T(l, "Look.ChangeTip");

	public static string LookPickerHint(Language? l = null) => T(l, "Look.PickerHint");

	public static string LookDefault(Language? l = null) => T(l, "Look.Default");

	public static string LookStandard(Language? l = null) => T(l, "Look.Standard");

	public static string LookSpecial(Language? l = null) => T(l, "Look.Special");

	public static string LookOwnAdd(Language? l = null) => T(l, "Look.OwnAdd");

	public static string LookOwn(Language? l = null) => T(l, "Look.Own");

	public static string LookOwnHint(Language? l = null) => T(l, "Look.OwnHint");

	public static string LookRemove(Language? l = null) => T(l, "Look.Remove");

	public static string LookRemoveTip(Language? l = null) => T(l, "Look.RemoveTip");

	public static string LookDone(Language? l = null) => T(l, "Look.Done");

	public static string LookEdgeNote(Language? l = null) => T(l, "Look.EdgeNote");

	public static string LookImageFailed(Language? l = null) => T(l, "Look.ImageFailed");

	public static string LookFileTitle(Language? l = null) => T(l, "Look.FileTitle");

	public static string LookFileTypes(Language? l = null) => T(l, "Look.FileTypes");

	public static string LookSelected(string name, Language? l = null) => T(l, "Look.Selected", A("name", name));
}
