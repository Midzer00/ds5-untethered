// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the default controller type ("Varsayılan Kumanda Türü" in Quick Settings and on the Games page)
// and its choices. Product names stay the same in every language.
public static partial class Text
{
	public static string PadChoiceAuto(Language? l = null) => Pick(l, $"Otomatik", $"Automatic");
}
