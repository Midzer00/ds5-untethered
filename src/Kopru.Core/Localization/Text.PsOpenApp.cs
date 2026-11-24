// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// "PS: Open The App" and "Also In Games" (Settings, Controller Shortcuts group).
public static partial class Text
{
	public static string PsOpenApp(Language? l = null) => T(l, "Ps.OpenApp");

	public static string PsOpenAppHint(Language? l = null) => T(l, "Ps.OpenAppHint");

	public static string PsOpenAppInGames(Language? l = null) => T(l, "Ps.OpenAppInGames");

	public static string PsOpenAppInGamesShort(Language? l = null) => T(l, "Ps.OpenAppInGamesShort");

	public static string PsOpenAppInGamesHint(Language? l = null) => T(l, "Ps.OpenAppInGamesHint");
}
