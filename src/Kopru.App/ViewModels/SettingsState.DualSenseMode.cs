// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Bridge;

namespace Kopru.App.ViewModels;

// The speaker switch and the bridge's automatic start (Core BridgeAutoStart). The DualSense Mode switch that used to live
// here is gone (its saved state is migrated once, AppSettings.MigrateUnifiedProfile): SpeakerEnabled is the one speaker
// switch, saved in the profile.
public sealed partial class SettingsState
{
	private readonly BridgeAutoStart _autoStart = new();
	private Func<bool>? _autoStartHold;
	private bool _autoStartAttached;

	// Start the bridge by itself when the app starts and when the controller connects again (on by default).
	[ObservableProperty]
	private bool _autoStartBridge = true;

	partial void OnSpeakerEnabledChanged(bool value)
	{
		Kopru.Core.Diagnostics.AppLog.Write($"settings: speaker switch {(value ? "on" : "off")} ({ChangeSource})");
		OnPropertyChanged(nameof(SpeakerWorks));
	}

	// Who changed a setting, for the log.
	private string ChangeSource => _loading ? "saved settings" : _applyingProfile ? "profile" : _micFromButton ? "controller button" : "user";

	partial void OnAutoStartBridgeChanged(bool value)
	{
		if (!_loading)
		{
			CheckAutoStart();
		}
	}

	// Turns the automatic start on for this app run (the app does; tests opt in). hold: true while it must wait (e.g. the
	// setup page is open).
	public void AttachAutoStart(Func<bool>? hold = null)
	{
		_autoStartHold = hold;
		if (!_autoStartAttached)
		{
			_autoStartAttached = true;
			_service.StateChanged += (_, _) => RunOnUi(CheckAutoStart);
		}
		CheckAutoStart();
	}

	// On every state change (and when the setting or the hold changes).
	public void CheckAutoStart()
	{
		if (!_autoStartAttached)
		{
			return;
		}
		bool ready = _service.CanStartBridge && _autoStartHold?.Invoke() != true;
		if (_autoStart.Update(AutoStartBridge, ready, _service.IsConnected, _service.IsBridgeRunning))
		{
			Kopru.Core.Diagnostics.AppLog.Write("auto start: controller connected, starting the virtual controller");
			_ = AutoStartAsync();
		}
	}

	private async Task AutoStartAsync()
	{
		try
		{
			await _service.StartBridgeAsync();
		}
		catch (Exception)
		{
			// The bridge reports its own error; the next reconnect (or the Start button) tries again.
		}
	}
}
