// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Steam;

namespace Kopru.App.Services;

// Colour family of a toast: info = accent blue, success = green, warning = amber, error = red.
public enum NotificationKind
{
	Info,
	Success,
	Warning,
	Error
}

public enum NotificationType
{
	Connected,
	Disconnected,
	BatteryLow,
	BatteryCritical,
	Charged,
	GameDetected,
	SteamInput,
	PoweredOffIdle,
	BridgeError,
	HidHideMissing,
	TrayHint,
	GameListUpdated,
	// A known game started with the supported mode off: turn it on? (with buttons)
	GameAsk,
	// Feedback after a controller shortcut (profile, haptics level, mode)
	Shortcut,
	// The bridged controller was plugged in by cable
	CableConnected,
	// The Bluetooth link stayed weak long enough to feel (LinkQualityMonitor)
	LinkWeak
}

// The page a click on a toast opens.
public enum AppPage
{
	Overview,
	SupportedGames,
	OtherGames,
	// Settings on its General tab (power, running in the background) and on its Shortcuts tab.
	SettingsGeneral,
	SettingsShortcuts
}

// A button on a toast; Run is called on the UI thread, then the toast closes. The primary one is drawn in the accent.
public sealed record NotificationAction(string Label, Action Run, bool IsPrimary = false);

// One toast. Value (0-100) is the controller's battery level, shown as a battery glyph; IsEdge picks the controller drawing
// and IsCharging shows the battery as charging. A toast with actions stays up longer (ActionDuration).
public sealed record AppNotification(NotificationType Type, NotificationKind Kind, string Title, string Body, AppPage Page, double? Value = null,
	bool IsEdge = false, bool IsCharging = false)
{
	public static readonly TimeSpan NormalDuration = TimeSpan.FromSeconds(5);
	public static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(8);
	public static readonly TimeSpan ActionDuration = TimeSpan.FromSeconds(20);

	public IReadOnlyList<NotificationAction> Actions { get; init; } = [];

	public TimeSpan Duration => Actions.Count > 0 ? ActionDuration : Kind == NotificationKind.Error ? ErrorDuration : NormalDuration;
}

// Turns service state changes and notices into notifications: tracks transitions (connected, battery thresholds once per
// discharge, game changes, Steam Input once per game), applies the per-type switches and "don't notify while playing",
// and drops repeats (same type within TypeCooldown, same text within RepeatCooldown, more than BurstLimit per BurstWindow).
public sealed partial class NotificationCenter
{
	public static readonly TimeSpan TypeCooldown = TimeSpan.FromSeconds(20);
	public static readonly TimeSpan RepeatCooldown = TimeSpan.FromMinutes(2);
	public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(10);
	public const int BurstLimit = 3;
	public const double LowBatteryPercent = 20;
	public const double CriticalBatteryPercent = 10;
	// Above this (or when charging) a new discharge starts and the thresholds count again.
	public const double BatteryRearmPercent = 25;

	private readonly IControllerService _service;
	private readonly SettingsState _settings;
	private readonly Func<bool> _fullScreen;
	private readonly Func<long> _clockMs;
	private readonly object _gate = new();
	private readonly Dictionary<NotificationType, long> _lastByType = [];
	private readonly Dictionary<(NotificationType, string), long> _lastByText = [];
	private readonly Queue<long> _recent = new();
	private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
	// Games asked about in their current launch (answering "always" there is no news).
	private readonly HashSet<string> _asked = new(StringComparer.OrdinalIgnoreCase);

	private bool _connected;
	private string? _gameExe;
	private string? _steamWarnedFor;
	private bool _lowShown;
	private bool _criticalShown;
	private bool _chargedShown;
	private bool _wasCharging;
	private bool _hidHideShown;

