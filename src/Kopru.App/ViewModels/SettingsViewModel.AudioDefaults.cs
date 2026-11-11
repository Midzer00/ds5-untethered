// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// "Keep My Default Audio Device" and "Mic Button" rows of the microphone group (the device pickers bind Settings.Audio).
public sealed partial class SettingsViewModel
{
	public string ProtectDefaultAudioLabel => Text.ProtectDefaultAudio();
	public string ProtectDefaultAudioHint => Text.ProtectDefaultAudioHint();

	public string MicButtonModeLabel => Text.MicButtonModeLabel();
	public string MicButtonModeHint => Text.MicButtonModeHint();

	// 0 = Sessize Al, 1 = Tamamen Kapat (the Quick Settings row uses the same order).
	public IReadOnlyList<string> MicButtonModes => _micButtonModes is { } m && m.SequenceEqual([Text.MicButtonMute(), Text.MicButtonTurnOff()])
		? m
		: _micButtonModes = [Text.MicButtonMute(), Text.MicButtonTurnOff()];

	private IReadOnlyList<string>? _micButtonModes;

	public int MicButtonModeIndex
	{
		get => Settings.MicButtonMode == MicButtonMode.Mute ? 0 : 1;
		set
		{
			if (value is 0 or 1)
			{
				Settings.MicButtonMode = value == 0 ? MicButtonMode.Mute : MicButtonMode.TurnOff;
			}
		}
	}
}
