// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Localization;
using Kopru.Core.Setup;
using Kopru.Core.Diagnostics;
using Kopru.Core.Health;

namespace Kopru.App.ViewModels;

public sealed partial class OverviewViewModel : PageViewModel
{
	// How often the lit buttons on the drawing follow the controller while the page is shown and the bridge runs.
	public static readonly TimeSpan InputInterval = TimeSpan.FromMilliseconds(33);

	private readonly IControllerService _service;
	private Func<DateTimeOffset> _now = () => DateTimeOffset.Now;
	private Avalonia.Threading.DispatcherTimer? _inputTimer;
	private Avalonia.Threading.DispatcherTimer? _sampleTimer;
	private bool _syncingProfile;

	public OverviewViewModel(IControllerService service, SettingsState settings)
	{
		_service = service;
		Settings = settings;
		Func<FixAction, Task> fix = service.ExecuteFix;
		HealthChips = [.. Enum.GetValues<HealthChipKind>().Select(k => new HealthChipViewModel(k, fix))];
		Live = new LivePanelViewModel(service);
		Live.OnStateChanged();
		Quick = new QuickSettingsViewModel(service, settings);
		_service.StateChanged += (_, _) => RunOnUi(() =>
		{
			OnPropertyChanged(string.Empty);
			UpdateHealth();
			Live.OnStateChanged();
		});
		settings.ProfilesChanged += () => RunOnUi(SyncProfiles);
		settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(SettingsState.LightColor) or nameof(SettingsState.LightIntensity) or nameof(SettingsState.LowPowerOn))
			{
				OnPropertyChanged(nameof(ArtLightIntensity));
				OnPropertyChanged(nameof(ArtPlayerLeds));
			}
		};
		SyncProfiles();
		UpdateHealth();
	}

	// The clock for the last-known battery's age (tests pass a fixed one).
	public void Attach(Func<DateTimeOffset>? now = null)
	{
		_now = now ?? _now;
		UpdateHealth();
		OnPropertyChanged(string.Empty);
	}

	public SettingsState Settings { get; }

	// The live card's contents (graph history, level bars).
	public LivePanelViewModel Live { get; }

	// The quick settings card (Low Power, haptics, triggers, light, profile, speaker, microphone).
	public QuickSettingsViewModel Quick { get; }
	public override string Title => Text.NavOverview();

	// State: no controller, connected with the bridge off, bridge waiting for a game, a game connected.
	public bool IsConnected => _service.IsConnected;
	public bool IsBridgeRunning => _service.IsBridgeRunning;
	public bool IsStopped => !_service.IsBridgeRunning;
	public bool IsWaiting => _service.IsBridgeRunning && !_service.IsGameConnected;
	public bool IsRunning => _service.IsBridgeRunning && _service.IsGameConnected;
	public bool IsGameConnected => _service.IsGameConnected;
	public bool IsEdge => _service.Kind == ControllerKind.DualSenseEdge;
	public string Warning => _service.Warning;
	public bool HasWarning => !string.IsNullOrEmpty(_service.Warning);
	public string BridgeButtonText => _service.IsBridgeRunning ? Text.BridgeStop() : Text.BridgeStart();

	// The foreground game's name once one was seen.
	public bool HasGame => _service.Games.State.Name != null;
	public string GameValue => _service.Games.State.Name
		?? (_service.IsGameConnected ? Text.ValueGameConnected() : Text.ValueNoGame());

	// The controller sub-state from the health model; a live controller wins (the model refreshes every few seconds).
	public ConnectionState Connection => _service.IsConnected || _service.Connection.Kind != ConnectionKind.ConnectedBluetooth
		? _service.Connection : new ConnectionState(ConnectionKind.PairedNotConnected, _service.Connection.IsEdge);
	private BlockingIssue? Blocking => _service.Health.BlockingIssue;
	private bool HasProblemChip => _service.Health.Chips.Any(c => c.Level == HealthLevel.Error);

	// On a cable the bridge has nothing to do: shown like "no controller" with the explanation.
	public bool IsOnCable => _service.Connection.Kind == ConnectionKind.ConnectedUsb;

	// Header: one plain status line under the title ("Running · Cyberpunk 2077").
	public string StatusLine => IsOnCable ? Connection.Label()
		: !_service.IsConnected
		? (IsBridgeRunning && Blocking == null ? Text.OverviewWaitingController() : Connection.Label())
		: HasWarning || HasProblemChip ? Text.OverallAttention()
		: IsRunning && HasGame ? Text.OverviewPlaying(GameValue)
		: IsRunning ? Text.BridgeStateRunning()
		: IsWaiting ? Text.BridgeStateWaiting()
		: Text.OverviewReady();

	// Header: the one action that matters now. A blocking issue with a fix (install the driver, turn Bluetooth on, pair)
	// replaces the bridge button; otherwise the bridge, disabled with the reason when it cannot start. Accent unless it
	// stops the bridge.
	private FixAction PrimaryFix => _service.IsBridgeRunning ? FixAction.None : Blocking?.Fix ?? FixAction.None;
	public string PrimaryText => PrimaryFix != FixAction.None ? HealthFixes.Label(PrimaryFix) : BridgeButtonText;
	public bool IsPrimaryQuiet => PrimaryFix == FixAction.None && _service.IsBridgeRunning;
	public bool CanPrimary => PrimaryFix != FixAction.None || CanToggleBridge;
	// The reason the bridge cannot start (null: no tooltip).
	public string? PrimaryTip => !_service.IsBridgeRunning && Blocking is { } b ? b.Reason : null;

	// Stopping is always possible; starting only when nothing blocks it. Never while a start or stop is under way.
	public bool CanToggleBridge => !_bridgeBusy && (_service.IsBridgeRunning || _service.CanStartBridge);

	// A start or stop from ToggleBridge is under way.
	private bool _bridgeBusy;

	// Sidebar controller card: the virtual controller's name with a short state below it ("Kapalı", "Bağlanıyor", "Açık",
	// "Hata"); the tooltip says what is going on or why it cannot start.
	private HealthChip BridgeHealth => _service.Health.Chip(HealthChipKind.Bridge);
	private bool IsBridgeProblem => BridgeHealth.Level == HealthLevel.Error;
	private bool IsBridgeStarting => _bridgeBusy && _service.IsBridgeRunning;
	public string VirtualLabel => Text.RowVirtualPad();
	public string VirtualStateText =>
		IsBridgeProblem ? Text.HealthError()
		: IsBridgeStarting ? Text.BridgeConnecting()
		: _service.IsBridgeRunning ? Text.HealthOn()
		: Text.HealthOff();
	public bool IsVirtualStateOn => _service.IsBridgeRunning && !IsBridgeProblem && !IsBridgeStarting;
	public bool IsVirtualStateProblem => IsBridgeProblem;
	public string VirtualStatusTip =>
		IsBridgeStarting ? Text.HealthBridgeConnecting()
		: _service.IsBridgeRunning && !_service.IsConnected ? Text.HealthBridgeWaiting()
		: BridgeHealth.Level is HealthLevel.Error or HealthLevel.Warning ? BridgeHealth.Reason
		: !_service.IsBridgeRunning && !_service.CanStartBridge ? _service.CanStartBridgeReason
		: _service.IsBridgeRunning ? Text.BridgeStatusRunning()
		: Text.BridgeStatusStopped();

	[RelayCommand]
	private async Task Primary()
	{
		if (PrimaryFix != FixAction.None)
		{
			await _service.ExecuteFix(PrimaryFix);
		}
		else if (CanToggleBridge)
		{
			await ToggleBridge();
		}
	}

	// Hero: the controller drawing, faded with its light off without a controller; lit buttons while the bridge runs.
	public double ArtOpacity => _service.IsConnected ? 1 : 0.4;
	// Low Power darkens the light bar and the player LEDs on the drawing too.
	public double ArtLightIntensity => _service.IsConnected && !Settings.LowPowerOn ? Settings.LightIntensity : 0;
	public bool ArtPlayerLeds => _service.IsConnected && !Settings.LowPowerOn;

	[ObservableProperty]
	private Kopru.Core.Input.PadButtons _livePressed;

	// Hero text: the game while one runs, else the controller, else why there is none and what to do.
	public bool ShowsGame => _service.IsConnected && _service.IsBridgeRunning && HasGame;
	public bool ShowsDevice => _service.IsConnected && !IsOnCable && !ShowsGame;
	public bool ShowsNoController => !_service.IsConnected || IsOnCable;
	public string GameCaption => Text.HeroGame();
	public string DeviceCaption => Text.CardController();
	public string DeviceName => _service.IsConnected ? _service.DeviceName : Text.DeviceNoController();
	public string NoControllerTitle => Connection.Label();
	public string NoControllerReason => Connection.Reason();
	// What to press for this state (pair, turn Bluetooth on); none when the reason says it all.
	private FixAction HeroFix => Connection.Kind switch
	{
		ConnectionKind.NeverPaired => FixAction.PairController,
		ConnectionKind.BluetoothOff => FixAction.OpenBluetoothSettings,
		_ => FixAction.None
	};
	public bool HasHeroAction => ShowsNoController && HeroFix != FixAction.None && HeroFix != PrimaryFix;
	public string HeroActionText => HealthFixes.Label(HeroFix);

	[RelayCommand]
	private Task HeroAction() => HeroFix == FixAction.None ? Task.CompletedTask : _service.ExecuteFix(HeroFix);

	// Battery: the live reading is in the sidebar; without a controller the hero shows the last one seen
	// ("Last: 62% · 2 h ago").
	public bool HasBattery => _service.IsConnected && _service.HasBattery;
	// The live reading in the hero, next to the name: the same values the sidebar card shows ("%78 · Charging").
	public bool ShowsBattery => HasBattery && !ShowsNoController;
	public double BatteryPercent => _service.BatteryPercent;
	public bool IsCharging => _service.IsCharging;
	public string BatteryText => !HasBattery ? string.Empty
		: _service.IsCharging ? Text.BatteryValue(Math.Round(_service.BatteryPercent)) + " · " + Text.Charging()
		: Text.BatteryValue(Math.Round(_service.BatteryPercent));
	// Below 20 % while not charging: the number turns amber (the glyph colours itself).
	public bool IsBatteryLow => HasBattery && !_service.IsCharging && _service.BatteryPercent < NotificationCenter.LowBatteryPercent;
	public string BatteryTip => _service.IsCharging ? Text.HeroBatteryChargingTip() : Text.HeroBatteryTip();
	public string LastBatteryText => !HasBattery && _service.LastKnownBattery is { } b ? b.Describe(_now()) : string.Empty;
	public bool HasLastBattery => LastBatteryText.Length > 0;
	public bool ShowsLastBatteryInHero => ShowsNoController && HasLastBattery;

	// DualSense supported mode for the current game (off: the other games profile).
	public string SupportedModeLabel => Text.SupportedMode();
	public string SupportedModeHint => Text.SupportedModeHint();
	public string SupportedModeHelper => Text.SupportedModeHelper();
	public string SupportedModeTip => SupportedModeLabel + Environment.NewLine + SupportedModeHint + Environment.NewLine + GameDetail;
	public bool IsSupportedMode => _service.Games.State.Mode == Kopru.Core.Games.GameMode.Supported;
	public string ModeValue => IsSupportedMode ? Text.ModeShortSupported() : Text.ModeShortClassic();
	public string GameDetail => GameModeText.Line(_service.Games.State);
	public bool IsSupportedModeOn
	{
		get => _service.Games.IsSupportedModeOn;
		set
		{
			_service.Games.SetSupportedMode(value);
			OnPropertyChanged();
		}
	}

	// Profile picker.
	public string ProfileLabel => Text.ProfileLabel();
	public ObservableCollection<ProfileOption> Profiles { get; } = [];

	[ObservableProperty]
	private ProfileOption? _selectedProfile;

	partial void OnSelectedProfileChanged(ProfileOption? value)
	{
		if (!_syncingProfile && value != null && value.Id != Settings.Profiles.Active.Id)
		{
			Settings.ActivateProfile(value.Id);
		}
	}

	private void SyncProfiles()
	{
		_syncingProfile = true;
		try
		{
			Profiles.Clear();
			foreach (var p in Settings.Profiles.Profiles)
			{
				Profiles.Add(new ProfileOption(p.Id, SettingsState.DisplayName(p)));
			}
			SelectedProfile = Profiles.FirstOrDefault(o => o.Id == Settings.Profiles.Active.Id);
		}
		finally
		{
			_syncingProfile = false;
		}
	}

	// Live panel: input rate, link delay and haptics output while the bridge runs with a controller.
	public bool IsLive => _service.IsConnected && _service.IsBridgeRunning;
	public bool IsNotLive => !IsLive;
	public string LiveTitle => Text.LiveTitle();
	public string LiveIdle => Text.LiveIdle();
	public string StatRateLabel => Text.StatInputRate();
	public string StatLatencyLabel => Text.StatLatency();
	public string RateValue => Text.InputRate(Math.Round(_service.InputRateHz));
	public bool HasLatency => _service.Latency != null;
	public string LatencyValue => _service.Latency is { } r ? Text.LatencyShort(r.LinkP50Ms, r.LinkP99Ms) : Text.HealthUnknown();
	public string LatencyTip => _service.Latency is { } r
		? Text.LatencyValue(r.LinkP50Ms, r.LinkP99Ms, r.RelayMs) + Environment.NewLine + Text.LatencyHint()
		: Text.LatencyHint();
	// Worst-case delay: green under 10 ms, amber under 20 ms, red above.
	public bool IsLatencyGood => _service.Latency is { LinkP99Ms: < 10 };
	public bool IsLatencyFair => _service.Latency is { LinkP99Ms: >= 10 and < 20 };
	public bool IsLatencyPoor => _service.Latency is { LinkP99Ms: >= 20 };
	public string HapticsLabel => Text.CardHaptics();
	public string LeftShort => Text.LeftShort();
	public string RightShort => Text.RightShort();
	public double HapticsLeft => _service.HapticsLeftLevel * 100;
	public double HapticsRight => _service.HapticsRightLevel * 100;

	// Health strip: the model's five chips, shown one by one only when one needs attention; otherwise one quiet line.
	public IReadOnlyList<HealthChipViewModel> HealthChips { get; }
	public bool IsHealthAllGood => HealthChips.All(c => !c.IsAttention && !c.IsProblem);
	public bool ShowsHealthChips => !IsHealthAllGood;
	public string HealthSummary => Text.HealthAllGood(string.Join(", ", HealthChips.Select(c => c.Name.Trim()).Where(n => n.Length > 0)));

	public HealthChipViewModel Chip(HealthChipKind kind) => HealthChips.First(c => c.Kind == kind);

	// Steam Input warning (only when it may take the controller).
	public bool HasSteamWarning => SteamInputText.IsWarning(_service.SteamInput);
	public string SteamWarning => HasSteamWarning ? _service.SteamInput!.Advice : string.Empty;

	private void UpdateHealth()
	{
		foreach (Kopru.Core.Health.HealthChip chip in _service.Health.Chips)
		{
			HealthChips.FirstOrDefault(c => c.Kind == chip.Kind)?.Set(chip);
		}
		OnPropertyChanged(nameof(IsHealthAllGood));
		OnPropertyChanged(nameof(ShowsHealthChips));
		OnPropertyChanged(nameof(HealthSummary));
	}

	// Shown or left: while shown, the drawing follows the controller's buttons (when the bridge runs).
	public void SetVisible(bool visible)
	{
		if (visible && _inputTimer == null)
		{
			_inputTimer = new Avalonia.Threading.DispatcherTimer(InputInterval, Avalonia.Threading.DispatcherPriority.Background, (_, _) => ReadInput());
			_inputTimer.Start();
			// The graph covers the time the page is shown: one sample a second.
			_sampleTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(1), Avalonia.Threading.DispatcherPriority.Background, (_, _) => Live.Sample());
			_sampleTimer.Start();
			UpdateHealth();
		}
		else if (!visible && _inputTimer != null)
		{
			_inputTimer.Stop();
			_inputTimer = null;
			_sampleTimer?.Stop();
			_sampleTimer = null;
			LivePressed = Kopru.Core.Input.PadButtons.None;
		}
	}

	private void ReadInput()
	{
		Live.Follow();
		byte[]? raw = _service.IsBridgeRunning ? _service.RawInput : null;
		LivePressed = raw is { Length: >= 9 }
			? Kopru.Core.Input.InputSnapshot.DecodeButtons(raw[7], raw[8], raw.Length > 9 ? raw[9] : (byte)0)
			: Kopru.Core.Input.PadButtons.None;
	}

	// Turn off controller (only offered with a controller).
	public string PowerOffText => Text.TurnOffController();
	public bool CanPowerOff => _service.IsConnected && !IsPoweringOff;
	public bool HasPowerOffResult => !string.IsNullOrEmpty(PowerOffResult);

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CanPowerOff))]
	private bool _isPoweringOff;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasPowerOffResult))]
	private string _powerOffResult = string.Empty;

	[RelayCommand]
	private async Task PowerOff()
	{
		if (!CanPowerOff)
		{
			return;
		}
		IsPoweringOff = true;
		PowerOffResult = Text.PowerOffRunning();
		try
		{
			PowerOffResult = await _service.PowerOffAsync() ? Text.PowerOffDone() : Text.PowerOffFailed();
		}
		finally
		{
			IsPoweringOff = false;
		}
	}

	[RelayCommand]
	private async Task ToggleBridge()
	{
		// While a start or stop is under way a second press (the Overview button, the primary action) does nothing and the
		// sidebar card says it is connecting.
		if (_bridgeBusy)
		{
			return;
		}
		_bridgeBusy = true;
		OnPropertyChanged(string.Empty);
		try
		{
			if (_service.IsBridgeRunning)
			{
				await _service.StopBridgeAsync();
			}
			else
			{
				await _service.StartBridgeAsync();
			}
		}
		finally
		{
			_bridgeBusy = false;
			RunOnUi(() => OnPropertyChanged(string.Empty));
		}
	}
}