	public NotificationCenter(IControllerService service, SettingsState settings, Func<bool>? fullScreen = null, Func<long>? clockMs = null)
	{
		_service = service;
		_settings = settings;
		_fullScreen = fullScreen ?? (() => false);
		_clockMs = clockMs ?? (() => Environment.TickCount64);
		// What is true at start is the baseline, not news.
		_connected = service.IsConnected;
		InitControllers();
		_gameExe = service.Games.State.Exe;
		if (_gameExe != null && service.Games.State.Mode == GameMode.Supported)
		{
			_announced.Add(_gameExe);
		}
		_wasCharging = service.IsCharging;
		service.StateChanged += (_, _) => Evaluate();
		service.Notice += (_, notice) => OnNotice(notice);
		settings.ShortcutFeedback += (title, body) =>
		{
			if (_settings.NotificationsEnabled)
			{
				Post(new AppNotification(NotificationType.Shortcut, NotificationKind.Info, title, body, AppPage.SettingsShortcuts, IsEdge: IsEdge), force: true);
			}
		};
		service.Games.AskRequested += game =>
		{
			lock (_gate)
			{
				_asked.Add(game.Exe);
			}
			Post(Ask(service.Games, game, IsEdge), force: true);
		};
	}

	// Raised for every notification that passed the filters; may be raised on any thread.
	public event Action<AppNotification>? Raised;

	// A game is connected to the virtual controller, or a full-screen app is in front.
	public bool IsPlaying => _service.IsGameConnected || SafeFullScreen();

	public void Evaluate()
	{
		List<AppNotification> pending = [];
		lock (_gate)
		{
			CheckConnection(pending);
			CheckBattery(pending);
			CheckGame(pending);
		}
		foreach (AppNotification n in pending)
		{
			Post(n);
		}
		CheckLink();
	}

	// The weak link warning, when the monitor says one is due (it already waited for the link to stay weak, skipped the
	// first seconds after connecting and allows one per bad episode). It goes through the same filters as every toast,
	// "Don't Notify While Playing" included: whoever asked for silence during games gets it, and since the warning stays
	// due while the link is weak, it shows once the game is left if the link is still weak then. With that setting off
	// (the default) it shows during games, which is when it matters. Acknowledged only when it was actually shown.
	private void CheckLink()
	{
		if (_service.LinkQuality is not { WarningDue: true } || !_service.IsConnected)
		{
			return;
		}
		if (Post(LinkWeak(IsEdge)))
		{
			_service.AcknowledgeLinkWarning();
		}
	}

	public static AppNotification LinkWeak(bool isEdge = false) =>
		new(NotificationType.LinkWeak, NotificationKind.Warning, Text.NotifyLinkTitle(), Text.NotifyLinkBody(), AppPage.Overview, IsEdge: isEdge);

	public void OnNotice(ServiceNotice notice)
	{
		AppNotification? n = notice.Kind switch
		{
			ServiceNoticeKind.PoweredOffIdle => new AppNotification(NotificationType.PoweredOffIdle, NotificationKind.Info,
				Text.NotifyPoweredOffTitle(), Text.NotifyPoweredOffBody(), AppPage.SettingsGeneral, IsEdge: IsEdge),
			ServiceNoticeKind.BridgeError => new AppNotification(NotificationType.BridgeError, NotificationKind.Error,
				Text.NotifyBridgeErrorTitle(), notice.Detail ?? Text.NotifyBridgeOther(), AppPage.Overview, IsEdge: IsEdge),
			ServiceNoticeKind.HidHideMissing => HidHideOnce(),
			ServiceNoticeKind.CableConnected => new AppNotification(NotificationType.CableConnected, NotificationKind.Info,
				Text.NotifyCableTitle(), Text.NotifyCableBody(), AppPage.Overview, IsEdge: IsEdge),
			ServiceNoticeKind.PadTypeChanged => PadTypeNotice(notice.Detail, IsEdge, _service.Games.ForcedPadType != null),
			_ => null
		};
		if (n != null)
		{
			Post(n);
		}
	}

	// Shows a notification if the switches, "quiet while playing" and the rate limits allow it. Forced ones (the tray hint)
	// skip all of that. Returns whether it was raised.
	public bool Post(AppNotification n, bool force = false)
	{
		if (!force)
		{
			if (!_settings.NotificationsEnabled || !IsEnabled(n.Type) || (_settings.QuietWhilePlaying && IsPlaying))
			{
				return false;
			}
			lock (_gate)
			{
				long now = _clockMs();
				if ((Within(_lastByType, n.Type, now, TypeCooldown) && !IsSlotNotice(n)) || Within(_lastByText, (n.Type, n.Title + "\n" + n.Body), now, RepeatCooldown))
				{
					return false;
				}
				while (_recent.Count > 0 && now - _recent.Peek() >= BurstWindow.TotalMilliseconds)
				{
					_recent.Dequeue();
				}
				if (_recent.Count >= BurstLimit)
				{
					return false;
				}
				_recent.Enqueue(now);
				_lastByType[n.Type] = now;
				_lastByText[(n.Type, n.Title + "\n" + n.Body)] = now;
			}
		}
		Raised?.Invoke(n);
		return true;
	}

