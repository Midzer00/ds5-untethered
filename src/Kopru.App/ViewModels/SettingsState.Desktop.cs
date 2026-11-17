// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Games;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// Desktop mode: the switch (not saved; PS + Options, the touchpad double press, the PS tap that opens the app, the tray,
// Quick Settings and the Settings group change it through SetDesktopMode, which logs who did it), its tuning (saved), a toast when it was switched from
// the controller, and the automatic switch-off when a game takes the controller: a known game (catalog or added) coming
// to the front, or a program with the virtual pad open using the controller as a game does (IControllerService.GameUsingController: a trigger
// effect, haptics sound, rumble) while a program that could be a game is in front (PossibleGameInFront: never this app or
// the desktop); a use already going on counts when such a program comes to the front (OnForegroundChanged). The automatic switch-off raises DesktopAutoOff instead of the shortcut toast
// (NotificationCenter shows it through the usual filters).
public sealed partial class SettingsState
{
	// A game's use of the controller this soon after mouse mode was turned on does not turn it off again: the game may react
	// to the input going neutral (or to the touchpad presses that turned it on) with a rumble or a new trigger effect.
	public static readonly TimeSpan GameUseGrace = TimeSpan.FromSeconds(3);

	// The log source of the touchpad double press.
	public const string TouchpadDoublePressSource = "touchpad double press";

	private DesktopModeViewModel? _desktopPanel;
	private string? _desktopGameKey;
	// Set just before DesktopModeOn changes: who changed it (logged), or that it was switched off automatically (and for
	// which game: its display name and exe; the name is null for a game that started using the controller).
	private string? _desktopSource;
	private bool _desktopAuto;
	private string? _desktopOffGame;
	private string _desktopOffExe = string.Empty;
	private bool _desktopBridgeRunning;
	private long _desktopOnAt;

	[ObservableProperty]
	private bool _desktopModeOn;

	[ObservableProperty]
	private DesktopModeSettings _desktopMode = DesktopModeSettings.Default;

	// Monotonic milliseconds (tests replace it).
	public Func<long> Clock { get; set; } = () => Environment.TickCount64;

	// The desktop mode group on the Settings page.
	public DesktopModeViewModel DesktopPanel => _desktopPanel ??= new DesktopModeViewModel(this);

	// Mouse mode was switched off automatically because of a game: the name and exe file name of a known game that came to
	// the front, or null and what the game used (GameUseNames.LogName) for a game that started using the controller.
	// Raised on the UI thread.
	public event Action<string?, string>? DesktopAutoOff;

	// Mouse mode was turned on with the touchpad double press (the tips count it). Raised on the UI thread.
	public event Action? DesktopGestureUsed;

	// Every change by the user goes through here: source is what the log names ("shortcut", "settings switch",
	// "quick settings", "tray menu", "touchpad double press").
	public void SetDesktopMode(bool on, string source)
	{
		if (on == DesktopModeOn)
		{
			return;
		}
		_desktopSource = source;
		DesktopModeOn = on;
		if (on && source == TouchpadDoublePressSource)
		{
			DesktopGestureUsed?.Invoke();
		}
	}

	partial void OnDesktopModeOnChanged(bool value)
	{
		string? source = _desktopSource;
		bool auto = _desktopAuto;
		string? game = _desktopOffGame;
		string exe = _desktopOffExe;
		_desktopSource = null;
		_desktopAuto = false;
		_desktopOffGame = null;
		_desktopOffExe = string.Empty;
		if (value)
		{
			_desktopOnAt = Clock();
			_desktopLogged.Clear();
		}
		if (auto && !value)
		{
			DesktopAutoOff?.Invoke(game, exe);
			return;
		}
		AppLog.Write($"mouse mode: {(value ? "on" : "off")} ({source ?? "app"})");
		// A toast only when it was switched from the controller (PS + Options, the touchpad double press, the PS tap that opened
		// the app): a switch in the app or the tray menu shows the change where it was made.
		if (source is ShortcutSource or TouchpadDoublePressSource or PsOpenSource)
		{
			ShortcutFeedback?.Invoke(value ? Text.DesktopOnTitle() : Text.DesktopOffTitle(), value ? Text.DesktopOnBody() : Text.DesktopOffBody());
		}
		DesktopModeSwitched?.Invoke(value, source ?? "app");
	}

	// The source of a PS + Options press (SetDesktopMode).
	public const string ShortcutSource = "shortcut";

	// The source when going back to a game from the app turns mouse mode off (GameReturn): the controller is the game's
	// again. Logged like any change; no toast (the game coming back is the feedback).
	public const string BackToGameSource = "back to game";

	// The source when a PS tap brought the app's window to the front ("PS: Open The App", OpenWithPs).
	public const string PsOpenSource = "ps opened the app";

	// Mouse mode was turned on or off by the user, and from where (SetDesktopMode's source; not the automatic switch-off).
	// Raised on the UI thread.
	public event Action<bool, string>? DesktopModeSwitched;

	// True while a PS tap is bringing the app's window up (OpenWithPs): Mouse Mode comes on right after, so the touchpad tip
	// does not show for this activation (TipBar).
	public bool PsOpeningApp { get; private set; }

