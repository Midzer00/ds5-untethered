// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Support (GitHub Sponsors): the pill in the sidebar and the link in Settings > General > Updates.
public static partial class Text
{
	public static string SupportLabel(Language? l = null) => Pick(l, $"Destek Ol", $"Support");
	public static string SupportTip(Language? l = null) => Pick(l,
		$"Hep ücretsiz. Destek tamamen isteğe bağlı.",
		$"Free forever. Sponsoring is optional.");
}