public sealed class TriggerEffectItem(TriggerTestEffect effect, Func<string> name, Func<string> hint)
{
	public TriggerTestEffect Effect { get; } = effect;
	public string Name => name();
	public string Hint => hint();
}

// Trigger effects to try and the trigger strength: the Triggers tab of the controller page.
public sealed partial class TriggersViewModel : ViewModelBase
{
	private readonly IControllerService _service;

	public TriggersViewModel(IControllerService service, SettingsState settings)
	{
		_service = service;
		Settings = settings;
		Effects =
		[
			new(TriggerTestEffect.Off, () => Text.EffectOff(), () => Text.EffectOffHint()),
			new(TriggerTestEffect.Feedback, () => Text.EffectFeedback(), () => Text.EffectFeedbackHint()),
			new(TriggerTestEffect.Weapon, () => Text.EffectWeapon(), () => Text.EffectWeaponHint()),
			new(TriggerTestEffect.Vibration, () => Text.EffectVibration(), () => Text.EffectVibrationHint()),
		];
		_selectedEffect = Effects[0];
	}

	public SettingsState Settings { get; }
	public IReadOnlyList<TriggerEffectItem> Effects { get; private set; }
	public string Intro => Text.TriggerEffectsHint();
	public string StrengthLabel => Text.TriggerStrength();
	public string StrengthHint => Text.TriggerStrengthHint();
	public string EffectsLabel => Text.TesterTriggerEffects();

