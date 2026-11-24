// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the HidHide integration.
public static partial class Text
{
	public static string HidHideNotInstalled(Language? l = null) => T(l, "Hid.HideNotInstalled");

	public static string HidHideCommandFailed(string command, string detail, Language? l = null) => T(l, "Hid.HideCommandFailed", A("command", command), A("detail", detail));

	public static string HidHideHideFailed(string detail, Language? l = null) => T(l, "Hid.HideHideFailed", A("detail", detail));

	public static string HidHideRestoreFailed(string detail, Language? l = null) => T(l, "Hid.HideRestoreFailed", A("detail", detail));

	public static string HidHideNoAppPath(Language? l = null) => T(l, "Hid.HideNoAppPath");

	public static string HidHideActive(Language? l = null) => T(l, "Hid.HideActive");

	public static string HidHideRestored(Language? l = null) => T(l, "Hid.HideRestored");
}
