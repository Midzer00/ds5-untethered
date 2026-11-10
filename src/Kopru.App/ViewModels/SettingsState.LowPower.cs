// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Bridge;

namespace Kopru.App.ViewModels;

// Low Power mode: an override on top of the active profile, applied only to what goes to the controller (see
// Core LowPower). The values here and in the profile stay the user's; only the switch itself is saved.
public sealed partial class SettingsState
{
	private readonly LowPowerAutoSwitch _autoLowPowerSwitch = new();

	[ObservableProperty]
	private bool _lowPowerOn;

	// Turn Low Power on when the battery drops below 20 % while not charging (once per discharge).
	[ObservableProperty]
	private bool _autoLowPower;

	// What the controller gets: the settings with DualSense Mode's set, then Low Power on top (Low Power wins).
	public ControllerSettings Effective(ControllerSettings settings) => DualSenseMode.Effective(settings, _modeOn, _modeChoices, LowPowerOn);

	partial void OnAutoLowPowerChanged(bool value)
	{
		if (!_loading)
		{
			CheckAutoLowPower();
		}
	}

	private void InitLowPower() => _service.StateChanged += (_, _) => RunOnUi(CheckAutoLowPower);

	// On every state change (battery readings included).
	private void CheckAutoLowPower()
	{
		double? percent = _service.IsConnected && _service.HasBattery ? _service.BatteryPercent : null;
		if (_autoLowPowerSwitch.Update(AutoLowPower, percent, _service.IsCharging, LowPowerOn))
		{
			LowPowerOn = true;
		}
	}
}
