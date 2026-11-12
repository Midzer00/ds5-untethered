// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// Settings shared by several pages (e.g. haptics level on Overview and on Haptics); pushed to the service on change,
// loaded from and saved (debounced) to the settings store.
public sealed partial class SettingsState : ViewModelBase
{
	public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

	private readonly IControllerService _service;
	private readonly ISettingsStore? _store;
	private readonly IStartupRegistration? _startup;
	private readonly Timer? _saveTimer;
	private readonly bool _loading;
	private AppSettings? _pending;

	public SettingsState(IControllerService service, ISettingsStore? store = null, IStartupRegistration? startup = null)
	{
		_service = service;
		_store = store;
		_startup = startup;
		AppSettings? saved = store?.Load();
		IsFirstRun = store != null && saved == null;
		SetupCompleted = saved != null ? saved.SetupCompleted : false;
		if (saved != null)
		{
			_loading = true;
			Localizer.Chosen = saved.Language switch { "tr" => Language.Turkish, "en" => Language.English, _ => null };
			GameHapticsLevel = saved.GameHapticsLevel;
			RumbleLevel = saved.RumbleLevel;
			TriggerStrength = Math.Clamp(saved.TriggerStrength, 0, 100);
			LightFromGame = saved.LightFromGame;
			RumbleFeel = saved.RumbleFeel;
			FallbackTrigger = saved.FallbackTrigger;
			FallbackStrength = saved.FallbackStrength;
			LightbarColour = saved.LightbarColour;
			Brightness = saved.Brightness;
			PlayerLeds = saved.PlayerLeds;
			IdlePowerOffMinutes = saved.IdlePowerOffMinutes;
			StartWithWindows = saved.StartWithWindows;
			PowerOffWhenIdle = saved.PowerOffWhenIdle;
			LowPowerOn = saved.LowPowerOn;
			AutoLowPower = saved.AutoLowPower;
			Tuning = saved.Tuning ?? InputTuning.Default;
			Remap = saved.Remap ?? RemapSettings.Default;
			Gyro = saved.Gyro ?? GyroSettings.Default;
			NotificationsEnabled = saved.NotificationsEnabled;
			NotifyConnection = saved.NotifyConnection;
			NotifyBattery = saved.NotifyBattery;
			NotifyGame = saved.NotifyGame;
			NotifySteamInput = saved.NotifySteamInput;
			NotifyPowerOff = saved.NotifyPowerOff;
			NotifyErrors = saved.NotifyErrors;
			NotifyHidHide = saved.NotifyHidHide;
			NotifyLinkQuality = saved.NotifyLinkQuality;
			QuietWhilePlaying = saved.QuietWhilePlaying;
			KeepInTray = saved.KeepInTray;
			TrayHintShown = saved.TrayHintShown;
			AutoUpdateGameList = saved.AutoUpdateGameList;
			GameListCheckedAt = saved.GameListCheckedAt;
			DownloadGameImages = saved.DownloadGameImages;
			AskOnKnownGame = saved.AskOnKnownGame;
			LoadLight(saved);
			ShortcutsEnabled = saved.ShortcutsEnabled;
			PsOpensApp = saved.PsOpensApp;
			PsOpensAppInGames = saved.PsOpensAppInGames;
			SpeakerEnabled = saved.SpeakerEnabled;
			SpeakerVolume = Math.Clamp(saved.SpeakerVolume, 0, 100);
			HeadphoneVolume = Math.Clamp(saved.HeadphoneVolume, 0, 100);
			DesktopMode = saved.DesktopMode ?? DesktopModeSettings.Default;
			LoadMic(saved);
			AutoStartBridge = saved.AutoStartBridge;
			LoadAudioDefaults(saved);
			LoadInstalledGames(saved);
			_loading = false;
			service.Games.LoadMemory(saved.ToGameMemory(service.Games.Catalog));
		}
		else if (IsFirstRun)
		{
			// A fresh install starts with the saved defaults (DualSense Mode on).
			service.Games.LoadMemory(new AppSettings().ToGameMemory(service.Games.Catalog));
		}
		InitProfiles(saved);
		InitDesktop();
		InitControllerProfiles(saved);
		InitLowPower();
		InitDualSenseMode();
		InitAudioDevices();
		if (store != null)
		{
			_saveTimer = new Timer(_ => SaveNow());
			Localizer.LanguageChanged += _ => ScheduleSave();
			service.Games.MemoryChanged += ScheduleSave;
		}
		Push();
	}