	[ObservableProperty]
	private TriggerTestEffect _activeEffect = TriggerTestEffect.Off;

	// The effect card selected in the picker; selecting one plays it on the controller.
	[ObservableProperty]
	private TriggerEffectItem _selectedEffect;

	partial void OnSelectedEffectChanged(TriggerEffectItem value)
	{
		if (value != null && value.Effect != ActiveEffect)
		{
			TestEffect(value.Effect);
		}
	}

	protected override void OnLanguageChanged()
	{
		// Item templates do not observe the items, so hand the list out again to rebuild them with new names.
		TriggerEffectItem selected = SelectedEffect;
		Effects = Effects.ToList();
		base.OnLanguageChanged();
		SelectedEffect = selected;
	}

	[RelayCommand]
	private void TestEffect(TriggerTestEffect effect)
	{
		ActiveEffect = effect;
		SelectedEffect = Effects.First(e => e.Effect == effect);
		_service.TestTrigger(effect);
	}
}

public sealed class ColourPreset(uint rgb, Func<string> name)
{
	public uint Rgb { get; } = rgb;
	public string Name => name();
	public IBrush Brush { get; } = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
}

public sealed partial class LightViewModel : PageViewModel
{
	private readonly IControllerService _service;

	public LightViewModel(IControllerService service, SettingsState settings)
	{
		_service = service;
		Settings = settings;
		Presets =
		[
			new(0x2F6BFF, () => Text.ColourBlue()),
			new(0xFF3B3B, () => Text.ColourRed()),
			new(0x2FD27A, () => Text.ColourGreen()),
			new(0x9B5CFF, () => Text.ColourPurple()),
			new(0xFF8A2F, () => Text.ColourOrange()),
			new(0xF2F2F2, () => Text.ColourWhite()),
		];
		_selectedPreset = Presets.FirstOrDefault(p => p.Rgb == settings.LightbarColour) ?? Presets[0];
		InitEffects();
	}

