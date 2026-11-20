// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.App.Services;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.App.ViewModels;

public sealed class NavItem(PageViewModel page, string iconData) : ViewModelBase
{
	public PageViewModel Page { get; } = page;
	public Geometry Icon { get; } = Geometry.Parse(iconData);
	public string Title => Page.Title;
}

// What the app passes in besides the controller service; everything is optional (tests leave it out).
// FullScreen: whether a full-screen app is in front (for "don't notify while playing"). GameList: downloads the supported
// games list (null: no update button); Now: the wall clock for its weekly automatic check. GameImages: the pictures of the
// supported games list (null: placeholders only). InstalledGames: finds the supported games installed on this PC (null:
// nothing counts as installed). Links: opens web pages in the browser (the support link; null: the shell's default browser).
public sealed record AppEnvironment(ISettingsStore? Store = null, IStartupRegistration? Startup = null, Func<SetupWizard>? Wizard = null,
	Func<bool>? FullScreen = null, Func<long>? Clock = null, Kopru.Core.Games.GameCatalogUpdater? GameList = null,
	Func<DateTimeOffset>? Now = null, GameImageService? GameImages = null,
	Kopru.Core.Games.InstalledGames? InstalledGames = null, IUrlLauncher? Links = null);

public sealed partial class MainWindowViewModel : ViewModelBase
{
	private readonly IControllerService _service;
	private readonly AppEnvironment _environment;
	private readonly ShownProperties _shown;

