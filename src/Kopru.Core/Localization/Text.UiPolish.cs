// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Labels of the setting rows added in the 0.2 layout pass: the try row of Xbox And DS4 Rumble, the fallback effect's
// strength line, and the rows of a game's pop-up (start file, sound range).
public static partial class Text
{
	// Haptics And Triggers: the row of try buttons at the end of the Xbox And DS4 Rumble card.
	public static string TryRowLabel(Language? l = null) => Pick(l, $"Dene", $"Try It");

	public static string TryRowHint(Language? l = null) => Pick(l, $"Bu ayarlarla kumandada çalar.", $"Plays on the controller with these settings.");

	// The try buttons in that row (the row's label says "try").
	public static string TryTriggerEffect(Language? l = null) => Pick(l, $"Tetik Efekti", $"Trigger Effect");

	public static string FallbackStrengthHint(Language? l = null) => Pick(l, $"Yedek efektin tetikteki gücü.", $"How strong the fallback effect feels.");

	// A game's pop-up: the start file row (its label, the button while none is chosen, Change while one is).
	public static string StartFileLabel(Language? l = null) => Pick(l, $"Başlatma Dosyası", $"Start File");

	public static string StartFilePick(Language? l = null) => Pick(l, $"Dosya Seç…", $"Choose File…");

	public static string StartFilePickNone(Language? l = null) => Pick(l, $"Seçilmedi", $"None chosen");

	public static string StartFileChangeShort(Language? l = null) => Pick(l, $"Değiştir", $"Change");

	// A game's pop-up: Bass or All Sound under Turn Sound Into Haptics.
	public static string AudioHapticsBandLabel(Language? l = null) => Pick(l, $"Ses Aralığı", $"Sound Range");

	// A game's pop-up: the light switch's group.
	public static string GamePanelLightGroup(Language? l = null) => Pick(l, $"Işık", $"Light");
}