	// No settings were saved before.
	public bool IsFirstRun { get; }

	// True once the required setup steps were all done (the setup page then stops opening by itself); null = an older
	// settings file that does not say yet. A new install starts with false.
	public bool? SetupCompleted { get; private set; }

	// Remembers that setup is complete and saves it. Never undone: a driver removed later shows the blocking state.
	public void MarkSetupCompleted()
	{
		if (SetupCompleted == true)
		{
			return;
		}
		SetupCompleted = true;
		ScheduleSave();
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(GameHapticsText))]
	private double _gameHapticsLevel = 150;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(RumbleText))]
	private double _rumbleLevel = 100;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(TriggerStrengthText))]
	private double _triggerStrength = 100;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(LightColor))]
	private uint _lightbarColour = 0x2F6BFF;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(BrightnessText))]
	[NotifyPropertyChangedFor(nameof(LightIntensity))]
	private double _brightness = 80;

	[ObservableProperty]
	private bool _playerLeds = true;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IdlePowerOffText))]
	private double _idlePowerOffMinutes = 15;

	[ObservableProperty]
	private bool _startWithWindows;

	[ObservableProperty]
	private bool _powerOffWhenIdle;

	// Notifications and the tray (see NotificationCenter).
	[ObservableProperty]
	private bool _notificationsEnabled = true;

	[ObservableProperty]
	private bool _notifyConnection = true;

	[ObservableProperty]
	private bool _notifyBattery = true;

	[ObservableProperty]
	private bool _notifyGame = true;

	[ObservableProperty]
	private bool _notifySteamInput = true;

	[ObservableProperty]
	private bool _notifyPowerOff = true;

	[ObservableProperty]
	private bool _notifyErrors = true;

	[ObservableProperty]
	private bool _notifyHidHide = true;

	// The weak link warning (LinkQualityMonitor).
	[ObservableProperty]
	private bool _notifyLinkQuality = true;

	[ObservableProperty]
	private bool _quietWhilePlaying;

	[ObservableProperty]
	private bool _keepInTray = true;

	[ObservableProperty]
	private bool _trayHintShown;

	[ObservableProperty]
	private bool _autoUpdateGameList = true;

	// Ask when a known game starts while the supported mode is off.
	[ObservableProperty]
	private bool _askOnKnownGame = true;

	[ObservableProperty]
	private DateTimeOffset? _gameListCheckedAt;

	// Download game pictures from Steam for the supported games list.
	[ObservableProperty]
	private bool _downloadGameImages = true;

	// Stick and trigger tuning (edited on the sticks & triggers page).
	[ObservableProperty]
	private InputTuning _tuning = InputTuning.Default;

	// Button remapping and stick swap/invert (edited on the remap & gyro tab).
	[ObservableProperty]
	private RemapSettings _remap = RemapSettings.Default;

	// Gyro aiming (edited on the remap & gyro tab).
	[ObservableProperty]
	private GyroSettings _gyro = GyroSettings.Default;

	[ObservableProperty]
	private bool _lightFromGame = true;

	// Experimental controller speaker: the switch and the volumes in percent.
	[ObservableProperty]
	private bool _speakerEnabled;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SpeakerVolumeText))]
	private double _speakerVolume = 80;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HeadphoneVolumeText))]
	private double _headphoneVolume = 60;

	public string SpeakerVolumeText => Text.PercentValue(Math.Round(SpeakerVolume));
	public string HeadphoneVolumeText => Text.PercentValue(Math.Round(HeadphoneVolume));

	[ObservableProperty]
	private RumbleFeel _rumbleFeel = RumbleFeel.Soft;

	[ObservableProperty]
	private FallbackTrigger _fallbackTrigger = FallbackTrigger.Off;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(FallbackStrengthText))]
	private double _fallbackStrength = 50;

	public string GameHapticsText => Text.PercentValue(Math.Round(GameHapticsLevel));
	public string RumbleText => Text.PercentValue(Math.Round(RumbleLevel));
	public string TriggerStrengthText => Text.PercentValue(Math.Round(TriggerStrength));
	public string FallbackStrengthText => Text.PercentValue(Math.Round(FallbackStrength));
	public string BrightnessText => Text.PercentValue(Math.Round(Brightness));
	public string IdlePowerOffText => IdlePowerOffMinutes < 0.5 ? Text.Never() : Text.Minutes(Math.Round(IdlePowerOffMinutes));

	protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
	{
		base.OnPropertyChanged(e);
		if (_loading || _applyingProfile || string.IsNullOrEmpty(e.PropertyName) || e.PropertyName.EndsWith("Text", StringComparison.Ordinal)
			|| e.PropertyName is nameof(LightColor) or nameof(LightIntensity) or nameof(ActiveProfileName) or nameof(ActiveProfileLine)
			|| MicStateNames.Contains(e.PropertyName))
		{
			return;
		}
		if (e.PropertyName == nameof(LowPowerOn))
		{
			NotifyMicState();
		}
		CaptureProfileValue(e.PropertyName);
		if (e.PropertyName == nameof(StartWithWindows))
		{
			// Only a change by the user touches the registry.
			try
			{
				_startup?.Set(StartWithWindows);
			}
			catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
			{
				// Leave the switch as chosen; Windows refused the change.
			}
		}
		Push();
		ScheduleSave();
	}

	// Light bar colour and glow strength for the controller drawing.
	public Color LightColor => Color.FromRgb((byte)(LightbarColour >> 16), (byte)(LightbarColour >> 8), (byte)LightbarColour);
	public double LightIntensity => 0.15 + 0.85 * Math.Clamp(Brightness / 100.0, 0, 1);

	public ControllerSettings ToSettings() => new(GameHapticsLevel, RumbleLevel, TriggerStrength, LightbarColour, Brightness,
		PlayerLeds, IdlePowerOffMinutes, LightFromGame, RumbleFeel, FallbackTrigger, FallbackStrength)
	{
		Tuning = Tuning,
		Remap = Remap,
		Gyro = Gyro,
		PowerOffWhenIdle = PowerOffWhenIdle,
		LightOptions = LightOptions,
		ShortcutsEnabled = ShortcutsEnabled,
		PsOpensApp = PsOpensApp,
		PsOpensAppInGames = PsOpensAppInGames,
		Speaker = new SpeakerSettings(SpeakerEnabled, Math.Clamp(SpeakerVolume / 100, 0, 1), Math.Clamp(HeadphoneVolume / 100, 0, 1)),
		DesktopModeOn = DesktopModeOn,
		Desktop = DesktopMode,
		Mic = ToMicSettings()
	};

	public AppSettings ToAppSettings() => WithInstalledGames(WithLight(new AppSettings
	{
		Language = Localizer.Chosen switch { Language.Turkish => "tr", Language.English => "en", _ => null },
		GameHapticsLevel = GameHapticsLevel,
		RumbleLevel = RumbleLevel,
		TriggerStrength = TriggerStrength,
		LightbarColour = LightbarColour,
		Brightness = Brightness,
		PlayerLeds = PlayerLeds,
		IdlePowerOffMinutes = IdlePowerOffMinutes,
		StartWithWindows = StartWithWindows,
		PowerOffWhenIdle = PowerOffWhenIdle,
		LowPowerOn = LowPowerOn,
		AutoLowPower = AutoLowPower,
		Tuning = Tuning,
		Remap = Remap,
		Gyro = Gyro,
		LightFromGame = LightFromGame,
		RumbleFeel = RumbleFeel,
		FallbackTrigger = FallbackTrigger,
		FallbackStrength = FallbackStrength,
		NotificationsEnabled = NotificationsEnabled,
		NotifyConnection = NotifyConnection,
		NotifyBattery = NotifyBattery,
		NotifyGame = NotifyGame,
		NotifySteamInput = NotifySteamInput,
		NotifyPowerOff = NotifyPowerOff,
		NotifyErrors = NotifyErrors,
		NotifyHidHide = NotifyHidHide,
		NotifyLinkQuality = NotifyLinkQuality,
		QuietWhilePlaying = QuietWhilePlaying,
		KeepInTray = KeepInTray,
		TrayHintShown = TrayHintShown,
		AutoUpdateGameList = AutoUpdateGameList,
		GameListCheckedAt = GameListCheckedAt,
		DownloadGameImages = DownloadGameImages,
		ShortcutsEnabled = ShortcutsEnabled,
		PsOpensApp = PsOpensApp,
		PsOpensAppInGames = PsOpensAppInGames,
		SpeakerEnabled = SpeakerEnabled,
		SpeakerVolume = SpeakerVolume,
		HeadphoneVolume = HeadphoneVolume,
		DesktopMode = DesktopMode,
		MicEnabled = MicEnabled,
		MicGain = MicGain,
		MicNoiseGate = MicNoiseGate,
		AutoStartBridge = AutoStartBridge,
		ProtectDefaultAudio = ProtectDefaultAudio,
		MicButtonMode = MicButtonMode,
		ControllerSpeakerChosen = AudioChoices.ControllerSpeakerChosen,
		ControllerMicChosen = AudioChoices.ControllerMicChosen,
		SetupCompleted = SetupCompleted,
	}.WithGames(_service.Games.SnapshotMemory()).WithProfiles(Profiles).WithControllerProfiles(_controllerProfiles).WithKnownControllers(KnownControllers)));

	// Writes a pending change at once (also called by the debounce timer and on exit).
	public void SaveNow()
	{
		AppSettings? pending = Interlocked.Exchange(ref _pending, null);
		if (pending == null || _store == null)
		{
			return;
		}
		try
		{
			_store.Save(pending);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// Not saved this time; the next change tries again.
		}
	}

	private void ScheduleSave()
	{
		if (_store == null || _loading)
		{
			return;
		}
		_pending = ToAppSettings();
		_saveTimer?.Change(SaveDelay, Timeout.InfiniteTimeSpan);
	}

	// Sends the effective settings (Low Power applied) when they differ from the last ones sent: one user change is one
	// push, and a change that does not alter what the controller gets (e.g. the speaker switch while Low Power holds it
	// off, or a second notification for the same change) sends nothing.
	private void Push()
	{
		_service.Games.AskOnKnownGame = AskOnKnownGame;
		ControllerSettings next = Effective(ToSettings());
		if (next != _pushed)
		{
			if (_pushed == null || _pushed.Speaker.Enabled != next.Speaker.Enabled || _pushed.Mic.Enabled != next.Mic.Enabled)
			{
				Kopru.Core.Diagnostics.AppLog.Write($"settings push: speaker {(next.Speaker.Enabled ? "on" : "off")}, mic {(next.Mic.Enabled ? "on" : "off")} " +
					$"(DualSense Mode {(_modeOn ? "on" : "off")}, Low Power {(LowPowerOn ? "on" : "off")})");
			}
			_pushed = next;
			_service.ApplySettings(next);
		}
		PushControllerProfiles();
	}

	private ControllerSettings? _pushed;
}
