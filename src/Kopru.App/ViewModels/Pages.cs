// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

public sealed partial class OverviewViewModel : PageViewModel
{
	private readonly IControllerService _service;

	public OverviewViewModel(IControllerService service, SettingsState settings)
	{
		_service = service;
		Settings = settings;
		_service.StateChanged += (_, _) => RunOnUi(() => OnPropertyChanged(string.Empty));
	}

	public SettingsState Settings { get; }
	public override string Title => Text.NavOverview();
	public string Subtitle => Text.OverviewSubtitle();

	public string ConnectionLabel => Text.CardConnection();
	public string ConnectionValue => _service.IsConnected ? Text.ConnectionBluetooth() : Text.DeviceNotConnected();
	public string ConnectionDetail => _service.IsConnected ? _service.DeviceName + " · Bluetooth" : string.Empty;
	public bool IsConnected => _service.IsConnected;

	public string LatencyLabel => Text.CardLatency();
	// Measured link delay, median / worst 1 % ("1.2 / 3.4 ms"), or a dash until measured. Unit symbol only; the numbers
	// use the UI language's culture.
	public string LatencyValue => _service.Latency is { } r
		? string.Create(Localizer.Culture(Localizer.Current), $"{r.LinkP50Ms:F1} / {r.LinkP99Ms:F1} ms")
		: "—";
	public string LatencyTip => _service.Latency is { } r
		? Text.LatencyValue(r.LinkP50Ms, r.LinkP99Ms, r.RelayMs) + Environment.NewLine + Text.LatencyHint()
		: Text.LatencyHint();
	public string RateValue => Text.InputRate(Math.Round(_service.InputRateHz));

	public string BatteryLabel => Text.CardBattery();
	public string BatteryValue => Text.BatteryValue(Math.Round(_service.BatteryPercent));
	public string BatteryDetail => _service.IsCharging ? Text.Charging() : Text.OnBattery();
	public double BatteryPercent => _service.BatteryPercent;

	public string HapticsLabel => Text.CardHaptics();
	public string HapticsValue => _service.IsHapticsActive ? Text.HapticsActive() : Text.HapticsIdle();
	public string HapticsDetail => Text.HapticsFromGame();

	public bool IsBridgeRunning => _service.IsBridgeRunning;
	public string Warning => _service.Warning;
	public bool HasWarning => !string.IsNullOrEmpty(_service.Warning);
	public bool IsEdge => _service.Kind == ControllerKind.DualSenseEdge;
	public bool IsStopped => !_service.IsBridgeRunning;
	public bool IsWaiting => _service.IsBridgeRunning && !_service.IsGameConnected;
	public bool IsRunning => _service.IsBridgeRunning && _service.IsGameConnected;
	public string BridgeStateText => IsStopped ? Text.BridgeStateStopped() : IsWaiting ? Text.BridgeStateWaiting() : Text.BridgeStateRunning();
	public string BridgeHeadline => IsStopped ? Text.BridgeHeadlineStopped() : IsWaiting ? Text.BridgeHeadlineWaiting() : Text.BridgeHeadlineRunning();
	public string BridgeDetail => IsStopped ? Text.OverviewSubtitle() : IsWaiting ? Text.BridgeDetailWaiting() : Text.BridgeDetailRunning();
	public string StatLatencyLabel => Text.StatLatency();
	public string StatRateLabel => Text.StatInputRate();
	public string BridgeButtonText => _service.IsBridgeRunning ? Text.BridgeStop() : Text.BridgeStart();
	public string BridgeStatus => _service.IsBridgeRunning ? Text.BridgeStatusRunning() : Text.BridgeStatusStopped();

	public string QuickTestsLabel => Text.QuickTests();
	public string QuickSettingsLabel => Text.QuickSettings();
	public string HapticsLeftText => Text.TestHapticsLeft();
	public string HapticsRightText => Text.TestHapticsRight();
	public string TriggerText => Text.TestTrigger();
	public string LightText => Text.TestLight();
	public string GameHapticsLabel => Text.GameHapticsLevel();
	public string RumbleLabel => Text.RumbleLevel();

	// Dashboard: header and overall state (real state only: link, bridge, warnings).
	public string Intro => Text.OverviewIntro();
	public bool IsOverallOk => _service.IsConnected && !HasWarning && IsRunning;
	public bool IsOverallAttention => !_service.IsConnected || HasWarning;
	public string OverallText => !_service.IsConnected ? Text.OverallNoController()
		: HasWarning ? Text.OverallAttention()
		: IsRunning ? Text.OverallOk()
		: BridgeStateText;