	public SettingsState Settings { get; }
	public IReadOnlyList<ColourPreset> Presets { get; private set; }
	public override string Title => Text.NavLight();
	public string Intro => Text.LightIntro();
	public string ColourLabel => Text.LightbarColour();
	public string BrightnessLabel => Text.Brightness();
	public string PlayerLedsLabel => Text.PlayerLeds();
	public string PlayerLedsHint => Text.PlayerLedsHint();
	public string SelectedName => SelectedPreset.Name;
	public bool IsEdge => _service.Kind == ControllerKind.DualSenseEdge;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SelectedName))]
	private ColourPreset _selectedPreset;

	partial void OnSelectedPresetChanged(ColourPreset value)
	{
		Settings.LightbarColour = value.Rgb;
		_service.TestLight(value.Rgb);
	}

	protected override void OnLanguageChanged()
	{
		ColourPreset selected = SelectedPreset;
		Presets = Presets.ToList();
		RefreshChoices();
		base.OnLanguageChanged();
		SelectedPreset = selected;
	}
}

public sealed class LanguageOption(Language? language, Func<string> name)
{
	public Language? Language { get; } = language;
	public string Name => name();
}

public sealed partial class SettingsViewModel : PageViewModel
{
	private readonly IControllerService _service;
	private readonly Action? _runWizard;

