// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Bridge;

namespace Kopru.App.ViewModels;

// DualSense Mode's part of the settings (see Core DualSenseMode) and the bridge's automatic start (Core BridgeAutoStart).
// The mode turns the controller speaker on with it for the game's sound effects; the user's own speaker switch is kept for
// when the mode is off. Turning the speaker off by hand while the mode is on holds until the mode is switched again.
public sealed partial class SettingsState
{
	private readonly BridgeAutoStart _autoStart = new();
	private DualSenseModeChoices _modeChoices = DualSenseModeChoices.None;
	// The mode as last seen on the UI thread (what Effective uses, so a change is pushed once, from the UI thread).
	private bool _modeOn;
	private Func<bool>? _autoStartHold;
	private bool _autoStartAttached;

	// Start the bridge by itself when the app starts and when the controller connects again (on by default).
	[ObservableProperty]
	private bool _autoStartBridge = true;

	public bool IsDualSenseModeOn => _modeOn;

	// The speaker switch as the tiles and the Settings page show it: on with DualSense Mode (unless turned off by hand since
	// the mode was switched), otherwise the user's own switch. Setting it while the mode is on changes only this session's
	// choice; with the mode off it is the user's own switch (SpeakerEnabled).
	public bool SpeakerActive
	{
		get => DualSenseMode.SpeakerShown(SpeakerEnabled, _modeOn, _modeChoices);
		set
		{
			if (!_modeOn)
			{
				SpeakerEnabled = value;
				return;
			}
			if (_modeChoices.SpeakerOff == !value)
			{
				return;
			}
			Kopru.Core.Diagnostics.AppLog.Write($"settings: speaker {(value ? "on" : "off")} for this DualSense Mode session (user)");
			_modeChoices = _modeChoices with { SpeakerOff = !value };
			// Pushes the new effective settings (OnPropertyChanged).
			OnPropertyChanged();
		}
	}

	partial void OnSpeakerEnabledChanged(bool value)
	{
		Kopru.Core.Diagnostics.AppLog.Write($"settings: speaker switch {(value ? "on" : "off")} ({ChangeSource})");
		OnPropertyChanged(nameof(SpeakerActive));
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

	// Constructor, after the saved game memory was loaded.
	private void InitDualSenseMode()
	{
		_modeOn = _service.Games.IsSupportedModeOn;
		_service.Games.StateChanged += _ => RunOnUi(OnModeMaybeChanged);
	}

	// The mode was switched (by the user, a game or a shortcut): this session's choices end, the whole set is on again.
	private void OnModeMaybeChanged()
	{
		bool on = _service.Games.IsSupportedModeOn;
		if (on == _modeOn)
		{
			return;
		}
		_modeOn = on;
		Kopru.Core.Diagnostics.AppLog.Write($"settings: DualSense Mode {(on ? "on" : "off")}");
		_modeChoices = DualSenseModeChoices.None;
		OnPropertyChanged(nameof(IsDualSenseModeOn));
		// Pushes the new effective settings once (OnPropertyChanged; Push only sends a change).
		OnPropertyChanged(nameof(SpeakerActive));
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