	public bool IsEnabled(NotificationType type) => type switch
	{
		NotificationType.Connected or NotificationType.Disconnected => _settings.NotifyConnection,
		NotificationType.BatteryLow or NotificationType.BatteryCritical or NotificationType.Charged => _settings.NotifyBattery,
		NotificationType.GameDetected => _settings.NotifyGame,
		NotificationType.SteamInput => _settings.NotifySteamInput,
		NotificationType.PoweredOffIdle => _settings.NotifyPowerOff,
		NotificationType.BridgeError => _settings.NotifyErrors,
		NotificationType.HidHideMissing => _settings.NotifyHidHide,
		NotificationType.LinkWeak => _settings.NotifyLinkQuality,
		_ => true
	};

	public static AppNotification GameListUpdated(int added) =>
		new(NotificationType.GameListUpdated, NotificationKind.Success, Text.NotifyGameListTitle(), Text.NotifyGameListBody(added), AppPage.SupportedGames);

	// "Cyberpunk 2077 Started · Turn on DualSense supported mode?" with Turn On / Not Now / Always. Shown whatever the
	// filters say: the controller only raises it once per launch, and only when "ask" is on.
	public static AppNotification Ask(GameModeController games, ForegroundGame game, bool isEdge = false) =>
		new(NotificationType.GameAsk, NotificationKind.Info, game.Name, Text.AskGameBody(), AppPage.SupportedGames, IsEdge: isEdge)
		{
			Actions =
			[
				new(Text.AskNotNow(), () => games.Answer(game, AskAnswer.NotNow)),
				new(Text.AskAlways(), () => games.Answer(game, AskAnswer.Always)),
				new(Text.AskTurnOn(), () => games.Answer(game, AskAnswer.TurnOn), IsPrimary: true),
			]
		};

	// The short reason a bridge error toast shows; the full text is on the Overview page.
	public static string BridgeReason(BridgeError error) => error switch
	{
		BridgeError.NoCapture => Text.NotifyBridgeNoIdentity(),
		BridgeError.CaptureInvalid => Text.NotifyBridgeIdentityUnreadable(),
		BridgeError.UsbIpMissing => Text.NotifyBridgeUsbIpMissing(),
		BridgeError.AttachFailed => Text.NotifyBridgeAttachFailed(),
		BridgeError.ControllerOpenFailed => Text.NotifyBridgeOpenFailed(),
		BridgeError.ControllerLost => Text.NotifyBridgeLost(),
		_ => Text.NotifyBridgeOther()
	};

	public static AppNotification TrayHint() =>
		new(NotificationType.TrayHint, NotificationKind.Info, Text.NotifyTrayTitle(), Text.NotifyTrayBody(), AppPage.SettingsGeneral);

	private AppNotification? HidHideOnce()
	{
		lock (_gate)
		{
			if (_hidHideShown)
			{
				return null;
			}
			_hidHideShown = true;
		}
		return new AppNotification(NotificationType.HidHideMissing, NotificationKind.Warning, Text.NotifyHidHideTitle(), Text.NotifyHidHideBody(), AppPage.Overview,
			IsEdge: IsEdge);
	}

	private void CheckConnection(List<AppNotification> pending)
	{
		if (CheckControllers(pending))
		{
			return;
		}
		bool connected = _service.IsConnected;
		if (connected == _connected)
		{
			return;
		}
		_connected = connected;
		if (connected)
		{
			bool battery = _service.HasBattery;
			double percent = Math.Round(_service.BatteryPercent);
			pending.Add(new AppNotification(NotificationType.Connected, NotificationKind.Success, Text.NotifyConnectedTitle(),
				Text.NotifyConnectedBody(_service.DeviceName),
				AppPage.Overview, battery ? percent : null, IsEdge, _service.IsCharging));
		}
		else if (!RecentlyRaised(NotificationType.PoweredOffIdle))
		{
			// After an idle power-off the "turned off" toast already says it.
			pending.Add(new AppNotification(NotificationType.Disconnected, NotificationKind.Info, Text.NotifyDisconnectedTitle(),
				Text.NotifyDisconnectedBody(), AppPage.Overview, IsEdge: IsEdge));
		}
	}

