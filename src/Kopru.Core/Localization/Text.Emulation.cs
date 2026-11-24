// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the per-game controller type (DualSense, or an emulated Xbox 360 or DualShock 4 controller).
public static partial class Text
{
	public static string PadTypeLabel(Language? l = null) => T(l, "Pad.TypeLabel");

	public static string PadModeXboxTitle(Language? l = null) => T(l, "Pad.ModeXboxTitle");

	public static string PadModeDualShock4Title(Language? l = null) => T(l, "Pad.ModeDualShock4Title");

	public static string PadModeDualSenseTitle(Language? l = null) => T(l, "Pad.ModeDualSenseTitle");

	public static string PadModeDualSenseBody(Language? l = null) => T(l, "Pad.ModeDualSenseBody");

	// The controller type model (the one per-game choice; PadTypeLabel above is its label).
	public static string PadTypeGameHint(Language? l = null) => T(l, "Pad.TypeGameHint");

	public static string PadTypeDefaultLabel(Language? l = null) => T(l, "Pad.TypeDefaultLabel");

	public static string PadTypeDefaultHint(Language? l = null) => T(l, "Pad.TypeDefaultHint");

	public static string PadChoiceAutoPerGame(Language? l = null) => T(l, "Pad.ChoiceAutoPerGame");

	// A game's Automatic choice with what it gives the game now ("Otomatik (Xbox 360)"); type: the product name.
	public static string PadChoiceAutoFor(string type, Language? l = null) => T(l, "Pad.ChoiceAutoFor", A("type", type));

	// What Automatic means on a game's picker: the best controller for the game; with a default type set, that type for a
	// game we know nothing about.
	public static string PadTypeAutoLine(Language? l = null) => T(l, "Pad.TypeAutoLine");

	public static string PadTypeAutoDefaultLine(Language? l = null) => T(l, "Pad.TypeAutoDefaultLine");

	public static string PadChoiceAutoLine(Language? l = null) => T(l, "Pad.ChoiceAutoLine");

	public static string PadTypeDualSenseLine(Language? l = null) => T(l, "Pad.TypeDualSenseLine");

	public static string PadTypeDualShock4Line(Language? l = null) => T(l, "Pad.TypeDualShock4Line");

	public static string PadTypeXboxLine(Language? l = null) => T(l, "Pad.TypeXboxLine");

	// type: the product name (DualSense, DualShock 4, Xbox 360).
	public static string GameSees(string type, Language? l = null) => T(l, "Game.Sees", A("type", type));

	// A controller type change while the game runs waits for the game to restart.
	public static string PadTypeDeferred(Language? l = null) => T(l, "Pad.TypeDeferred");

	public static string PadTypeDeferredHint(Language? l = null) => T(l, "Pad.TypeDeferredHint");

	public static string PadTypeApplyNow(Language? l = null) => T(l, "Pad.TypeApplyNow");

	// The controller type toast while a type waits for the running game to restart.
	public static string PadToastPendingBody(Language? l = null) => T(l, "Pad.ToastPendingBody");

	// After a live swap of the virtual controller.
	public static string PadSwapRestartHint(Language? l = null) => T(l, "Pad.SwapRestartHint");
}
