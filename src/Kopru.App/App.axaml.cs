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
		// Keeps Windows from making a virtual controller the default speaker or microphone (every slot, every attach); the
		// sound device pickers and the mic button on a PC microphone use the same Windows audio access.
		var windowsAudio = new Kopru.Core.Audio.WindowsAudioDefaults();
		var audioGuard = Kopru.Core.Audio.DefaultAudioGuard.CreateDefault(windowsAudio);
		var audioDevices = Kopru.Core.Audio.AudioDevices.CreateDefault(windowsAudio);
		if (audioDevices != null)
		{
			audioDevices.Guard = audioGuard;
			// The default microphone's mute switch has no notification here: read again every second (one Core Audio call).
			_ = audioDevices.PollMute(TimeSpan.FromSeconds(1));
		}
		foreach (Kopru.Core.Bridge.BridgeHost slotHost in manager.Hosts)
		{
			slotHost.PadAudio = audioGuard;
		}
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
		service.AudioDevices = audioDevices;
		var startup = new RunKeyStartupRegistration();
		// First start of another version: settings.json is copied before this version saves anything.
		Kopru.Core.Updates.SettingsBackup.Run(Kopru.Core.Updates.PhysicalUpdateFiles.Instance, Kopru.Core.AppPaths.Settings,
			Kopru.Core.Updates.AppVersion.Parse(AppInfo.Version)!);
		var downloader = Kopru.Core.Updates.UpdateDownloader.CreateDefault();
		// Installers from an earlier update are no longer needed.
		_ = Task.Run(downloader.CleanUp);
		var updates = new UpdateServices(Kopru.Core.Updates.UpdateChecker.CreateDefault(), downloader, Kopru.Core.Updates.InnoInstallKind.CreateDefault(),
			new ShellUrlLauncher(), AppInfo.Version);
		// Game pictures: downloads follow the setting (read when a row loads; the view model exists by then).
		MainWindowViewModel? created = null;
		var images = new GameImageService(Kopru.Core.Games.GameImageResolver.CreateDefault(() => created?.Settings.DownloadGameImages == true));
		var environment = new AppEnvironment(JsonSettingsStore.CreateDefault(), startup,
			() => new SetupWizard(SystemCheck.CreateDefault(), new ShellUrlLauncher()), FullScreenDetector.IsForegroundFullScreen,
			GameList: Kopru.Core.Games.GameCatalogUpdater.CreateDefault(), GameImages: images, Restart: RestartServices.CreateDefault(),
			InstalledGames: new Kopru.Core.Games.InstalledGames(Kopru.Core.Games.InstalledGameFinder.CreateDefault()), Updates: updates);
		var vm = new MainWindowViewModel(service, environment);
		created = vm;
		vm.Settings.MarkRunVersion(AppInfo.Version);
		if (audioGuard != null)
		{
			vm.Settings.AttachAudioGuard(audioGuard);
		}
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
		// Shows the window in front also from the background (PS tap, second start, toast) and hides it again.
		var presenter = new MainWindowPresenter(new AvaloniaPresentedWindow(window), new Win32ForegroundWindows());
		var shell = new DesktopShell(desktop, window, vm, presenter);
		// After "Restart The Computer" on the setup page: the normal exit (settings saved, bridge and hiding undone).
		vm.ExitApp = shell.Exit;
		// For the PS tap ("PS: Open The App"), read on the input thread: the real foreground window, not the Activated flag.
		service.IsAppInFront = () => presenter.IsInFront;
		service.FullScreenForeground = FullScreenDetector.ForegroundFullScreenApp;
		// A second start (Start menu, the exe again) shows this copy's window on its current page; that copy then exits.
		Program.Instance?.Listen(() => ShowFromOtherStart(shell));
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
		// "Update": the same shutdown as exit (settings saved, virtual controller stopped, hiding undone), then the installer
		// starts and the app exits; the installer waits for this process to end (SingleInstance.HandOverToUpdate).
		var installer = new Kopru.Core.Updates.UpdateInstaller(downloader, new Kopru.Core.Updates.ShellProcessLauncher(), () => Task.Run(Shutdown),
			() => Program.Instance?.HandOverToUpdate(), () => Avalonia.Threading.Dispatcher.UIThread.Post(shell.Exit));
		updates.Install = installer.InstallAsync;
		vm.Updates.StartSchedule();
		desktop.Exit += (_, _) => Shutdown();
		AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
		bool minimized = desktop.Args?.Contains(RunKeyStartupRegistration.MinimizedArgument, StringComparer.OrdinalIgnoreCase) == true;
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
