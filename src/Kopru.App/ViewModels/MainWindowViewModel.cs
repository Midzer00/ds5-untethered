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
// FullScreen: whether a full-screen app is in front (for "don't notify while playing").
public sealed record AppEnvironment(ISettingsStore? Store = null, IStartupRegistration? Startup = null, Func<SetupWizard>? Wizard = null,
	Func<bool>? FullScreen = null, Func<long>? Clock = null);

public sealed partial class MainWindowViewModel : ViewModelBase
{
	private readonly IControllerService _service;
	private readonly AppEnvironment _environment;

	public MainWindowViewModel(IControllerService service, AppEnvironment? environment = null)
	{
		_service = service;
		_environment = environment ?? new AppEnvironment();
		Settings = new SettingsState(service, _environment.Store, _environment.Startup);
		Overview = new OverviewViewModel(service, Settings);
		SupportedGames = new SupportedGamesViewModel(service, Settings);
		OtherGames = new OtherGamesViewModel(service, Settings);
		Triggers = new TriggersViewModel(service, Settings);
		Sticks = new SticksViewModel(service, Settings);
		Light = new LightViewModel(service, Settings);
		SettingsPage = new SettingsViewModel(service, Settings, _environment.Wizard != null ? ShowSetup : null);
		NavItems =
		[
			new(Overview, Icons.Overview),
			new(SupportedGames, Icons.SupportedGames),
			new(OtherGames, Icons.Haptics),
			new(Triggers, Icons.Triggers),
			new(Sticks, Icons.Sticks),
			new(Light, Icons.Light),
			new(SettingsPage, Icons.Settings),
		];
		_selectedNav = NavItems[0];
		Notifications = new NotificationCenter(service, Settings, _environment.FullScreen, _environment.Clock);
		_service.StateChanged += (_, _) => RunOnUi(() => OnPropertyChanged(string.Empty));
		if (Settings.IsFirstRun && _environment.Wizard != null)
		{
			ShowSetup();
			if (Setup?.Wizard.FirstIncomplete == null)
			{
				Setup = null;
			}
		}
	}

	// The setup page while it is open (shown instead of the selected page).
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CurrentPage))]
	private SetupViewModel? _setup;

	public void ShowSetup()
	{
		if (_environment.Wizard is { } create)
		{
			Setup = new SetupViewModel(create(), _service, () => Setup = null);
		}
	}

	public SettingsState Settings { get; }
	public OverviewViewModel Overview { get; }
	public SupportedGamesViewModel SupportedGames { get; }
	public OtherGamesViewModel OtherGames { get; }
	public TriggersViewModel Triggers { get; }
	public SticksViewModel Sticks { get; }
	public LightViewModel Light { get; }
	public SettingsViewModel SettingsPage { get; }
	public IReadOnlyList<NavItem> NavItems { get; }
	public IReadOnlyList<PageViewModel> Pages => NavItems.Select(n => n.Page).ToList();

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

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(CurrentPage))]
	private NavItem _selectedNav;

	partial void OnSelectedNavChanged(NavItem value) => Setup = null;

	public PageViewModel CurrentPage => (PageViewModel?)Setup ?? SelectedNav.Page;

	public void Navigate(PageViewModel page) => SelectedNav = NavItems.First(n => n.Page == page);

	public void Navigate(AppPage page) => Navigate(page switch
	{
		AppPage.SupportedGames => SupportedGames,
		AppPage.OtherGames => OtherGames,
		AppPage.Settings => SettingsPage,
		_ => (PageViewModel)Overview
	});

	public NotificationCenter Notifications { get; }

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
}

// 24x24 stroke icons (own drawings).
public static class Icons
{
	// Gamepad outline with a d-pad and two buttons.
	public const string Overview = "M7.5,6 H16.5 C19,6 20.4,7.6 21,10.2 L22,15.5 C22.5,18 20.6,19.6 18.8,18.4 L16.2,15.8 H7.8 L5.2,18.4 C3.4,19.6 1.5,18 2,15.5 L3,10.2 C3.6,7.6 5,6 7.5,6 Z M7,9.5 V12.5 M5.5,11 H8.5 M16,10 L16.01,10 M17.8,12 L17.81,12";
	// Gamepad with a check mark: games with native DualSense support.
	public const string SupportedGames = "M7.5,6 H16.5 C19,6 20.4,7.6 21,10.2 L22,15.5 C22.5,18 20.6,19.6 18.8,18.4 L16.2,15.8 H7.8 L5.2,18.4 C3.4,19.6 1.5,18 2,15.5 L3,10.2 C3.6,7.6 5,6 7.5,6 Z M9,11 L11,13 L15,9";
	// Waveform.
	public const string Haptics = "M2,12 H4.5 M7,8 V16 M10,4.5 V19.5 M13,8 V16 M16,6 V18 M19,10 V14 M21.5,12 H22";
	// Trigger with a resistance arc.
	public const string Triggers = "M8,21 V11 C8,6.5 9.8,4 12,4 C14.2,4 16,6.5 16,11 V21 M8,13 C10.5,11.8 13.5,11.8 16,13";
	// Stick seen from above: outer ring and the cap.
	public const string Sticks = "M12,3 A9,9 0 1 1 11.99,3 Z M12,8.5 A3.5,3.5 0 1 1 11.99,8.5 Z";
	// Light rays around a dot.
	public const string Light = "M12,9 A3,3 0 1 1 11.99,9 Z M12,2.5 V5 M12,19 V21.5 M2.5,12 H5 M19,12 H21.5 M5.3,5.3 L7,7 M17,17 L18.7,18.7 M5.3,18.7 L7,17 M17,7 L18.7,5.3";
	// Sliders.
	public const string Settings = "M4,7 H20 M4,17 H20 M9,4.5 V9.5 M15,14.5 V19.5";
}
