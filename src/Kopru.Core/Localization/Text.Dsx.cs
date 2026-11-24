// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Mods;

namespace Kopru.Core.Localization;

// DSX mod support (Settings, General group). "DSX" only names the mods this is compatible with.
public static partial class Text
{
	public static string DsxGroup(Language? l = null) => T(l, "Dsx.Group");

	public static string DsxModSupport(Language? l = null) => T(l, "Dsx.ModSupport");

	public static string DsxModSupportShort(Language? l = null) => T(l, "Dsx.ModSupportShort");

	public static string DsxModSupportHint(Language? l = null) => T(l, "Dsx.ModSupportHint");

	// The one-line state under the row while the setting is on (secondsAgo: since a mod's last message, null: none yet).
	public static string DsxStatus(DsxListenerState state, int port, int? secondsAgo, Language? l = null)
	{
		if (state == DsxListenerState.PortInUse)
		{
			return T(l, "Dsx.Status.PortInUse", A("port", port));
		}
		if (state == DsxListenerState.Failed)
		{
			return T(l, "Dsx.Status.Failed", A("port", port));
		}
		if (state == DsxListenerState.Off)
		{
			return T(l, "Dsx.Status.Off");
		}
		if (secondsAgo is { } ago)
		{
			int seconds = Math.Max(0, ago);
			return seconds < 60
				? T(l, "Dsx.Status.ActiveSeconds", A("port", port), A("seconds", seconds))
				: T(l, "Dsx.Status.ActiveMinutes", A("port", port), A("minutes", seconds / 60));
		}
		return T(l, "Dsx.Status.Listening", A("port", port));
	}
}
