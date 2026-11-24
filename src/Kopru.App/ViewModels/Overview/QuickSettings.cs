// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.ViewModels;

// The Overview's quick settings card: Low Power, Mouse Mode, controller shortcuts, speaker and microphone, the default
// controller type, three-step haptics / trigger / light choices and the profile.
// Every row edits the same settings the detailed pages edit. While Low Power is on the rows it overrides are disabled
// but keep showing the user's own values.
public sealed partial class QuickSettingsViewModel : ViewModelBase
{
	private readonly IControllerService _service;

	public QuickSettingsViewModel(IControllerService service, SettingsState settings)
	{
		_service = service;
		Settings = settings;
		settings.PropertyChanged += (_, e) =>
		{
			switch (e.PropertyName)
			{
				case nameof(SettingsState.LowPowerOn):
					OnPropertyChanged(nameof(LowPowerOn));
					OnPropertyChanged(nameof(CanEditOverridden));
					OnSpeakerStateChanged();
					OnPropertyChanged(nameof(MicFeatureOff));
					OnPropertyChanged(nameof(MicStateText));
					OnPropertyChanged(nameof(MicTip));
					break;
				case nameof(SettingsState.GameHapticsLevel):
					OnPropertyChanged(nameof(HapticsIndex));
					OnPropertyChanged(nameof(HapticsCustom));
					OnPropertyChanged(nameof(HasHapticsCustom));
					break;
				case nameof(SettingsState.TriggerStrength):
					OnPropertyChanged(nameof(TriggerIndex));
					OnPropertyChanged(nameof(TriggerCustom));
					OnPropertyChanged(nameof(HasTriggerCustom));
					break;
				case nameof(SettingsState.Brightness):
					OnPropertyChanged(nameof(LightIndex));
					OnPropertyChanged(nameof(LightCustom));
					OnPropertyChanged(nameof(HasLightCustom));
					break;
				case nameof(SettingsState.SpeakerEnabled):
				case nameof(SettingsState.CurrentPadType):
					OnSpeakerStateChanged();
					break;
				case nameof(SettingsState.MicLive):
					OnPropertyChanged(nameof(MicTile));
					break;
				case nameof(SettingsState.MicEnabled):
				case nameof(SettingsState.ControllerMicSelected):
				case nameof(SettingsState.MicStateText):
				case nameof(SettingsState.CanPressMic):
				case nameof(SettingsState.MicButtonMode):
					OnPropertyChanged(nameof(MicFeatureOff));
					OnPropertyChanged(nameof(MicStateText));
					OnPropertyChanged(nameof(CanPressMic));
					OnPropertyChanged(nameof(MicButtonModeIndex));
					OnPropertyChanged(nameof(MicTip));
					break;
			}
		};
		service.StateChanged += (_, _) => RunOnUi(() =>
		{
			OnPropertyChanged(nameof(ShowsDisconnectedNote));
			OnPropertyChanged(nameof(ShowsOutputPicker));
			OnPropertyChanged(nameof(MicPickerColumn));
			OnPropertyChanged(nameof(MicPickerSpan));
		});
		settings.PropertyChanged += (_, e) => OnTileSettingChanged(e.PropertyName);
	}

	public SettingsState Settings { get; }

	// Opens the Settings page (set by the main window).
	public Action? OpenAdvanced { get; set; }

	public string Title => Text.QuickSettingsTitle();
	public string LowPowerLabel => Text.LowPower();
	public string LowPowerNote => Text.LowPowerNote();
	// Under the card's title while Low Power is on.
	public string LowPowerOffNote => Text.LowPowerOffNote();
	public string HapticsLabel => Text.HapticsStrength();
	public string TriggerLabel => Text.TriggerStrength();
	public string LightLabel => Text.QuickLight();
	public string SpeakerLabel => Text.LiveSpeaker();
	public string MicLabel => Text.QuickMicrophone();
	public string AdvancedText => Text.AdvancedSettings();
	public string DisconnectedNote => Text.AppliedWhenConnected();

	// Step names: the same lists for good (only the names follow the language), so neither a re-read of the bindings (every
	// StateChanged refreshes the Overview) nor a language switch resets the selection.
	public IReadOnlyList<LabelledChoice> StrengthSteps { get; } = [new(() => Text.StepLight()), new(() => Text.StepNormal()), new(() => Text.StepStrong())];
	public IReadOnlyList<LabelledChoice> LightSteps { get; } = [new(() => Text.StepOff()), new(() => Text.StepLow()), new(() => Text.StepHigh())];

