// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of several controllers at once (player slots, the controller list and selector, per-controller profile).
public static partial class Text
{
	public static string ControllerSlotName(int slot, Language? l = null) => T(l, "Controller.SlotName", A("slot", slot));

	public static string ControllerCount(int count, Language? l = null) => T(l, "Controller.Count", A("count", count));

	public static string ControllerSelectorLabel(Language? l = null) => T(l, "Controller.SelectorLabel");

	public static string ControllerSelectorHelp(Language? l = null) => T(l, "Controller.SelectorHelp");

	public static string ControllerProfileLabel(Language? l = null) => T(l, "Controller.ProfileLabel");

	public static string ControllerProfileGlobal(Language? l = null) => T(l, "Controller.ProfileGlobal");

	public static string NotifyControllerConnectedTitle(int slot, Language? l = null) => T(l, "Notify.ControllerConnectedTitle", A("slot", slot));

	public static string NotifyControllerDisconnectedTitle(int slot, Language? l = null) => T(l, "Notify.ControllerDisconnectedTitle", A("slot", slot));
}
