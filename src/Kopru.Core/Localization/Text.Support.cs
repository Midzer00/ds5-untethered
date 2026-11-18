// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Support (GitHub Sponsors): the pill in the sidebar and the link in Settings > General > Updates.
public static partial class Text
{
	public static string SupportLabel(Language? l = null) => Pick(l, $"Destek Ol", $"Support");

	// The star button beside the pill: opens the project's GitHub page (star icon and this label; the next line is its tooltip and name).
	public static string StarLabel(Language? l = null) => Pick(l, $"Yıldızla", $"Star");
	public static string StarOnGitHub(Language? l = null) => Pick(l, $"GitHub'da yıldız ver", $"Star on GitHub");
	public static string SupportTip(Language? l = null) => Pick(l,
		$"Hep ücretsiz. Destek tamamen isteğe bağlı.",
		$"Free forever. Sponsoring is optional.");
}