	// Without a controller the card stays usable; the values go to it when it connects.
	public bool ShowsDisconnectedNote => !_service.IsConnected;

	public bool LowPowerOn
	{
		get => Settings.LowPowerOn;
		set => Settings.LowPowerOn = value;
	}

	// The rows Low Power overrides (haptics, triggers, light, speaker, microphone).
	public bool CanEditOverridden => !Settings.LowPowerOn;

	// The step of each value, or -1 between the steps (then a muted "Custom · 130%" shows instead of a selection).
	public int HapticsIndex
	{
		get => QuickSteps.IndexOf(QuickSteps.Haptics, Settings.GameHapticsLevel);
		set => Pick(QuickSteps.Haptics, value, v => Settings.GameHapticsLevel = v);
	}

	public int TriggerIndex
	{
		get => QuickSteps.IndexOf(QuickSteps.Trigger, Settings.TriggerStrength);
		set => Pick(QuickSteps.Trigger, value, v => Settings.TriggerStrength = v);
	}

	public int LightIndex
	{
		get => QuickSteps.IndexOf(QuickSteps.Light, Settings.Brightness);
		set => Pick(QuickSteps.Light, value, v => Settings.Brightness = v);
	}

	public string HapticsCustom => HasHapticsCustom ? Text.QuickCustom(Math.Round(Settings.GameHapticsLevel)) : string.Empty;
	public bool HasHapticsCustom => HapticsIndex < 0;
	public string TriggerCustom => HasTriggerCustom ? Text.QuickCustom(Math.Round(Settings.TriggerStrength)) : string.Empty;
	public bool HasTriggerCustom => TriggerIndex < 0;
	public string LightCustom => HasLightCustom ? Text.QuickCustom(Math.Round(Settings.Brightness)) : string.Empty;
	public bool HasLightCustom => LightIndex < 0;

	// The Speaker tile: the one speaker switch (SpeakerEnabled). It can play only with the DualSense type and outside Low
	// Power; otherwise the tile is off and disabled with the reason as its state word, and the saved switch stays as set.
	public bool SpeakerAvailable => !Settings.LowPowerOn && Settings.CurrentPadType == VirtualPadType.DualSense;

	public bool SpeakerOn
	{
		get => Settings.SpeakerEnabled && SpeakerAvailable;
		set
		{
			if (SpeakerAvailable)
			{
				Settings.SpeakerEnabled = value;
			}
		}
	}

	// Low Power first: it also holds the speaker off on a DualSense.
	public string SpeakerUnavailableText => Settings.LowPowerOn ? Text.QuickSpeakerOffLowPower() : Text.QuickSpeakerDualSenseOnly();

	private void OnSpeakerStateChanged()
	{
		OnPropertyChanged(nameof(SpeakerAvailable));
		OnPropertyChanged(nameof(SpeakerOn));
		OnPropertyChanged(nameof(SpeakerUnavailableText));
	}

	// The Microphone tile mirrors the controller's mic button: a click is a press (the value clicked is not used), the tile
	// shows whether the selected microphone is live.
	public bool MicTile
	{
		get => Settings.MicLive;
		set
		{
			Settings.PressMicButton(fromButton: false);
			OnPropertyChanged();
		}
	}

	// True when the controller microphone feature itself is turned off in Settings → Audio (outside Low Power).
	public bool MicFeatureOff => Settings.ControllerMicSelected && !Settings.MicEnabled && !Settings.LowPowerOn;

	public string MicStateText => MicFeatureOff ? Text.QuickMicOffInSettings() : Settings.MicStateText;

	public bool CanPressMic => Settings.CanPressMic;

	// The pickers (shared with the Settings page).
	public AudioDevicesViewModel Audio => Settings.Audio;

	// The sound output picker only while headphones are plugged into the controller (to send game sound there); otherwise
	// it has nothing to do with the controller and stays on Settings > Sound, and the microphone picker takes the whole row.
	public bool ShowsOutputPicker => _service.HeadphonesPlugged;
	public int MicPickerColumn => ShowsOutputPicker ? 1 : 0;
	public int MicPickerSpan => ShowsOutputPicker ? 1 : 2;

