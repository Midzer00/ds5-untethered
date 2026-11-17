// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// What the tray menu and notifications ask of the running app (the window, exiting).
public interface IAppShell
{
	// Shows, restores and focuses the main window, optionally on a page.
	void ShowMainWindow(AppPage? page = null);

	// Hides the main window like closing it does (to the icon next to the clock; the app keeps running).
	void HideMainWindow()
	{
	}

	// Whether the main window is the foreground window now (after ShowMainWindow: Windows may keep another window in front).
	bool IsMainWindowInFront => true;

	// Exits for real (stops the bridge and undoes the hiding).
	void Exit();
}

// The tray icon: tooltip and menu (open, start/stop bridge, turn off controller, the supported mode switch, notifications
// on/off, exit).
public sealed partial class TrayViewModel : ViewModelBase
{
	private readonly IControllerService _service;
	private readonly SettingsState _settings;
	private readonly IAppShell _shell;
	private readonly ShownProperties _shown;

	public TrayViewModel(IControllerService service, SettingsState settings, IAppShell shell)
	{
		_service = service;
		_settings = settings;
		_shell = shell;
		// The tray rebuilds its tooltip and menu on any change: one whole refresh, and only when something it shows changed.
		_shown = new ShownProperties(_ => { }, (nameof(ToolTip), () => ToolTip), (nameof(BridgeText), () => BridgeText),
			(nameof(CanReconnect), () => CanReconnect), (nameof(CanPowerOff), () => CanPowerOff));
		_service.StateChanged += (_, _) => RunOnUi(() =>
		{
			if (_shown.Refresh() > 0)
			{
				OnPropertyChanged(string.Empty);
			}
		});
		// "PS: Open The App": the controller's PS tap shows the window like the tray icon does (on its current page), with
		// Mouse Mode on once it is in front (SettingsState.OpenWithPs), and hides it again when it is already in front.
		_service.Shortcut += (_, action) =>
		{
			if (action == Kopru.Core.Input.ShortcutAction.OpenApp)
			{
				RunOnUi(() => _settings.OpenWithPs(() => _shell.ShowMainWindow(), () => _shell.IsMainWindowInFront));
			}
			else if (action == Kopru.Core.Input.ShortcutAction.HideApp)
			{
				RunOnUi(_shell.HideMainWindow);
			}
		};
		_settings.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(SettingsState.NotificationsEnabled))
			{
				OnPropertyChanged(nameof(NotificationsText));
			}
			else if (e.PropertyName == nameof(SettingsState.DesktopModeOn))
			{
				OnPropertyChanged(nameof(IsDesktopModeOn));
			}
		};
	}

	public string ToolTip
	{
		get
		{
			string state = !_service.IsConnected ? Text.TrayNoController()
				: _service.IsGameConnected ? Text.BridgeStateRunning()
				: _service.IsBridgeRunning ? Text.BridgeStateWaiting()
				: Text.BridgeStateStopped();
			string text = AppInfo.Name + " · " + state;
			return _service.IsConnected && _service.HasBattery ? text + " · " + Text.TrayBattery(Math.Round(_service.BatteryPercent)) : text;
		}
	}

	public string OpenText => Text.TrayOpen();
	public string BridgeText => _service.IsBridgeRunning ? Text.BridgeStop() : Text.BridgeStart();
	public string PowerOffText => Text.TurnOffController();
	public string ReconnectText => Text.ReconnectController();
	public bool CanReconnect => _service.IsConnected || _service.Details != null;
	public bool CanPowerOff => _service.IsConnected && _service.IsBridgeRunning;
	public string NotificationsText => _settings.NotificationsEnabled ? Text.TrayNotificationsOff() : Text.TrayNotificationsOn();
	public string DesktopModeText => Text.TrayDesktopMode();
	public bool IsDesktopModeOn => _settings.DesktopModeOn;
	public string ExitText => Text.TrayExit();

	[RelayCommand]
	private void Open() => _shell.ShowMainWindow();

	[RelayCommand]
	private Task ToggleBridge() => _service.IsBridgeRunning ? _service.StopBridgeAsync() : _service.StartBridgeAsync();

	[RelayCommand]
	private Task PowerOff() => _service.PowerOffAsync();

	[RelayCommand]
	private Task Reconnect() => _service.ReconnectAsync();

	[RelayCommand]
	private void ToggleDesktopMode() => _settings.SetDesktopMode(!_settings.DesktopModeOn, "tray menu");

	[RelayCommand]
	private void ToggleNotifications() => _settings.NotificationsEnabled = !_settings.NotificationsEnabled;

	[RelayCommand]
	private void Exit() => _shell.Exit();
}
