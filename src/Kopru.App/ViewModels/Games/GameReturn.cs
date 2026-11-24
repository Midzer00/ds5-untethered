// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Diagnostics;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// The way back to a running game from the main window: the "Back To Game" button in the Overview's header (beside "Turn
// Off Controller", while a known game runs), and Mouse Mode turned off with PS + Options while the window is in front
// after PS brought it up over a game. Both hide the window and bring the game back (Return: the app's MainWindowPresenter,
// the same path as the PS tap). Every way back (these two and the PS tap from the app while a game runs) turns Mouse Mode
// off: the controller is the game's again. UI thread, except the shortcut handler (input thread), which only reads the
// game state and posts.
public sealed partial class GameReturn : ViewModelBase
{
	private readonly IControllerService _service;
	private readonly SettingsState _settings;
	private readonly Func<bool> _fullScreen;
	private bool _windowActive;
	// PS brought the window up over a game (a known game or a full-screen program in front), until the window leaves the front.
	private bool _openedOverGame;

	public GameReturn(IControllerService service, SettingsState settings, Func<bool>? fullScreen = null)
	{
		_service = service;
		_settings = settings;
		_fullScreen = fullScreen ?? (() => false);
		service.Shortcut += (_, action) =>
		{
			if (action == ShortcutAction.OpenApp)
			{
				// What RoutePsTap saw: read now, before the window covers the game.
				bool over = service.Games.IsGameInFront || SafeFullScreen();
				RunOnUi(() => OnPsOpened(over));
			}
			else if (action == ShortcutAction.HideApp)
			{
				RunOnUi(OnPsHid);
			}
		};
		settings.DesktopModeSwitched += (on, source) =>
		{
			if (!on && source == SettingsState.ShortcutSource && _windowActive && _openedOverGame)
			{
				ReturnNow("mouse mode turned off with PS + Options");
			}
		};
		service.Games.StateChanged += _ => RunOnUi(Refresh);
	}

	// Hides the main window and brings the game back (reason for the log, the game's process id or 0); set by the app.
	public Action<string, int>? Return { get; set; }

	// A known game runs: the Overview shows the button.
	public bool IsGameRunning => GameName != null;

	public string Label => Text.BackToGame();

	public string ToolTip => GameName is { } name ? Text.BackToGameTip(name) : Label;

	// The running game's name (shown above the button), or null.
	public string? GameName => _service.Games.ActiveGame?.Name;

	// The main window became the foreground window or left it (set by the window).
	public void SetWindowActive(bool active)
	{
		if (active == _windowActive)
		{
			return;
		}
		_windowActive = active;
		if (!active)
		{
			_openedOverGame = false;
		}
		Refresh();
	}

	[RelayCommand]
	private void Back() => ReturnNow("back to game button");

	private void OnPsOpened(bool overGame)
	{
		_openedOverGame = overGame;
	}

	private void OnPsHid()
	{
		// The PS tap took the player back to the game: the controller is the game's again.
		if (_openedOverGame || _service.Games.ActiveGame != null)
		{
			_settings.SetDesktopMode(false, SettingsState.BackToGameSource);
		}
		_openedOverGame = false;
	}

	private void ReturnNow(string reason)
	{
		_openedOverGame = false;
		int process = _service.Games.ActiveGame?.ProcessId ?? 0;
		AppLog.Write($"back to game: {reason}{(GameName is { } name ? " (" + name + ")" : string.Empty)}");
		_settings.SetDesktopMode(false, SettingsState.BackToGameSource);
		Return?.Invoke(reason, process);
	}

	// "Oyunu Kapat" in the Overview's header (left of "Kumandayı Kapat"), only while a game is detected as running: the same
	// two steps as the game panel's (GamePanelViewModel.CloseGame). The game's main window is asked to close; only when the
	// game is still there after GamePanelViewModel.CloseWait the button's place asks whether to end it (Force Close, the only
	// destructive step, or Cancel). Only the detected active game is ever touched: the closer checks the key against
	// GameModeController's active game. Never offered without a closer (set by the main window's view model).
	public IGameCloser? Closer
	{
		get => _closer;
		set
		{
			_closer = value;
			OnPropertyChanged(nameof(ShowsCloseGame));
		}
	}

	private IGameCloser? _closer;
	private int _closeRound;

	// The wait after the close request (tests pass their own).
	public Func<TimeSpan, Task> Delay { get; set; } = Task.Delay;

	[CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
	[CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(ShowsCloseGame), nameof(IsClosing), nameof(ConfirmsForceClose))]
	private CloseGameStep _closeStep;

	public bool ShowsCloseGame => IsGameRunning && _closer != null && CloseStep == CloseGameStep.None;
	public bool IsClosing => IsGameRunning && CloseStep == CloseGameStep.Waiting;
	public bool ConfirmsForceClose => IsGameRunning && CloseStep == CloseGameStep.Confirm;
	public string CloseGameText => Text.CloseGame();
	public string CloseGameTip => GameName is { } name ? GameModeText.Join(Text.CloseGame(), name) : Text.CloseGame();
	public string ClosingText => Text.ClosingGame();
	public string ForceCloseQuestion => Text.ForceCloseQuestion();
	public string ForceCloseText => Text.ForceClose();
	public string ForceCloseAskText => Text.ForceCloseAsk();
	public string CancelText => Text.ProfileCancel();

	private void ResetClose()
	{
		_closeRound++;
		CloseStep = CloseGameStep.None;
	}

	[RelayCommand]
	private async Task CloseGame()
	{
		if (_closer == null || _service.Games.ActiveGame is not { } game || CloseStep != CloseGameStep.None)
		{
			return;
		}
		int round = ++_closeRound;
		CloseStep = CloseGameStep.Waiting;
		AppLog.Write($"close game asked from the overview ({game.Name})");
		if (!_closer.RequestClose(game.Key))
		{
			// Nothing to ask (no window): straight to the question.
			CloseStep = CloseGameStep.Confirm;
			return;
		}
		await Delay(GamePanelViewModel.CloseWait);
		if (round != _closeRound)
		{
			return;
		}
		CloseStep = string.Equals(_service.Games.ActiveGame?.Key, game.Key, StringComparison.OrdinalIgnoreCase) ? CloseGameStep.Confirm : CloseGameStep.None;
	}

	[RelayCommand]
	private void ForceClose()
	{
		if (_closer != null && _service.Games.ActiveGame is { } game && CloseStep == CloseGameStep.Confirm)
		{
			_closer.End(game.Key);
		}
		ResetClose();
	}

	[RelayCommand]
	private void KeepGame() => ResetClose();

	private void Refresh()
	{
		// The game ended (by itself or after the request): nothing left to close or ask.
		if (!IsGameRunning && CloseStep != CloseGameStep.None)
		{
			ResetClose();
		}
		OnPropertyChanged(nameof(IsGameRunning));
		OnPropertyChanged(nameof(ToolTip));
		OnPropertyChanged(nameof(GameName));
		OnPropertyChanged(nameof(ShowsCloseGame));
		OnPropertyChanged(nameof(IsClosing));
		OnPropertyChanged(nameof(ConfirmsForceClose));
		OnPropertyChanged(nameof(CloseGameTip));
	}

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