	public MainWindowViewModel(IControllerService service, AppEnvironment? environment = null)
	{
		_service = service;
		_environment = environment ?? new AppEnvironment();
		Settings = new SettingsState(service, _environment.Store, _environment.Startup);
		if (_environment.Clock is { } clock)
		{
			Settings.Clock = clock;
		}
		BackToGame = new GameReturn(service, Settings, _environment.FullScreen);
		Overview = new OverviewViewModel(service, Settings);
		Overview.BackToGame = BackToGame;
		SupportedGames = new SupportedGamesViewModel(service, Settings, _environment.GameList, _environment.Now, _environment.GameImages,
			_environment.InstalledGames) { BackToGame = BackToGame };
		OtherGames = new OtherGamesViewModel(service, Settings);
		Controller = new ControllerPageViewModel(service, Settings);
		Controller.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(ControllerPageViewModel.SelectedTab))
			{
				UpdateButtonPage();
			}
		};
		Light = new LightViewModel(service, Settings);
		SettingsPage = new SettingsViewModel(service, Settings, _environment.Wizard != null ? ShowSetup : null) { Bridge = Overview };
		// Support (GitHub Sponsors): the pill in the sidebar and the link in Settings > General > Updates.
		Support = new SupportLinkViewModel(_environment.Links ?? new ShellUrlLauncher());
		SettingsPage.Support = Support;
		NavItems =
		[
			new(Overview, Icons.Overview),
			new(SupportedGames, Icons.SupportedGames),
			new(OtherGames, Icons.Haptics),
			new(Controller, Icons.Controller),
			new(Light, Icons.Light),
			new(SettingsPage, Icons.Settings),
		];
		_selectedNav = NavItems[0];
		Overview.Attach(_environment.Now);
		Overview.Quick.OpenAdvanced = () => OpenSettingsAt(SettingsViewModel.GeneralTab);
		Overview.Quick.OpenShortcuts = () => OpenSettingsAt(SettingsViewModel.ShortcutsTab);
		_service.FixRequested += (_, fix) => RunOnUi(() => HandleFix(fix));
		UpdateLiveViews();
		Notifications = new NotificationCenter(service, Settings, _environment.FullScreen, _environment.Clock);
		SupportedGames.ListUpdatedAutomatically += added => Notifications.Post(NotificationCenter.GameListUpdated(added));
		// A game that could not be started from the games list: a short toast.
		SupportedGames.LaunchFailed += name => Notifications.Post(new AppNotification(NotificationType.GameDetected, NotificationKind.Error,
			Text.GameLaunchFailedTitle(), Text.GameLaunchFailedBody(name), AppPage.SupportedGames), force: true);
		// Haptics And Triggers' "Desteklenmeyen Oyunlar": the Games page on that view.
		OtherGames.OpenUnsupportedGames = () =>
		{
			SupportedGames.ShowUnsupported = true;
			Navigate(SupportedGames);
		};
		AutoUpdateTask = SupportedGames.AutoUpdateAsync();
		_shown = new ShownProperties(name => OnPropertyChanged(name),
			(nameof(IsEdge), () => IsEdge), (nameof(DeviceName), () => DeviceName), (nameof(DeviceConnection), () => DeviceConnection),
			(nameof(DeviceBattery), () => DeviceBattery), (nameof(IsConnected), () => IsConnected), (nameof(BatteryPercent), () => BatteryPercent),
			(nameof(HasBattery), () => HasBattery), (nameof(IsCharging), () => IsCharging), (nameof(BatteryShort), () => BatteryShort),
			(nameof(BatteryRowText), () => BatteryRowText), (nameof(BatteryRowTip), () => BatteryRowTip));
		// Only the sidebar values the service drives, and only the ones that changed.
		_service.StateChanged += (_, _) => RunOnUi(() =>
		{
			_shown.Refresh();
			Settings.Live.Refresh();
		});
		// The virtual controller connected to games: setup works, whatever the wizard last saw.
		_service.StateChanged += (_, _) => RunOnUi(() =>
		{
			if (_service.IsGameConnected)
			{
				Settings.MarkSetupCompleted();
			}
		});
		OpenSetupUntilCompleted();
	}

	// The setup page opens by itself at every start until the required steps were all done once (SetupCompleted). An
	// older settings file (no SetupCompleted yet) counts as completed when both drivers are ready: its user already went
	// through setup, and a controller that is just off must not bring the page back. Once completed, a missing driver
	// shows the blocking state and its fix button instead.
	private void OpenSetupUntilCompleted()
	{
		if (_environment.Store == null || _environment.Wizard == null || Settings.SetupCompleted == true)
		{
			return;
		}
		ShowSetup(null, autoFinish: true);
		if (Setup is not { } setup)
		{
			return;
		}
		Kopru.Core.Setup.SystemStatus status = setup.Wizard.Status;
		bool olderFileWithDrivers = Settings.SetupCompleted == null && status.UsbIp.Ready && status.Requirements?.All(r => r.Ready) == true;
		if (setup.Wizard.FirstIncomplete == null || olderFileWithDrivers)
		{
			Settings.MarkSetupCompleted();
			Setup = null;
		}
	}

	// Exits the app for real (the shell's exit; set by the app). Used after a restart was requested.
	public Action? ExitApp { get; set; }

	// The setup page while it is open (shown instead of the selected page).
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CurrentPage))]
	private SetupViewModel? _setup;

	// Opens the setup page, scrolled to the given step when there is one.
	public void ShowSetup() => ShowSetup(null);

	// autoFinish (first run): the page goes to the Overview by itself once the required steps are done.
	public void ShowSetup(Kopru.Core.Setup.SetupStepId? focus, bool autoFinish = false)
	{
		if (_environment.Wizard is { } create)
		{
			var setup = new SetupViewModel(create(), _service, CloseSetup, autoFinish) { FocusStep = focus };
			// Finishing the required steps (here or opened from Settings) completes setup for good.
			setup.PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(SetupViewModel.IsComplete) && setup.IsComplete)
				{
					Settings.MarkSetupCompleted();
				}
			};
			Setup = setup;
			_service.RefreshHealth();
		}
	}

	// Leaving the setup page (closed or finished) always lands on the Overview, not on the page it was opened from.
	public void CloseSetup()
	{
		Navigate(Overview);
		Setup = null;
	}

	// The fixes that need a page (IControllerService.FixRequested): the setup wizard's driver or pairing step, the Steam
	// Input panel on the supported games page, the adapter test in Settings.
	public void HandleFix(Kopru.Core.Health.FixAction fix)
	{
		switch (fix)
		{
			case Kopru.Core.Health.FixAction.InstallDriver:
			case Kopru.Core.Health.FixAction.InstallHidHide:
				ShowSetup(Kopru.Core.Setup.SetupStepId.Drivers);
				break;
			case Kopru.Core.Health.FixAction.PairController:
				ShowSetup(Kopru.Core.Setup.SetupStepId.PairController);
				break;
			case Kopru.Core.Health.FixAction.OpenSteamInputHelp:
				Navigate(SupportedGames);
				break;
			case Kopru.Core.Health.FixAction.RunAdapterTest:
				OpenSettingsAt(SettingsViewModel.GeneralTab);
				if (SettingsPage.TestAdapterCommand.CanExecute(null))
				{
					SettingsPage.TestAdapterCommand.Execute(null);
				}
				break;
		}
	}

	public SettingsState Settings { get; }
	// The "Back To Game" button (Overview header), and the other ways back to a running game.
	public GameReturn BackToGame { get; }
	public OverviewViewModel Overview { get; }
	public SupportedGamesViewModel SupportedGames { get; }
	public OtherGamesViewModel OtherGames { get; }
	// The controller page (test, sticks, triggers, remap & gyro) and its parts.
	public ControllerPageViewModel Controller { get; }
	public SticksViewModel Sticks => Controller.Tuning;
	public TesterViewModel Tester => Controller.Test;
	// The trigger effects and strength: the output tests on the controller page's Test tab.
	public TriggersViewModel Triggers => Sticks.Triggers;
	public LightViewModel Light { get; }
	public SettingsViewModel SettingsPage { get; }
	public IReadOnlyList<NavItem> NavItems { get; }
	public IReadOnlyList<PageViewModel> Pages => _pages ??= NavItems.Select(n => n.Page).ToList();
	private IReadOnlyList<PageViewModel>? _pages;

	public string AppName => AppInfo.Name;
	public bool IsEdge => _service.Kind == ControllerKind.DualSenseEdge;
	public string DeviceName => _service.IsConnected ? _service.DeviceName : Text.DeviceNoController();
	public string DeviceConnection => _service.IsConnected ? Text.DeviceConnectedBluetooth() : Text.DeviceNotConnected();
	public string DeviceBattery => !_service.HasBattery ? Text.BatteryUnknown() : _service.IsCharging
		? Text.BatteryCharging(Math.Round(_service.BatteryPercent))
		: Text.BatteryPercent(Math.Round(_service.BatteryPercent));
	public bool IsConnected => _service.IsConnected;
	public double BatteryPercent => _service.BatteryPercent;
	public bool HasBattery => _service.IsConnected && _service.HasBattery;
	public bool IsCharging => _service.IsCharging;
	// Short battery reading for the sidebar ("%78" / "78%"), or "unknown".
	public string BatteryShort => HasBattery ? Text.BatteryValue(Math.Round(_service.BatteryPercent)) : Text.BatteryUnknown();
	// The battery in the controller card's status row: the reading, or a dash without one ("battery unknown" is in the
	// tooltip, DeviceBattery), so the virtual controller's state beside it keeps its room.
	public string BatteryRowText => HasBattery ? Text.BatteryValue(Math.Round(_service.BatteryPercent)) : "—";
	public string BatteryRowTip => HasBattery ? DeviceBattery : Text.BatteryUnknown();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CurrentPage))]
	private NavItem _selectedNav;

	partial void OnSelectedNavChanged(NavItem value)
	{
		Setup = null;
		UpdateLiveViews();
	}

	// The setup page covers the selected page while it is open: the controller page's live input stops meanwhile.
	partial void OnSetupChanged(SetupViewModel? oldValue, SetupViewModel? newValue) => UpdateLiveViews();

	// Whether the main window is on screen (shown and not minimised); set by the window. True until the window says
	// otherwise (view models without a window keep their live readouts as before).
	public bool IsWindowShown { get; private set; } = true;

	// The one place the live readouts follow the window: hidden to the icon next to the clock or minimised, every page
	// timer (Overview drawing and graph, controller tester and remap preview, mic meter, light preview) stops; shown
	// again, they start again on the page in view.
	public void SetWindowShown(bool shown)
	{
		if (shown == IsWindowShown)
		{
			return;
		}
		IsWindowShown = shown;
		UpdateLiveViews();
		OnPropertyChanged(nameof(IsWindowShown));
		if (shown)
		{
			// The user may have installed a driver meanwhile: the health picture is read in full again.
			_service.RefreshHealth();
		}
	}

	// The controller page holds the controller (Test tab) or polls its input only while it is shown; the Overview follows
	// the controller only while it is in view; the mic meter runs only on the Settings page.
	private void UpdateLiveViews()
	{
		Controller.SetVisible(IsWindowShown && Setup == null && SelectedNav.Page == Controller);
		Overview.SetVisible(IsWindowShown && SelectedNav.Page == Overview);
		SettingsPage.SetMicMeterShown(IsWindowShown && Setup == null && SelectedNav.Page == SettingsPage);
		SettingsPage.Info.SetShown(IsWindowShown);
		Settings.Live.SetShown(IsWindowShown);
		UpdateButtonPage();
	}

	// The controller page's Test or Remap tab in view: the user is trying the buttons there, so the touchpad double press
	// does not turn Mouse Mode on (IControllerService.ButtonPageShown).
	private void UpdateButtonPage() =>
		_service.ButtonPageShown = Setup == null && SelectedNav.Page == Controller && (Controller.IsTestTab || Controller.IsRemapTab);

	public PageViewModel CurrentPage => (PageViewModel?)Setup ?? SelectedNav.Page;

	public void Navigate(PageViewModel page) => SelectedNav = NavItems.First(n => n.Page == page);

	// Opens the controller page on a tab (ControllerPageViewModel.TestTab...).
	public void OpenController(int tab)
	{
		Controller.ShowTab(tab);
		Navigate(Controller);
	}

	public void Navigate(AppPage page)
	{
		switch (page)
		{
			case AppPage.SettingsGeneral:
				OpenSettingsAt(SettingsViewModel.GeneralTab);
				break;
			case AppPage.SettingsShortcuts:
				OpenSettingsAt(SettingsViewModel.ShortcutsTab);
				break;
			default:
				Navigate(page switch
				{
					AppPage.SupportedGames => SupportedGames,
					AppPage.OtherGames => OtherGames,
					_ => (PageViewModel)Overview
				});
				break;
		}
	}

	public NotificationCenter Notifications { get; }

	public SupportLinkViewModel Support { get; }

	// The automatic game list check started with the window (tests await it).
	public Task AutoUpdateTask { get; }

	// The main window is closing (exiting: the app is shutting down anyway). True: hide it to the tray instead; the first
	// time a toast explains that the app keeps running.
	public bool HideToTrayOnClose(bool exiting)
	{
		if (exiting || !Settings.KeepInTray)
		{
			return false;
		}
		if (!Settings.TrayHintShown)
		{
			Settings.TrayHintShown = true;
			Notifications.Post(NotificationCenter.TrayHint(), force: true);
		}
		return true;
	}
}

