// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// A trigger effect's shape: the vibration's frequency (a game's Custom Trigger; Start and End are in Text.CustomTrigger).
public static partial class Text
{
	public static string FallbackFrequency(Language? l = null) => T(l, "Fallback.Frequency");
}