	private void CheckBattery(List<AppNotification> pending)
	{
		if (!_service.IsConnected || !_service.HasBattery)
		{
			return;
		}
		double percent = Math.Round(_service.BatteryPercent);
		bool charging = _service.IsCharging;
		if (charging || percent > BatteryRearmPercent)
		{
			_lowShown = _criticalShown = false;
		}
		if (percent < 95)
		{
			_chargedShown = false;
		}
		if (!charging && percent <= CriticalBatteryPercent && !_criticalShown)
		{
			_criticalShown = _lowShown = true;
			pending.Add(new AppNotification(NotificationType.BatteryCritical, NotificationKind.Error, Text.NotifyBatteryCriticalTitle(),
				Text.NotifyBatteryCriticalBody(percent), AppPage.Overview, percent, IsEdge));
		}
		else if (!charging && percent <= LowBatteryPercent && !_lowShown)
		{
			_lowShown = true;
			pending.Add(new AppNotification(NotificationType.BatteryLow, NotificationKind.Warning, Text.NotifyBatteryLowTitle(),
				Text.NotifyBatteryLowBody(percent), AppPage.Overview, percent, IsEdge));
		}
		if (percent >= 100 && (charging || _wasCharging) && !_chargedShown)
		{
			_chargedShown = true;
			pending.Add(new AppNotification(NotificationType.Charged, NotificationKind.Success, Text.NotifyChargedTitle(),
				Text.NotifyChargedBody(), AppPage.Overview, 100, IsEdge, charging));
		}
		_wasCharging = charging;
	}

	private void CheckGame(List<AppNotification> pending)
	{
		GameModeState state = _service.Games.State;
		if (state.Exe == null)
		{
			_asked.Clear();
		}
		if (state.Exe != null && !string.Equals(state.Exe, _gameExe, StringComparison.OrdinalIgnoreCase))
		{
			_gameExe = state.Exe;
			_steamWarnedFor = null;
		}
		// A game that turned the supported mode on by itself ("always") is announced once per session (switching away and
		// back is not news); one the user was asked about or turned on is not.
		if (state.Exe != null && state.Mode == GameMode.Supported && state.Reason == ModeReason.Always && !_asked.Contains(state.Exe)
			&& _announced.Add(state.Exe))
		{
			string name = state.Name ?? Path.GetFileNameWithoutExtension(state.Exe);
			pending.Add(new AppNotification(NotificationType.GameDetected, NotificationKind.Info, name,
				Text.NotifyGameSupported(), AppPage.SupportedGames, Battery, IsEdge, Charging));
		}
		if (_gameExe != null && _steamWarnedFor == null && _service.SteamInput is { Status: SteamInputStatus.OnGlobally or SteamInputStatus.OnForApp })
		{
			_steamWarnedFor = _gameExe;
			pending.Add(new AppNotification(NotificationType.SteamInput, NotificationKind.Warning, Text.NotifySteamInputTitle(),
				Text.NotifySteamInputBody(), AppPage.SupportedGames, IsEdge: IsEdge));
		}
	}

	// What the toasts show about the controller: its drawing, battery level (when known) and charging state.
	private bool IsEdge => _service.Kind == ControllerKind.DualSenseEdge;
	private double? Battery => _service.IsConnected && _service.HasBattery ? Math.Round(_service.BatteryPercent) : null;
	private bool Charging => Battery != null && _service.IsCharging;

	private bool RecentlyRaised(NotificationType type) => Within(_lastByType, type, _clockMs(), TypeCooldown);

	private static bool Within<TKey>(Dictionary<TKey, long> last, TKey key, long now, TimeSpan window) where TKey : notnull =>
		last.TryGetValue(key, out long at) && now - at < window.TotalMilliseconds;

	private bool SafeFullScreen()
	{
		try
		{
			return _fullScreen();
		}
		catch (Exception)
		{
			return false;
		}
	}
}
