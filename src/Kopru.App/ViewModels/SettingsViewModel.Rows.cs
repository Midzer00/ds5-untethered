// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// The Settings rows' card titles and short lines. A row shows one short line under its title; the longer explanation
// (the *Hint properties) is the row's tooltip and its control's help text.
public sealed partial class SettingsViewModel
{
	public string AppGroupLabel => Text.SettingsAppGroup();
	public string PowerGroupLabel => Text.RowPower();
	public string NotifyControllerGroupLabel => Text.NotifyControllerGroup();
	public string NotifyGamesGroupLabel => Text.NotifyGamesGroup();
	public string ShortcutCombosLabel => Text.ShortcutCombosGroup();

	public string StartWithWindowsShort => Text.StartWithWindowsShort();
	public string KeepInTrayShort => Text.KeepInTrayShort();
	public string AutoStartBridgeShort => Text.AutoStartBridgeShort();
	public string BridgeRowShort => Text.BridgeRowShort();
	public string BridgeRowHint => Text.BridgeRowHint();
	public string PowerOffWhenIdleShort => Text.PowerOffWhenIdleShort();
	public string AutoLowPowerShort => Text.AutoLowPowerShort();
	public string AudioOutputShort => Text.AudioOutputShort();
	public string AudioInputShort => Text.AudioInputShort();
	public string SpeakerSendShort => Text.SpeakerSendShort();
	public string MicSendShort => Text.MicSendShort();
	public string MicButtonModeShort => Text.MicButtonModeShort();
	public string ProtectDefaultAudioShort => Text.ProtectDefaultAudioShort();
	public string NotifyLinkShort => Text.NotifyLinkShort();
	public string NotifyHidHideShort => Text.NotifyHidHideShort();
}
