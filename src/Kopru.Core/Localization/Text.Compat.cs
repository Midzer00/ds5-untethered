// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Steam;

namespace Kopru.Core.Localization;

// User-facing texts of compatibility checks (Steam Input).
public static partial class Text
{
	public static string SteamInputAdvice(SteamInputStatus status, Language? l = null) => status switch
	{
		SteamInputStatus.NotInstalled => T(l, "Steam.InputAdvice.NotInstalled"),
		SteamInputStatus.Off => T(l, "Steam.InputAdvice.Off"),
		SteamInputStatus.OnGlobally => T(l, "Steam.InputAdvice.OnGlobally"),
		SteamInputStatus.OnForApp => T(l, "Steam.InputAdvice.OnForApp"),
		_ => T(l, "Steam.InputAdvice.Unknown")
	};
}
