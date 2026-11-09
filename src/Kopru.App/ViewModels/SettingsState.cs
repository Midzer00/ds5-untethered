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
			Tuning = saved.Tuning ?? InputTuning.Default;
			NotificationsEnabled = saved.NotificationsEnabled;
			NotifyConnection = saved.NotifyConnection;
			NotifyBattery = saved.NotifyBattery;
			NotifyGame = saved.NotifyGame;
			NotifySteamInput = saved.NotifySteamInput;
			NotifyPowerOff = saved.NotifyPowerOff;
			NotifyErrors = saved.NotifyErrors;
			NotifyHidHide = saved.NotifyHidHide;
			QuietWhilePlaying = saved.QuietWhilePlaying;
			KeepInTray = saved.KeepInTray;
			TrayHintShown = saved.TrayHintShown;
			_loading = false;
			service.Games.LoadMemory(saved.GameOverrides, saved.LearnedGames);
		}
		if (store != null)
		{
			_saveTimer = new Timer(_ => SaveNow());
			Localizer.LanguageChanged += _ => ScheduleSave();
			service.Games.MemoryChanged += ScheduleSave;
		}
		Push();
	}

	// No settings were saved before (the setup wizard opens on its own).
	public bool IsFirstRun { get; }

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

	[ObservableProperty]
	private bool _quietWhilePlaying;

	[ObservableProperty]
	private bool _keepInTray = true;

	[ObservableProperty]
	private bool _trayHintShown;

	// Stick and trigger tuning (edited on the sticks & triggers page).
	[ObservableProperty]
	private InputTuning _tuning = InputTuning.Default;

	[ObservableProperty]
	private bool _lightFromGame = true;

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
		if (_loading || string.IsNullOrEmpty(e.PropertyName) || e.PropertyName.EndsWith("Text", StringComparison.Ordinal)
			|| e.PropertyName is nameof(LightColor) or nameof(LightIntensity))
		{
			return;
		}
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
		PowerOffWhenIdle = PowerOffWhenIdle
	};

	public AppSettings ToAppSettings() => new()
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
		Tuning = Tuning,
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
		QuietWhilePlaying = QuietWhilePlaying,
		KeepInTray = KeepInTray,
		TrayHintShown = TrayHintShown,
		GameOverrides = _service.Games.SnapshotOverrides(),
		LearnedGames = _service.Games.SnapshotLearned()
	};

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

	private void Push() => _service.ApplySettings(ToSettings());
}
