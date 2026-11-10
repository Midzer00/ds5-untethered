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

	public TrayViewModel(IControllerService service, SettingsState settings, IAppShell shell)
	{
		_service = service;
		_settings = settings;
		_shell = shell;
		_service.StateChanged += (_, _) => RunOnUi(() => OnPropertyChanged(string.Empty));
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
	public string SupportedModeText => Text.TraySupportedMode();
	public bool IsSupportedModeOn => _service.Games.IsSupportedModeOn;
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
	private void ToggleSupportedMode()
	{
		_service.Games.SetSupportedMode(!_service.Games.IsSupportedModeOn);
		OnPropertyChanged(nameof(IsSupportedModeOn));
	}

	[RelayCommand]
	private void ToggleDesktopMode() => _settings.DesktopModeOn = !_settings.DesktopModeOn;

	[RelayCommand]
	private void ToggleNotifications() => _settings.NotificationsEnabled = !_settings.NotificationsEnabled;

	[RelayCommand]
	private void Exit() => _shell.Exit();
}