	// "Kumanda Türü": the default type, for the games without their own choice (shared with the games pages; its Automatic
	// choice reads "Otomatik (Oyuna Göre)").
	public PadTypeChoiceViewModel PadType => Settings.PadType;

	// "Mikrofon Tuşu": 0 = Sessize Al, 1 = Tamamen Kapat.
	public string MicButtonLabel => Text.MicButtonModeLabel();
	public string MicButtonTip => Text.QuickMicButtonTip();
	public IReadOnlyList<LabelledChoice> MicButtonModes { get; } = [new(() => Text.MicButtonMute()), new(() => Text.MicButtonTurnOffShort())];

	public int MicButtonModeIndex
	{
		get => Settings.MicButtonMode == Kopru.Core.Audio.MicButtonMode.Mute ? 0 : 1;
		set
		{
			if (value is 0 or 1)
			{
				Settings.MicButtonMode = value == 0 ? Kopru.Core.Audio.MicButtonMode.Mute : Kopru.Core.Audio.MicButtonMode.TurnOff;
			}
		}
	}

	[RelayCommand]
	private void Advanced() => OpenAdvanced?.Invoke();

	// Mouse Mode tile: the desktop mode switch (the same one as the Settings group, the tray and PS + Options).
	public string MouseModeLabel => Text.QuickMouseMode();

	public bool MouseModeOn
	{
		get => Settings.DesktopModeOn;
		set => Settings.SetDesktopMode(value, "quick settings");
	}

	// State words under the tile labels; Low Power shows what it does while on.
	public string OnText => Text.QuickStateOn();
	public string OffText => Text.QuickStateOff();
	public string LowPowerState => LowPowerOn ? Text.QuickLowPowerShort() : Text.QuickStateOff();

	// One plain sentence per setting; the ones Low Power overrides say so while it is on.
	public string LowPowerTip => Text.QuickLowPowerTip();
	public string MouseModeTip => Text.QuickMouseModeTip();
	public string SpeakerTip => Overridden(Text.QuickSpeakerTip());
	// Low Power holds the controller microphone off (a selected PC microphone can still be muted).
	public string MicTip => MicFeatureOff
		? Text.QuickMicOffInSettingsTip()
		: Settings.ControllerMicSelected ? Overridden(Text.QuickMicTileTip()) : Text.QuickMicTileTip();
	public string HapticsTip => Overridden(Text.QuickHapticsTip());
	public string TriggerTip => Overridden(Text.QuickTriggerTip());
	public string LightTip => Overridden(Text.QuickLightTip());
	public string ProfileTip => Text.QuickProfileTip();

	private string Overridden(string tip) => Settings.LowPowerOn ? tip + Environment.NewLine + Text.QuickOverriddenByLowPower() : tip;

	// Controller Shortcuts tile: the shortcuts master switch (the same one as the Settings group); its small button opens
	// that group (set by the main window).
	public string ShortcutsLabel => Text.QuickShortcutsShort();
	public string ShortcutsTip => Text.QuickShortcutsTip();
	public string EditShortcutsTip => Text.QuickEditShortcuts();

	public bool ShortcutsOn
	{
		get => Settings.ShortcutsEnabled;
		set => Settings.ShortcutsEnabled = value;
	}

	public Action? OpenShortcuts { get; set; }

	[RelayCommand]
	private void EditShortcuts() => OpenShortcuts?.Invoke();

	private void OnTileSettingChanged(string? name)
	{
		if (name == nameof(SettingsState.DesktopModeOn))
		{
			OnPropertyChanged(nameof(MouseModeOn));
		}
		else if (name == nameof(SettingsState.ShortcutsEnabled))
		{
			OnPropertyChanged(nameof(ShortcutsOn));
		}
		else if (name == nameof(SettingsState.LowPowerOn))
		{
			foreach (string p in new[] { nameof(LowPowerState), nameof(SpeakerTip), nameof(MicTip), nameof(HapticsTip), nameof(TriggerTip), nameof(LightTip) })
			{
				OnPropertyChanged(p);
			}
		}
	}

	// A cleared selection (-1, e.g. while the segments are rebuilt) leaves the value alone.
	private static void Pick(IReadOnlyList<double> steps, int index, Action<double> set)
	{
		if (index >= 0 && index < steps.Count)
		{
			set(steps[index]);
		}
	}
}
