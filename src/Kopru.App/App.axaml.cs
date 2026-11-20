// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;

namespace Kopru.App;

public partial class App : Application
{
	private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && OperatingSystem.IsLinux())
		{
			StartDesktop(desktop);
		}
		base.OnFrameworkInitializationCompleted();
	}

	// The Linux app (experimental): the Linux parts come from LinuxAppServices; what Linux does not have yet is hidden in
	// the views (AppFeatures).
	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	private void StartDesktop(IClassicDesktopStyleApplicationLifetime desktop)
	{
		// One line of background work counts every ten minutes and one at exit (see BackgroundWork).
		Kopru.Core.Diagnostics.BackgroundWork.StartReporting();
		Kopru.Core.Platform.IPlatform platform = Kopru.Core.Platform.PlatformServices.Current;
		Kopru.Core.Hiding.IControllerHiding hiding = platform.CreateHiding();
		// Undo what a crashed run left behind, then register early (no-ops while Linux has no hiding). Undone in the reverse
		// order on exit (RealControllerService.ShutdownAsync).
		_ = Task.Run(() => HidingLifecycle.StartAsync(hiding));
		var host = Kopru.Core.Bridge.BridgeHost.Create(platform);
		// Up to four controllers: host is slot 1, the manager adds the others and is the game mode's bridge.
		var manager = Kopru.Core.Bridge.BridgeManager.Create(platform, host);
		var catalog = Kopru.Core.Games.GameCatalog.Load(Kopru.Core.AppPaths.GameList);
		// The running Steam game (by its app id); Linux has no foreground window to read.
		var games = new Kopru.Core.Games.GameModeController(catalog, new Kopru.Core.Games.GameWatcher(catalog, LinuxAppServices.CreateProcessSource()),
			manager);
		// Unplug virtual pads a crashed run left attached (every slot); attaches wait for this (BridgeHostRecovery).
		_ = Task.Run(() => manager.CleanupStaleAsync());
		string adapterResult = Path.Combine(Kopru.Core.AppPaths.DataDirectory, Kopru.Core.Diagnostics.DiagnosticsExport.AdapterResultFileName);
		var service = new RealControllerService(host, hiding, platform.CreateControllerFinder(), games: games, adapterResultPath: adapterResult,
			healthProbe: LinuxAppServices.CreateHealthProbe(adapterResult), batteryStore: Kopru.Core.Health.LastBatteryStore.CreateDefault(),
			audioCapture: platform.CreateAudioCapture());
		service.UseManager(manager);
		service.TurnOnBluetooth = LinuxAppServices.TurnOnBluetoothAsync;
		IStartupRegistration startup = new XdgAutostartRegistration();
		// First start of another version: settings.json is copied before this version saves anything.
		Kopru.Core.Updates.SettingsBackup.Run(Kopru.Core.Updates.PhysicalUpdateFiles.Instance, Kopru.Core.AppPaths.Settings,
			Kopru.Core.Updates.AppVersion.Parse(AppInfo.Version)!);
		// Game pictures: downloads follow the setting (read when a row loads; the view model exists by then).
		MainWindowViewModel? created = null;
		var images = new GameImageService(Kopru.Core.Games.GameImageResolver.CreateDefault(() => created?.Settings.DownloadGameImages == true));
		var environment = new AppEnvironment(JsonSettingsStore.CreateDefault(), startup, LinuxAppServices.CreateWizard, FullScreenDetector.IsForegroundFullScreen,
			GameList: Kopru.Core.Games.GameCatalogUpdater.CreateDefault(), GameImages: images,
			InstalledGames: new Kopru.Core.Games.InstalledGames(Kopru.Core.Games.InstalledGameFinder.CreateDefault()));
		var vm = new MainWindowViewModel(service, environment);
		created = vm;
		vm.Settings.MarkRunVersion(AppInfo.Version);
		if (vm.Settings.StartWithWindows)
		{
			// Points the autostart entry at this copy (the folder may have moved).
			try
			{
				startup.Set(true);
			}
			catch (Exception e) when (e is UnauthorizedAccessException or IOException)
			{
				Kopru.Core.Diagnostics.AppLog.Write("startup: could not refresh the autostart entry (" + e.Message + ")");
			}
		}

		var window = new MainWindow { DataContext = vm };
		window.FitToScreen();
		// "Copy Commands" on the setup page.
		SetupViewModel.CopyText = text => window.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
		// Shows the window in front also from the background (PS tap, second start, toast) and hides it again. The window's
		// own active state stands in for the foreground window.
		var active = new AvaloniaForegroundWindows(window);
		active.Attach();
		var presenter = new MainWindowPresenter(new AvaloniaPresentedWindow(window), active);
		var shell = new DesktopShell(desktop, window, vm, presenter);
		vm.ExitApp = shell.Exit;
		// "Back To Game": hide the window, bring the game back.
		vm.BackToGame.Return = (reason, game) =>
		{
			if (!shell.IsExiting)
			{
				presenter.ReturnTo(reason, game);
			}
		};
		// For the PS tap ("PS: Open The App"), read on the input thread.
		service.IsAppInFront = () => presenter.IsInFront;
		service.FullScreenForeground = FullScreenDetector.ForegroundFullScreenApp;
		// A second start (the menu, the executable again) shows this copy's window on its current page; that copy then exits.
		Program.Instance?.Listen(() => ShowFromOtherStart(shell));
		var toasts = new ToastHost(page => shell.ShowMainWindow(page), FullScreenDetector.IsForegroundFullScreen);
		vm.Notifications.Raised += toasts.Show;
		// StatusNotifierItem over D-Bus where the desktop has it; without it the app still works (a second start from the
		// menu shows the window again).
		try
		{
			CreateTrayIcon(new TrayViewModel(service, vm.Settings, shell), shell);
		}
		catch (Exception e)
		{
			Kopru.Core.Diagnostics.AppLog.Write("tray: no tray icon (" + e.Message + ")");
		}
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
				// Exiting anyway.
			}
			Kopru.Core.Diagnostics.BackgroundWork.StopReporting();
		}
		// The app keeps running in the background: closing the window hides it unless "Keep Running In Background" is off.
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
		bool minimized = desktop.Args?.Contains(XdgAutostartRegistration.MinimizedArgument, StringComparer.OrdinalIgnoreCase) == true;
		if (!minimized)
		{
			desktop.MainWindow = window;
		}
	}

	// Runs on a thread-pool thread; waits for the window so the other start's "shown" answer is true.
	private static void ShowFromOtherStart(DesktopShell shell)
	{
		try
		{
			Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
			{
				if (!shell.IsExiting)
				{
					shell.ShowMainWindow();
				}
			});
		}
		catch (Exception e) when (e is InvalidOperationException or TaskCanceledException or OperationCanceledException)
		{
			// The app is shutting down.
		}
	}

	// No Mouse Mode item: Linux has no input sink yet.
	private void CreateTrayIcon(TrayViewModel tray, DesktopShell shell)
	{
		NativeMenuItem open = new() { Command = tray.OpenCommand };
		NativeMenuItem bridge = new() { Command = tray.ToggleBridgeCommand };
		NativeMenuItem powerOff = new() { Command = tray.PowerOffCommand };
		NativeMenuItem reconnect = new() { Command = tray.ReconnectCommand };
		NativeMenuItem notifications = new() { Command = tray.ToggleNotificationsCommand };
		NativeMenuItem exit = new() { Command = tray.ExitCommand };
		NativeMenu menu = [open, new NativeMenuItemSeparator(), bridge, reconnect, powerOff, new NativeMenuItemSeparator(), notifications,
			new NativeMenuItemSeparator(), exit];
		var icon = new TrayIcon
		{
			Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Kopru.App/Assets/icon-256.png"))),
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
	private sealed class DesktopShell(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainWindowViewModel vm,
		MainWindowPresenter presenter) : IAppShell
	{
		public bool IsExiting { get; private set; }

		public event Action? Exiting;

		public void ShowMainWindow(AppPage? page = null)
		{
			if (IsExiting)
			{
				return;
			}
			if (page is { } p)
			{
				vm.Navigate(p);
			}
			desktop.MainWindow ??= window;
			presenter.Show(page is { } shown ? "page " + shown : "open");
		}

		public void HideMainWindow()
		{
			if (!IsExiting)
			{
				presenter.Hide("PS tap");
			}
		}

		public bool IsMainWindowInFront => !IsExiting && presenter.IsInFront;

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