	// Status cards
	public string CardConnectionLabel => Text.CardConnection();
	public string CardBridgeLabel => Text.CardBridge();
	public string CardControllerLabel => Text.CardController();
	public string RowLinkLabel => Text.RowLink();
	public string RowStatusLabel => Text.RowStatus();
	public string RowGameLabel => Text.RowGame();
	public string RowVirtualPadLabel => Text.RowVirtualPad();
	public string RowModelLabel => Text.RowModel();
	public string RowPowerLabel => Text.RowPower();
	public bool IsGameConnected => _service.IsGameConnected;
	// The foreground game and its mode ("Cyberpunk 2077 · DualSense supported") once one was seen.
	public string GameValue => _service.Games.State.Name
		?? (_service.IsGameConnected ? Text.ValueGameConnected() : Text.ValueNoGame());
	public string RowModeLabel => Text.RowMode();
	public bool HasGame => _service.Games.State.Name != null;
	public bool IsSupportedMode => HasGame && _service.Games.State.Mode == Kopru.Core.Games.GameMode.Supported;
	// Short for the narrow card: "Supported" / "Classic".
	public string ModeValue => !HasGame ? Text.ValueNoGame() : IsSupportedMode ? Text.OverrideSupported() : Text.ModeShortClassic();
	public string GameDetail => GameModeText.Line(_service.Games.State);
	public string VirtualPadValue => _service.IsBridgeRunning ? Text.ValuePluggedIn() : Text.ValueUnplugged();
	public bool IsHapticsActive => _service.IsHapticsActive;
	public string ModelValue => _service.IsConnected ? _service.DeviceName : Text.DeviceNoController();
	public bool HasBattery => _service.IsConnected && _service.HasBattery;
	public string BatteryShort => HasBattery ? BatteryValue : Text.BatteryUnknown();
	public bool IsBatteryLow => HasBattery && !_service.IsCharging && _service.BatteryPercent <= 15;
	public bool IsCharging => _service.IsCharging;

	// Quick actions / controls / bottom strip
	public string QuickActionsLabel => Text.QuickActions();
	public string QuickControlsLabel => Text.QuickControls();
	public string TriggerStrengthLabel => Text.TriggerStrength();
	public string BrightnessLabel => Text.Brightness();
	public string StartupLabel => Text.GroupStartup();
	public string StartWithWindowsLabel => Text.StartWithWindows();
	public string PowerSavingLabel => Text.GroupPowerSaving();
	public string IdlePowerOffLabel => Text.IdlePowerOff();
	public string LightGroupLabel => Text.NavLight();
	public string PlayerLedsLabel => Text.PlayerLeds();

	// Steam Input warning (only when it may take the controller).
	public bool HasSteamWarning => SteamInputText.IsWarning(_service.SteamInput);
	public string SteamWarning => HasSteamWarning ? _service.SteamInput!.Advice : string.Empty;
	// Product name, the same in every language.
	public string SteamTitle => "Steam Input";

	// Turn off controller quick action
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
		if (_service.IsBridgeRunning)
		{
			await _service.StopBridgeAsync();
		}
		else
		{
			await _service.StartBridgeAsync();
		}
	}

	[RelayCommand]
	private void TestHaptics(HapticSide side) => _service.TestHaptics(side);

	[RelayCommand]
	private void TestTrigger() => _service.TestTrigger(TriggerTestEffect.Weapon);

	[RelayCommand]
	private void TestLight() => _service.TestLight(Settings.LightbarColour);
}

public sealed class TriggerEffectItem(TriggerTestEffect effect, Func<string> name, Func<string> hint)
{
	public TriggerTestEffect Effect { get; } = effect;
	public string Name => name();
	public string Hint => hint();
}

public sealed partial class TriggersViewModel : PageViewModel
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
	public override string Title => Text.NavTriggers();
	public string Intro => Text.TriggersIntro();
	public string StrengthLabel => Text.TriggerStrength();
	public string StrengthHint => Text.TriggerStrengthHint();
	public string EffectsLabel => Text.TriggerTestEffects();

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
		// Language names are shown in their own language (endonyms) on purpose.
		Languages = BuildLanguages();
		_selectedLanguage = Languages.First(o => o.Language == Localizer.Chosen);
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