	public SettingsViewModel(IControllerService service, SettingsState settings, Action? runWizard = null)
	{
		_service = service;
		_runWizard = runWizard;
		Settings = settings;
		Info = new ControllerInfoViewModel(service);
		// Language names are shown in their own language (endonyms) on purpose.
		Languages = BuildLanguages();
		_selectedLanguage = Languages.First(o => o.Language == Localizer.Chosen);
		_service.StateChanged += (_, _) => RunOnUi(() => OnPropertyChanged(nameof(HeadphonesPlugged)));
		InitMic();
	}

	public SettingsState Settings { get; }
	public ObservableCollection<LanguageOption> Languages { get; private set; }
	public override string Title => Text.NavSettings();
	public string GeneralLabel => Text.General();
	public string LanguageLabel => Text.LanguageLabel();
	public string StartWithWindowsLabel => Text.StartWithWindows();
	public string StartWithWindowsHint => Text.StartWithWindowsHint();
	public string IdlePowerOffLabel => Text.IdlePowerOff();
	public string IdlePowerOffHint => Text.IdlePowerOffHint();
	public string ToolsLabel => Text.Tools();
	public string AboutLabel => Text.About();
	public string AppName => AppInfo.Name;
	public string VersionText => Text.AboutVersion(AppInfo.Version);
	public string AboutTagline => Text.AboutTagline();
	// The compact controller line under the version.
	public ControllerInfoViewModel Info { get; }
	public string RunWizardText => Text.RunSetupWizard();
	public string TestAdapterText => Text.TestBluetoothAdapter();
	public string ExportText => Text.ExportDiagnostics();
	public string LanguageHint => Text.LanguageHint();
	public string RunWizardHint => Text.RunSetupWizardHint();
	public string TestAdapterHint => Text.TestBluetoothAdapterHint();
	public string ExportHint => Text.ExportDiagnosticsHint();
	public string RunActionText => Text.ActionRun();
	public string TestActionText => Text.ActionTest();
	public string ExportActionText => Text.ActionExport();
	public string PowerOffWhenIdleLabel => Text.PowerOffWhenIdleSetting();
	public string PowerOffWhenIdleHint => Text.PowerOffWhenIdleHint();
	public string AutoLowPowerLabel => Text.AutoLowPower();
	public string AutoLowPowerHint => Text.AutoLowPowerHint();
	public string KeepInTrayLabel => Text.KeepInTray();
	public string KeepInTrayHint => Text.KeepInTrayHint();
	public string NotificationsLabel => Text.GroupNotifications();
	public string NotificationsShowLabel => Text.NotificationsShow();
	public string NotificationsShowHint => Text.NotificationsShowHint();
	public string QuietWhilePlayingLabel => Text.QuietWhilePlaying();
	public string QuietWhilePlayingHint => Text.QuietWhilePlayingHint();
	public string NotifyConnectionLabel => Text.NotifyConnectionSetting();
	public string NotifyConnectionHint => Text.NotifyConnectionHint();
	public string NotifyBatteryLabel => Text.NotifyBatterySetting();
	public string NotifyBatteryHint => Text.NotifyBatteryHint();
	public string NotifyGameLabel => Text.NotifyGameSetting();
	public string NotifyGameHint => Text.NotifyGameHint();
	public string NotifySteamInputLabel => Text.NotifySteamInputSetting();
	public string NotifySteamInputHint => Text.NotifySteamInputHint();
	public string NotifyPowerOffLabel => Text.NotifyPowerOffSetting();
	public string NotifyPowerOffHint => Text.NotifyPowerOffHint();
	public string NotifyErrorsLabel => Text.NotifyErrorsSetting();
	public string NotifyErrorsHint => Text.NotifyErrorsHint();
	public string NotifyHidHideLabel => Text.NotifyHidHideSetting();
	public string NotifyHidHideHint => Text.NotifyHidHideHint();
	public string SpeakerGroupLabel => Text.GroupControllerSpeaker();
	public string SpeakerSendLabel => Text.SpeakerSendGameAudio();
	public string SpeakerSendHint => Text.SpeakerSendGameAudioHint();
	public string SpeakerVolumeLabel => Text.SpeakerVolume();
	public string HeadphoneVolumeLabel => Text.HeadphoneVolume();
	public string HeadphonesPluggedText => Text.HeadphonesPlugged();

