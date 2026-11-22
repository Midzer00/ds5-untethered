// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Services;
using Kopru.Core.Audio;

namespace Kopru.App.ViewModels;

// "Protect My Default Audio Device" (Settings, controller microphone group; global, not per profile) and the flows where
// the user made the controller the default on purpose (learned by Core DefaultAudioGuard, saved with the settings).
public sealed partial class SettingsState
{
	private DefaultAudioGuard? _audioGuard;

	[ObservableProperty]
	private bool _protectDefaultAudio = true;

	public AudioGuardPreferences AudioChoices { get; private set; } = new();

	partial void OnProtectDefaultAudioChanged(bool value)
	{
		if (_audioGuard != null)
		{
			_audioGuard.Enabled = value;
		}
		Kopru.Core.Diagnostics.AppLog.Write($"settings: protect default audio device {(value ? "on" : "off")} ({ChangeSource})");
	}

	// The app gives the guard every bridge host uses; tests may give their own.
	public void AttachAudioGuard(DefaultAudioGuard guard)
	{
		_audioGuard = guard;
		guard.Enabled = ProtectDefaultAudio;
		guard.LoadPreferences(AudioChoices);
		guard.PreferencesChanged += choices => RunOnUi(() => RememberAudioChoices(choices));
	}

	// A choice learned (or forgotten) by the guard: saved.
	public void RememberAudioChoices(AudioGuardPreferences choices)
	{
		if (choices == AudioChoices)
		{
			return;
		}
		AudioChoices = choices;
		ScheduleSave();
	}

	private void LoadAudioDefaults(AppSettings saved)
	{
		ProtectDefaultAudio = saved.ProtectDefaultAudio;
		AudioChoices = new AudioGuardPreferences(saved.ControllerSpeakerChosen, saved.ControllerMicChosen);
	}
}
