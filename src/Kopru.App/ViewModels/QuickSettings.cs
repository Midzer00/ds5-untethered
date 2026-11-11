// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// The Overview's quick settings card: Low Power, DualSense and Mouse Mode, speaker and microphone, controller shortcuts,
// the controller type, three-step haptics / trigger / light choices and the profile.
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
				case nameof(SettingsState.SpeakerActive):
					OnPropertyChanged(nameof(SpeakerOn));
					break;
				case nameof(SettingsState.MicLive):
					OnPropertyChanged(nameof(MicTile));
					break;
				case nameof(SettingsState.MicStateText):
				case nameof(SettingsState.CanPressMic):
				case nameof(SettingsState.MicButtonMode):
					OnPropertyChanged(nameof(MicStateText));
					OnPropertyChanged(nameof(CanPressMic));
					OnPropertyChanged(nameof(MicButtonModeIndex));
					break;
			}
		};
		service.StateChanged += (_, _) => RunOnUi(() => OnPropertyChanged(nameof(ShowsDisconnectedNote)));
		settings.PropertyChanged += (_, e) => OnTileSettingChanged(e.PropertyName);
	}

	public SettingsState Settings { get; }

	// Opens the Settings page (set by the main window).
	public Action? OpenAdvanced { get; set; }

	public string Title => Text.QuickSettingsTitle();
	public string LowPowerLabel => Text.LowPower();
	public string LowPowerNote => Text.LowPowerNote();
	public string HapticsLabel => Text.HapticsStrength();
	public string TriggerLabel => Text.TriggerStrength();
	public string LightLabel => Text.QuickLight();
	public string SpeakerLabel => Text.LiveSpeaker();
	public string MicLabel => Text.QuickMicrophone();
	public string AdvancedText => Text.AdvancedSettings();
	public string DisconnectedNote => Text.AppliedWhenConnected();

	// Step names: a new list only when the language changed (a language switch rebuilds the segments). The same list
	// otherwise, so a re-read of the bindings (every StateChanged refreshes the Overview) never resets the selection.
	public IReadOnlyList<string> StrengthSteps => _strengthSteps = Same(_strengthSteps, [Text.StepLight(), Text.StepNormal(), Text.StepStrong()]);
	public IReadOnlyList<string> LightSteps => _lightSteps = Same(_lightSteps, [Text.StepOff(), Text.StepLow(), Text.StepHigh()]);

	private IReadOnlyList<string>? _strengthSteps;
	private IReadOnlyList<string>? _lightSteps;

	private static IReadOnlyList<string> Same(IReadOnlyList<string>? cached, IReadOnlyList<string> now) =>
		cached != null && cached.SequenceEqual(now) ? cached : now;

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

	// On with DualSense Mode (see SettingsState.SpeakerActive).
	public bool SpeakerOn
	{
		get => Settings.SpeakerActive;
		set => Settings.SpeakerActive = value;
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

	public string MicStateText => Settings.MicStateText;

	public bool CanPressMic => Settings.CanPressMic;

	// The pickers (shared with the Settings page).
	public AudioDevicesViewModel Audio => Settings.Audio;

	// "Kumanda Türü": Automatic or one controller type for every game (shared with the Supported Games page).
	public PadTypeChoiceViewModel PadType => Settings.PadType;

	// "Mikrofon Tuşu": 0 = Sessize Al, 1 = Tamamen Kapat.
	public string MicButtonLabel => Text.MicButtonModeLabel();
	public string MicButtonTip => Text.QuickMicButtonTip();
	public IReadOnlyList<string> MicButtonModes => _micButtonModes = Same(_micButtonModes, [Text.MicButtonMute(), Text.MicButtonTurnOffShort()]);

	private IReadOnlyList<string>? _micButtonModes;

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
		set => Settings.DesktopModeOn = value;
	}

	// DualSense Mode tile label (the tile binds the Overview's own switch, so it stays in sync with the hero and the sidebar).
	public string SupportedModeLabel => Text.SupportedModeShort();

	// State words under the tile labels; Low Power shows what it does while on.
	public string OnText => Text.QuickStateOn();
	public string OffText => Text.QuickStateOff();
	public string LowPowerState => LowPowerOn ? Text.QuickLowPowerShort() : Text.QuickStateOff();

	// One plain sentence per setting; the ones Low Power overrides say so while it is on.
	public string LowPowerTip => Text.QuickLowPowerTip();
	public string SupportedModeTip => Text.QuickSupportedModeTip();
	public string MouseModeTip => Text.QuickMouseModeTip();
	public string SpeakerTip => Overridden(Text.QuickSpeakerTip());
	// Low Power holds the controller microphone off (a selected PC microphone can still be muted).
	public string MicTip => Settings.ControllerMicSelected ? Overridden(Text.QuickMicTileTip()) : Text.QuickMicTileTip();
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