	// Headphones detected in the controller's jack (shown under the speaker volumes).
	public bool HeadphonesPlugged => _service.HeadphonesPlugged;

	// Asks where to save a file (suggested name in, local path out; null when cancelled). Set by the view.
	public Func<string, Task<string?>>? PickSavePath { get; set; }

	public static string DiagnosticsFileName(DateTime date) =>
		"ds5-untethered-diagnostics-" + date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".zip";
	public bool HasToolResult => !string.IsNullOrEmpty(ToolResult);

	[ObservableProperty]
	private LanguageOption _selectedLanguage;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasToolResult))]
	private string _toolResult = string.Empty;

	private static ObservableCollection<LanguageOption> BuildLanguages() =>
	[
		new(null, () => Text.LanguageFollowSystem()),
		new(Language.Turkish, () => "Türkçe"),
		new(Language.English, () => "English"),
	];

	partial void OnSelectedLanguageChanged(LanguageOption value)
	{
		if (value != null)
		{
			Localizer.Chosen = value.Language;
		}
	}

	protected override void OnLanguageChanged()
	{
		// Refresh the "follow system" entry name without re-triggering the language choice.
		Language? chosen = Localizer.Chosen;
		var fresh = BuildLanguages();
		Languages = fresh;
#pragma warning disable MVVMTK0034 // set the backing field to avoid a feedback loop
		_selectedLanguage = fresh.First(o => o.Language == chosen);
#pragma warning restore MVVMTK0034
		base.OnLanguageChanged();
	}

	[RelayCommand]
	private void RunWizard()
	{
		if (_runWizard != null)
		{
			_runWizard();
		}
		else
		{
			ToolResult = Text.ComingSoon();
		}
	}

	[RelayCommand]
	private async Task TestAdapter()
	{
		ToolResult = Text.AdapterTestRunning();
		ToolResult = (await _service.TestAdapterAsync()).Text;
	}

	[RelayCommand]
	private async Task Export()
	{
		string? path = PickSavePath == null ? null : await PickSavePath(DiagnosticsFileName(DateTime.Now));
		if (string.IsNullOrEmpty(path))
		{
			return;
		}
		ToolResult = Text.DiagnosticsRunning();
		try
		{
			await _service.ExportDiagnosticsAsync(path);
			ToolResult = Text.DiagnosticsSaved(path);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
		{
			ToolResult = Text.DiagnosticsFailed(e.Message);
		}
	}
}

// When the Steam Input check is worth showing.
public static class SteamInputText
{
	// Steam Input may take the controller: a warning on Overview and on the supported games page.
	public static bool IsWarning(Kopru.Core.Steam.SteamInputResult? result) =>
		result?.Status is Kopru.Core.Steam.SteamInputStatus.OnGlobally or Kopru.Core.Steam.SteamInputStatus.OnForApp;

	// Could not be read: a muted note on the supported games page only.
	public static bool IsUnknown(Kopru.Core.Steam.SteamInputResult? result) =>
		result?.Status == Kopru.Core.Steam.SteamInputStatus.Unknown;
}