public static class AppInfo
{
	// Product name; the same in every language.
	public const string Name = "DS5 Untethered";

	// Major.minor.patch of the app assembly.
	public static string Version { get; } = typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
}

// 24x24 stroke icons (own drawings), drawn in the sidebar by IconSlot on their 24 grid: each glyph's ink is centred on
// the grid (12,12), so every icon sits on its label's centre line (the gamepads' ink, grips included, spans 5.6..18.4).
public static class Icons
{
	// Gamepad outline with a d-pad and two buttons.
	public const string Overview = "M7.5,5.6 H16.5 C19,5.6 20.4,7.2 21,9.8 L22,15.1 C22.5,17.6 20.6,19.2 18.8,18 L16.2,15.4 H7.8 L5.2,18 C3.4,19.2 1.5,17.6 2,15.1 L3,9.8 C3.6,7.2 5,5.6 7.5,5.6 Z M7,9.1 V12.1 M5.5,10.6 H8.5 M16,9.6 L16.01,9.6 M17.8,11.6 L17.81,11.6";
	// Gamepad with a check mark: games with native DualSense support.
	public const string SupportedGames = "M7.5,5.6 H16.5 C19,5.6 20.4,7.2 21,9.8 L22,15.1 C22.5,17.6 20.6,19.2 18.8,18 L16.2,15.4 H7.8 L5.2,18 C3.4,19.2 1.5,17.6 2,15.1 L3,9.8 C3.6,7.2 5,5.6 7.5,5.6 Z M9,10.6 L11,12.6 L15,8.6";
	// Waveform.
	public const string Haptics = "M2,12 H4.5 M7,8 V16 M10,4.5 V19.5 M13,8 V16 M16,6 V18 M19,10 V14 M21.5,12 H22";
	// Light rays around a dot.
	public const string Light = "M12,9 A3,3 0 1 1 11.99,9 Z M12,2.5 V5 M12,19 V21.5 M2.5,12 H5 M19,12 H21.5 M5.3,5.3 L7,7 M17,17 L18.7,18.7 M5.3,18.7 L7,17 M17,7 L18.7,5.3";
	// Gamepad with a pulse line: the controller page (test and tuning).
	public const string Controller = "M7.5,5.6 H16.5 C19,5.6 20.4,7.2 21,9.8 L22,15.1 C22.5,17.6 20.6,19.2 18.8,18 L16.2,15.4 H7.8 L5.2,18 C3.4,19.2 1.5,17.6 2,15.1 L3,9.8 C3.6,7.2 5,5.6 7.5,5.6 Z M6,11.1 H9 L10.5,8.6 L13.5,13.6 L15,11.1 H18";
	// Sliders.
	public const string Settings = "M4,7 H20 M4,17 H20 M9,4.5 V9.5 M15,14.5 V19.5";
}
