// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Support (GitHub Sponsors): the pill in the sidebar and the link in Settings > General > Updates.
public static partial class Text
{
	public static string SupportLabel(Language? l = null) => T(l, "Support.Label");

	// The star button beside the pill: opens the project's GitHub page (star icon and this label; the next line is its tooltip and name).
	public static string StarLabel(Language? l = null) => T(l, "Star.Label");
	public static string StarOnGitHub(Language? l = null) => T(l, "Star.OnGitHub");
	public static string SupportTip(Language? l = null) => T(l, "Support.Tip");
}
