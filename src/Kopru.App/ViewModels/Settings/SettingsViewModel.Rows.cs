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
	// Linux (experimental): what is not there yet, under the version.
	public string LinuxNote => Text.LinuxExperimentalNote();
	public string AutoStartBridgeShort => Text.AutoStartBridgeShort();
	public string BridgeRowShort => Text.BridgeRowShort();
	public string BridgeRowHint => Text.BridgeRowHint();
	public string PowerOffWhenIdleShort => Text.PowerOffWhenIdleShort();
	public string AutoLowPowerShort => Text.AutoLowPowerShort();
	public string PauseHapticsShort => Text.PauseHapticsInSilenceShort();
	public string AudioOutputShort => Text.AudioOutputShort();
	public string AudioInputShort => Text.AudioInputShort();
	public string MicSendShort => Text.MicSendShort();
	public string MicButtonModeShort => Text.MicButtonModeShort();
	public string ProtectDefaultAudioShort => Text.ProtectDefaultAudioShort();
	public string NotifyLinkShort => Text.NotifyLinkShort();

	// The experimental Xbox Trigger Rumble switch (Controllers tab, its own Triggers card in the right column): the title
	// with a muted Experimental tag; the full name (tag included) is the switch's accessible name.
	public string TriggersGroupLabel => Text.TriggersGroup();
	public string ExperimentalTag => Text.ExperimentalTag();
	public string XboxTriggerRumbleTitle => Text.XboxTriggerRumbleTitle();
	public string XboxTriggerRumbleLabel => Text.XboxTriggerRumbleSetting();
	public string XboxTriggerRumbleShort => Text.XboxTriggerRumbleSettingShort();
	public string XboxTriggerRumbleHint => Text.XboxTriggerRumbleSettingHint();
}
