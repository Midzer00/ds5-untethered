// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of several controllers at once (player slots, the controller list and selector, per-controller profile).
public static partial class Text
{
	public static string ControllerSlotName(int slot, Language? l = null) => Pick(l,
		$"Kumanda {slot}",
		$"Controller {slot}");

	public static string ControllerCount(int count, Language? l = null) => Pick(l,
		$"{count} Kumanda",
		$"{count} Controllers");

	public static string ControllerSelectorLabel(Language? l = null) => Pick(l,
		$"Kumanda Seç",
		$"Choose Controller");

	public static string ControllerSelectorHelp(Language? l = null) => Pick(l,
		$"Bu sayfa seçili kumandayı gösterir ve test eder.",
		$"This page shows and tests the selected controller.");

	public static string ControllerProfileLabel(Language? l = null) => Pick(l,
		$"Bu Kumandanın Profili",
		$"This Controller's Profile");

	public static string ControllerProfileGlobal(Language? l = null) => Pick(l,
		$"Genel Ayarlar",
		$"Global Settings");

	public static string NotifyControllerConnectedTitle(int slot, Language? l = null) => Pick(l,
		$"Kumanda {slot} Bağlandı",
		$"Controller {slot} Connected");

	public static string NotifyControllerDisconnectedTitle(int slot, Language? l = null) => Pick(l,
		$"Kumanda {slot} Bağlantısı Kesildi",
		$"Controller {slot} Disconnected");
}