	// A PS tap opens the app ("PS: Open The App"): show runs the window up, inFront then tells whether it really is the
	// foreground window. In front, Mouse Mode comes on (with the usual "Mouse Mode On" toast) so the controller drives the
	// app at once; not when it is on already (no second toast), when Windows kept another window in front (a game in front
	// would lose the controller), or on the controller page's Test or Remap tab (the user is trying the buttons there).
	// No fight with the automatic switch-off: the app in front is never a possible game, nor a known game coming to the
	// front, and every way back to a game (the PS tap, Back To Game) turns it off again. UI thread.
	public void OpenWithPs(Action show, Func<bool> inFront)
	{
		PsOpeningApp = true;
		try
		{
			show();
		}
		finally
		{
			PsOpeningApp = false;
		}
		if (DesktopModeOn)
		{
			return;
		}
		if (!inFront())
		{
			AppLog.Write("mouse mode: stays off, PS opened the app but Windows kept another window in front");
			return;
		}
		if (_service.ButtonPageShown)
		{
			AppLog.Write("mouse mode: stays off, PS opened the app on the controller's Test or Remap tab");
			return;
		}
		SetDesktopMode(true, PsOpenSource);
	}

	private void InitDesktop()
	{
		_desktopGameKey = _service.Games.ActiveGame?.Key;
		_service.Games.StateChanged += _ => RunOnUi(CheckDesktopAutoOff);
		_service.GameUsingController += (_, use) =>
		{
			// Raised for every rumble start while playing: only mouse mode on needs the UI thread.
			if (DesktopModeOn)
			{
				RunOnUi(() => OnGameUse(use));
			}
		};
		_service.ForegroundChanged += (_, _) =>
		{
			if (DesktopModeOn)
			{
				RunOnUi(OnForegroundChanged);
			}
		};
		_service.StateChanged += (_, _) => RunOnUi(CheckDesktopAtStart);
	}

	// A known game that just came to the front turns desktop mode off (when the option is on). A game already in front
	// when the user turned desktop mode on doesn't.
	public void CheckDesktopAutoOff()
	{
		ForegroundGame? game = _service.Games.ActiveGame;
		string? key = game?.Key;
		if (string.Equals(key, _desktopGameKey, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		_desktopGameKey = key;
		if (game == null || !DesktopModeOn)
		{
			return;
		}
		if (!DesktopMode.AutoOffInGame)
		{
			AppLog.Write($"mouse mode: stays on, {game.Name} came to the front ({game.Exe}) but turning off in games is off");
			return;
		}
		AppLog.Write($"mouse mode: switched off automatically, {game.Name} came to the front ({game.Exe})");
		SwitchOffForGame(ForegroundGame.DisplayName(game.Exe), game.Exe);
	}

	// A program with the virtual pad open started using the controller as a game does: mouse mode goes off when the option
	// is on, it was turned on at least GameUseGrace ago, and the foreground program could be a game (not this app, not the
	// desktop or a desktop app: the player who left a running game for the browser, or opened the app, keeps the pointer).
	public void OnGameUse(GameUse use)
	{
		if (DesktopModeOn && GameMayTakeController(use, "started using the controller") is { } what)
		{
			AppLog.Write($"mouse mode: switched off automatically, the game started using the controller ({what})");
			SwitchOffForGame(null, what);
		}
	}

	// The foreground changed (game tick, Mouse Mode on): a program that could be a game came to the front while a game is
	// already using the controller (a trigger effect held, motors running, haptics sound): mouse mode goes off as for a
	// new use. This also catches signals that were already on when mouse mode was turned on or arrived in the grace time.
	public void OnForegroundChanged()
	{
		if (!DesktopModeOn || _service.PossibleGameInFront == null || _service.GameUsingControllerNow is not { } use)
		{
			return;
		}
		if (GameMayTakeController(use, "is using the controller") is { } what)
		{
			AppLog.Write($"mouse mode: switched off automatically, {_service.PossibleGameInFront} came to the front while the game uses the controller ({what})");
			SwitchOffForGame(null, what);
		}
	}

	// The log name of the use when a game's use of the controller may switch mouse mode off now; null (and one log line per
	// mouse mode session and reason) when it may not.
	private string? GameMayTakeController(GameUse use, string doing)
	{
		string what = use.LogName();
		if (Clock() - _desktopOnAt < GameUseGrace.TotalMilliseconds)
		{
			LogDesktopOnce("grace", $"mouse mode: stays on, the game used the controller ({what}) right after mouse mode was turned on");
			return null;
		}
		if (_service.PossibleGameInFront == null)
		{
			LogDesktopOnce("not in front", $"mouse mode: stays on, the game {doing} ({what}) but no game is in front");
			return null;
		}
		if (!DesktopMode.AutoOffInGame)
		{
			LogDesktopOnce("option off", $"mouse mode: stays on, the game {doing} ({what}) but turning off in games is off");
			return null;
		}
		return what;
	}

	// Reasons already logged in this mouse mode session (cleared when it is turned on).
	private readonly HashSet<string> _desktopLogged = new(StringComparer.Ordinal);

	private void LogDesktopOnce(string reason, string line)
	{
		if (_desktopLogged.Add(reason))
		{
			AppLog.Write(line);
		}
	}

	private void SwitchOffForGame(string? game, string exe)
	{
		_desktopAuto = true;
		_desktopOffGame = game;
		_desktopOffExe = exe;
		DesktopModeOn = false;
	}

	// One line when the bridge starts (or the controller connects to a running bridge) with mouse mode on, so a session
	// log always shows that games get no buttons or sticks.
	private void CheckDesktopAtStart()
	{
		bool running = _service.IsBridgeRunning && _service.IsConnected;
		if (running && !_desktopBridgeRunning && DesktopModeOn)
		{
			AppLog.Write("mouse mode: on at start");
		}
		_desktopBridgeRunning = running;
	}
}
