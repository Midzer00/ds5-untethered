// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the cable mode setting (Settings, Notifications tab, its own card) and the wired connection name.
public static partial class Text
{
	// The card's title and the setting row labels and choices (Title Case).
	public static string CardCable(Language? l = null) => T(l, "Card.Cable");
	public static string CableModeLabel(Language? l = null) => T(l, "Cable.ModeLabel");
	public static string CableModeWired(Language? l = null) => T(l, "Cable.ModeWired");
	public static string CableModeChargeOnly(Language? l = null) => T(l, "Cable.ModeChargeOnly");

	// Descriptions (sentence case). The row's line describes the selected choice: Use The Cable, or Charge Only.
	public static string CableModeShort(Language? l = null) => T(l, "Cable.ModeShort");
	public static string CableModeShortChargeOnly(Language? l = null) => T(l, "Cable.ModeShortChargeOnly");

	public static string CableModeHint(Language? l = null) => T(l, "Cable.ModeHint");

	// The connection name shown while the bridge talks to the controller over its USB cable.
	public static string ConnectionWired(Language? l = null) => T(l, "Connection.Wired");
}
