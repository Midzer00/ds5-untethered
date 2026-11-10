// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Setup;

namespace Kopru.App;

public partial class App : Application
{
	private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && OperatingSystem.IsWindows())
		{
			StartDesktop(desktop);
		}
		base.OnFrameworkInitializationCompleted();
	}

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private void StartDesktop(IClassicDesktopStyleApplicationLifetime desktop)
	{
		HidHideHidingService hiding = HidHideHidingService.CreateDefault();
		// Undo HidHide changes a crashed run left behind, then allow our exe early (a controller hidden by another
		// tool stays visible to us). Undone in the reverse order on exit (RealControllerService.ShutdownAsync).
		_ = Task.Run(() => HidingLifecycle.StartAsync(hiding));
		var host = Kopru.Core.Bridge.BridgeHost.CreateDefault();
		// Up to four controllers: host is slot 1, the manager adds the others and is the game mode's bridge.
		var manager = Kopru.Core.Bridge.BridgeManager.CreateDefault(host);
		var catalog = Kopru.Core.Games.GameCatalog.Load(Kopru.Core.AppPaths.GameList);
		var games = new Kopru.Core.Games.GameModeController(catalog,
			new Kopru.Core.Games.GameWatcher(catalog, steam: Kopru.Core.Steam.SteamLibrary.CreateDefault()), manager);
		// Unplug virtual pads a crashed run left attached (every slot); attaches wait for this (BridgeHostRecovery).
		_ = Task.Run(() => manager.CleanupStaleAsync());
		// Tear the bridge down before sleep and start it again after resume.
		if (Kopru.Core.Bridge.WindowsPowerEvents.TryCreate() is { } powerEvents)
		{
			_ = new Kopru.Core.Bridge.BridgeSleepGuard(powerEvents, host);
		}
		var service = new RealControllerService(host, hiding, new HidControllerFinder(), games: games,
			steamCheck: Kopru.Core.Steam.SteamInputCheck.CreateDefault().Check,
			adapterResultPath: Path.Combine(Kopru.Core.AppPaths.DataDirectory, Kopru.Core.Diagnostics.DiagnosticsExport.AdapterResultFileName),
			healthProbe: Kopru.Core.Health.HealthProbe.CreateDefault(), batteryStore: Kopru.Core.Health.LastBatteryStore.CreateDefault());
		service.UseManager(manager);
		var startup = new RunKeyStartupRegistration();
		var environment = new AppEnvironment(JsonSettingsStore.CreateDefault(), startup,
			() => new SetupWizard(SystemCheck.CreateDefault(), new ShellUrlLauncher()), FullScreenDetector.IsForegroundFullScreen,
			GameList: Kopru.Core.Games.GameCatalogUpdater.CreateDefault());
		var vm = new MainWindowViewModel(service, environment);
		if (vm.Settings.StartWithWindows)
		{
			// Older versions registered the exe without --minimized.
			try
			{
				startup.Set(true);
			}
			catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
			{
				// Keeps the old entry.
			}
		}

		var window = new MainWindow { DataContext = vm };
		window.FitToScreen();
		var shell = new DesktopShell(desktop, window, vm);
		var toasts = new ToastHost(page => shell.ShowMainWindow(page), FullScreenDetector.IsForegroundFullScreen);
		vm.Notifications.Raised += toasts.Show;
		CreateTrayIcon(new TrayViewModel(service, vm.Settings, shell), shell);
		// Starts the bridge once a controller is connected and nothing blocks it ("Start The Bridge Automatically").
		vm.AttachAutoStart();

		int shutDown = 0;
		void Shutdown()
		{
			if (Interlocked.Exchange(ref shutDown, 1) != 0)
			{
				return;
			}
			vm.Settings.SaveNow();
			try
			{
				Task.Run(service.ShutdownAsync).Wait(ShutdownTimeout);
			}
			catch (AggregateException)
			{
				// Exiting anyway; the next start recovers the hiding state.
			}
		}
		// The app lives in the tray: closing the window hides it unless "keep running in tray" is off.
		desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
		window.Closing += (_, e) =>
		{
			if (vm.HideToTrayOnClose(shell.IsExiting || e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown))
			{
				e.Cancel = true;
				window.Hide();
			}
			else
			{
				shell.Exit();
			}
		};
		desktop.Exit += (_, _) => Shutdown();
		AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
		bool minimized = desktop.Args?.Contains(RunKeyStartupRegistration.MinimizedArgument, StringComparer.OrdinalIgnoreCase) == true;
		if (!minimized)
		{
			desktop.MainWindow = window;
		}
	}

	private void CreateTrayIcon(TrayViewModel tray, DesktopShell shell)
	{
		NativeMenuItem open = new() { Command = tray.OpenCommand };
		NativeMenuItem bridge = new() { Command = tray.ToggleBridgeCommand };
		NativeMenuItem powerOff = new() { Command = tray.PowerOffCommand };
		NativeMenuItem reconnect = new() { Command = tray.ReconnectCommand };
		NativeMenuItem supportedMode = new() { Command = tray.ToggleSupportedModeCommand, ToggleType = NativeMenuItemToggleType.CheckBox };
		NativeMenuItem desktopMode = new() { Command = tray.ToggleDesktopModeCommand, ToggleType = NativeMenuItemToggleType.CheckBox };
		NativeMenuItem notifications = new() { Command = tray.ToggleNotificationsCommand };
		NativeMenuItem exit = new() { Command = tray.ExitCommand };
		NativeMenu menu = [open, new NativeMenuItemSeparator(), bridge, reconnect, powerOff, new NativeMenuItemSeparator(), supportedMode, desktopMode, notifications,
			new NativeMenuItemSeparator(), exit];
		var icon = new TrayIcon
		{
			Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Kopru.App/Assets/icon.ico"))),
			Menu = menu,
			IsVisible = true,
		};
		void Refresh()
		{
			icon.ToolTipText = tray.ToolTip;
			open.Header = tray.OpenText;
			bridge.Header = tray.BridgeText;
			powerOff.Header = tray.PowerOffText;
			powerOff.IsEnabled = tray.CanPowerOff;
			reconnect.Header = tray.ReconnectText;
			reconnect.IsEnabled = tray.CanReconnect;
			supportedMode.Header = tray.SupportedModeText;
			supportedMode.IsChecked = tray.IsSupportedModeOn;
			desktopMode.Header = tray.DesktopModeText;
			desktopMode.IsChecked = tray.IsDesktopModeOn;
			notifications.Header = tray.NotificationsText;
			exit.Header = tray.ExitText;
		}
		Refresh();
		tray.PropertyChanged += (_, _) => Refresh();
		icon.Clicked += (_, _) => shell.ShowMainWindow();
		TrayIcon.SetIcons(this, [icon]);
		shell.Exiting += () => icon.IsVisible = false;
	}

	// The running app as seen by the tray and the toasts.
	private sealed class DesktopShell(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainWindowViewModel vm) : IAppShell
	{
		public bool IsExiting { get; private set; }

		public event Action? Exiting;

		public void ShowMainWindow(AppPage? page = null)
		{
			if (page is { } p)
			{
				vm.Navigate(p);
			}
			desktop.MainWindow ??= window;
			window.Show();
			if (window.WindowState == WindowState.Minimized)
			{
				window.WindowState = WindowState.Normal;
			}
			window.Activate();
		}

		public void Exit()
		{
			if (IsExiting)
			{
				return;
			}
			IsExiting = true;
			Exiting?.Invoke();
			desktop.Shutdown();
		}
	}
}
