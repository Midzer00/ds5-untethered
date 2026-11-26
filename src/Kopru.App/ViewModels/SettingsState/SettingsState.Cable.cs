// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Bridge;

namespace Kopru.App.ViewModels;

// "When The Cable Is Plugged In" (Settings, Notifications tab, its own card; an app setting, not per profile): what the bridge
// does while the controller's cable is in — Charge Only keeps everything on Bluetooth and hides the USB twin,
// Use The Cable (experimental) moves input, output and audio onto it.
public sealed partial class SettingsState
{
	// The cable behaviour while the saved setting is missing: Charge Only. Wired mode is opt-in (the user picks
	// "Use The Cable").
	public const CableMode DefaultCableMode = CableMode.ChargeOnly;

	[ObservableProperty]
	private CableMode _cableMode = DefaultCableMode;

	partial void OnCableModeChanged(CableMode value) =>
		Kopru.Core.Diagnostics.AppLog.Write($"settings: cable mode {(value == CableMode.Wired ? "wired" : "charge only")} ({ChangeSource})");
}
