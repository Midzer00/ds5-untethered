// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the per-game controller type (DualSense, or an emulated Xbox 360 or DualShock 4 controller).
public static partial class Text
{
	public static string PadTypeLabel(Language? l = null) => Pick(l, $"Kumanda Türü", $"Controller Type");

	public static string PadTypeTip(Language? l = null) => Pick(l,
		$"Oyunun gördüğü kumanda. DualSense'i hiç tanımayan oyunlar için Xbox 360 seç; bu modda oyunun haptikleri, adaptif tetikleri ve hoparlörü olmaz, titreşim haptik olarak çalınır.",
		$"The controller the game sees. Pick Xbox 360 for games that don't support DualSense at all; in this mode there are no native haptics, adaptive triggers or speaker, and rumble is played as haptics.");

	public static string PadModeXboxTitle(Language? l = null) => Pick(l, $"Xbox 360 Modu", $"Xbox 360 Mode");

	public static string PadModeXboxBody(Language? l = null) => Pick(l,
		$"Bu oyun için kumanda Xbox 360 olarak görünüyor.",
		$"The controller appears as an Xbox 360 controller for this game.");

	public static string PadTypeDualShock4Tip(Language? l = null) => Pick(l,
		$"Yalnızca PS4 kumandasını tanıyan eski oyunlar için DualShock 4 seç; bu modda adaptif tetik ve oyun haptikleri yoktur, sadece titreşim ve ışık çalışır.",
		$"Pick DualShock 4 for older games that only know the PS4 controller; this mode has no adaptive triggers or game haptics, only rumble and light.");

	public static string PadModeDualShock4Title(Language? l = null) => Pick(l, $"DualShock 4 Modu", $"DualShock 4 Mode");

	public static string PadModeDualShock4Body(Language? l = null) => Pick(l,
		$"Bu oyun için kumanda DualShock 4 olarak görünüyor.",
		$"The controller appears as a DualShock 4 for this game.");

	public static string PadModeDualSenseTitle(Language? l = null) => Pick(l, $"DualSense Modu", $"DualSense Mode");

	public static string PadModeDualSenseBody(Language? l = null) => Pick(l,
		$"Kumanda yeniden DualSense olarak görünüyor.",
		$"The controller appears as a DualSense again.");
}
