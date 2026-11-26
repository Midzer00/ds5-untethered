// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Audio;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Mods;
using Kopru.Core.Virtual;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

// One picture of the translation context: its id (the image's file name), the English page or dialog it shows.
// Prepare: what the state needs right before each of its pictures (scrolled parts included). Parts: whether a page taller
// than the window also gets its scrolled parts.
internal sealed record ContextShot(string Id, string Group, string Title, Action? Prepare = null, bool Parts = true);

// The app states the translation context shows, with public-safe data only: made-up game names, generic sound devices,
// no user names, paths or addresses. Run walks every page, tab and dialog in a fixed order and hands each one to `shot`
// (once with the plain English render, once with the key tag render; both see the same states).
internal static class ContextScene
{
	public const double Width = 1152;
	public const double Height = 700;
	private static readonly TimeSpan ConnectedFor = TimeSpan.FromSeconds(4 * 60 + 7);

	public const string RunningGame = "Starlight Rally";
	private const string RunningExe = "StarlightRally.exe";
	private const string RunningPath = @"C:\Games\Starlight Rally\StarlightRally.exe";
	// A game that is not on the supported list, added by hand (the games page's Unsupported view).
	private const string UnlistedGame = AuditRenderTests.MadeUpGame;
	private const string UnlistedExe = "NeonDrift.exe";
	private const string UnlistedPath = @"C:\Games\Neon Drift\NeonDrift.exe";

	// A made-up supported games list (Steam ids far outside the real range, so no picture could ever match).
	public static GameCatalog Catalog { get; } = new(
	[
		new CatalogGame(RunningGame, [RunningExe], 99000001, true, true, "high"),
		new CatalogGame("Hollow Tide", ["HollowTide.exe"], 99000002, false, true, "medium"),
		new CatalogGame("Ember Knights", ["EmberKnights-Win64-Shipping.exe"], 99000003, true, true, "high"),
		new CatalogGame("Paper Comet", ["PaperComet.exe"], 99000004, true, false, "high"),
	]);

	// Real names that must never reach a public picture (the other tests' sample data).
	public static readonly string[] ForbiddenText =
		["Cyberpunk", "Assetto", "Spider-Man", "Marvel", "Returnal", "Realtek", "HyperX", "Web Kamera", @"C:\Users", "Player\\"];

	// `language`: English for the plain render, or the key tag locale. Both build their view models with the language on
	// Automatic (so the language picker shows the same entry), then switch. `shot` sees the window with the state in place
	// (not yet settled).
	public static async Task Run(Language language, Func<ContextShot, Window, Task> each)
	{
		Task shot(ContextShot s, Window w) => WithScrolledParts(s, w, each);
		await MainWindowShots(language, shot);
		await EdgeTabShot(language, shot);
		await NoControllerShot(language, shot);
		await UpdateShot(language, shot);
		await GamesShots(language, shot);
		await SetupShot(language, shot);
		await NotificationShots(language, shot);
	}

	// A page or dialog taller than the window: after the first picture, the rest of it, scrolled a window at a time
	// ("<id>-2", "<id>-3"; at most MaxParts pictures), then back to the top.
	public const int MaxParts = 4;
	private const double PartOverlap = 48;

	private static async Task WithScrolledParts(ContextShot first, Window window, Func<ContextShot, Window, Task> each)
	{
		first.Prepare?.Invoke();
		await each(first, window);
		if (!first.Parts || ScrolledPart(window) is not { } scroll)
		{
			return;
		}
		for (int part = 2; part <= MaxParts && scroll.Offset.Y + scroll.Viewport.Height < scroll.Extent.Height - 1; part++)
		{
			double y = Math.Min(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y + scroll.Viewport.Height - PartOverlap);
			scroll.Offset = new Avalonia.Vector(scroll.Offset.X, y);
			first.Prepare?.Invoke();
			await each(first with { Id = $"{first.Id}-{part}", Title = $"{first.Title} ({part})" }, window);
		}
		scroll.Offset = new Avalonia.Vector(scroll.Offset.X, 0);
		FitCheck.Settle(window);
	}

	// The largest shown scroll area with more below it, not under a dialog.
	private static ScrollViewer? ScrolledPart(Window window) =>
		window.GetVisualDescendants().OfType<ScrollViewer>()
			.Where(v => v.Extent.Height > v.Viewport.Height + 8 && v.Viewport.Height > 100 && KeyTagScanner.IsShown(v, window))
			.Where(v => KeyTagScanner.ToWindow(v, new Avalonia.Rect(v.Bounds.Size), window) is var box && !KeyTagScanner.IsCovered(v, box.Deflate(8), window))
			.OrderByDescending(v => v.Viewport.Width * v.Viewport.Height)
			.FirstOrDefault();

	// Sets the language for this render after a view model was built (building one applies the saved language: Automatic).
	private static void Apply(Language language) => Localizer.Chosen = language;

	// The same, and the fake's health report built again in that language (its chip names are worded when it is built; the
	// app builds a new one on every health check).
	private static void Apply(Language language, FakeControllerService fake)
	{
		Apply(language);
		fake.Health = new FakeControllerService(animate: false).Health;
		fake.RaiseStateChanged();
	}

	private static FakeControllerService NewService()
	{
		var fake = new FakeControllerService(animate: false)
		{
			Details = ControllerInfoAppTests.Details(),
			ConnectedSince = DateTimeOffset.UtcNow - ConnectedFor
		};
		UseMadeUpGames(fake);
		UseGenericSoundDevices(fake.AudioEndpoints);
		return fake;
	}

	private static void UseMadeUpGames(FakeControllerService fake)
	{
		fake.Watcher.Catalog = Catalog;
		fake.Games.SetCatalog(Catalog);
		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Watcher.Source.Show(RunningExe, RunningPath);
		fake.Games.Tick();
	}

	private static void UseGenericSoundDevices(FakeAudioEndpoints endpoints)
	{
		endpoints.Add("speaker", AudioFlow.Render, "Speakers (USB Audio)");
		endpoints.Add("headset", AudioFlow.Render, "Headphones (Wireless Headset)");
		endpoints.Add("pad-speaker", AudioFlow.Render, "Speakers (DualSense Wireless Controller)", pad: true, port: 1);
		endpoints.Add("webcam", AudioFlow.Capture, "Microphone (USB Camera)");
		endpoints.Add("pad-mic", AudioFlow.Capture, "Headset Microphone (DualSense Wireless Controller)", pad: true, port: 1);
	}

	// SettingsFitTests.FullCase with public-safe data: everything that can make a tab taller shown.
	private static (MainWindowViewModel Vm, FakeControllerService Fake) FullCase(Language language)
	{
		FakeControllerService fake = NewService();
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now })));
		Apply(language, fake);
		vm.Sticks.StopPreview();
		vm.Overview.ToggleBridgeCommand.Execute(null);
		fake.Tick();
		vm.Settings.KnownControllers.Observe([SettingsFitTests.Entry("fake-pad-b", 2, battery: 41, edge: true)]);
		vm.Settings.KnownControllers.Observe([]);
		vm.Settings.KnownControllers.Observe([SettingsFitTests.Entry("fake-pad-a", battery: 78)]);
		vm.Settings.RenameController("fake-pad-a", "Living Room Pad");
		vm.Settings.Profiles.Create("Racing", vm.Settings.CaptureValues());
		vm.Settings.Profiles.Create("Shooter", vm.Settings.CaptureValues());
		fake.ControllerList.Add(SettingsFitTests.Entry("fake-pad-a", battery: 78));
		vm.Settings.MicEnabled = true;
		fake.MicLevel = 0.35;
		vm.SettingsPage.RefreshMic();
		vm.SettingsPage.Info.Refresh();
		vm.SettingsPage.ToolResult = Text.DiagnosticsSaved(@"Documents\ds5-untethered-diagnostics.zip");
		fake.RaiseStateChanged();
		return (vm, fake);
	}

	private static async Task MainWindowShots(Language language, Func<ContextShot, Window, Task> shot)
	{
		(MainWindowViewModel vm, FakeControllerService fake) = FullCase(language);
		var window = new MainWindow { DataContext = vm, Width = Width, Height = Height };
		window.Show();
		// The same "connected for" in both renders, whenever the shot is taken.
		void Prepare()
		{
			fake.ConnectedSince = DateTimeOffset.UtcNow - ConnectedFor;
			vm.SettingsPage.Info.Refresh();
			vm.SettingsPage.Info.Tick();
			// One fresh input report in the last second, so the live status and its rate read the same in both renders.
			if (fake.LiveHandler != null)
			{
				Thread.Sleep(1050);
				fake.LiveHandler(TesterTests.Sample);
			}
			vm.Tester.Tick();
		}
		Task Shot(ContextShot s, Window w) => shot(s with { Prepare = Prepare }, w);
		try
		{
			string controller = vm.Controller.Title;
			vm.Navigate(vm.Overview);
			await Shot(new ContextShot("overview", vm.Overview.Title, vm.Overview.Title), window);

			vm.Overview.Live.SelectedView = LivePanelViewModel.SignalsView;
			await Shot(new ContextShot("overview-signals", vm.Overview.Title, vm.Overview.Title + " (" + Strip(Text.LiveViewSignals()) + ")"), window);
			vm.Overview.Live.SelectedView = LivePanelViewModel.LinkView;

			vm.Navigate(vm.OtherGames);
			vm.OtherGames.SelectedTab = OtherGamesViewModel.GameEffectsTab;
			await Shot(new ContextShot("haptics-triggers", vm.OtherGames.Title, Strip(vm.OtherGames.Tabs[OtherGamesViewModel.GameEffectsTab].Name)), window);
			vm.OtherGames.SelectedTab = OtherGamesViewModel.RumbleTab;
			await Shot(new ContextShot("haptics-triggers-rumble", vm.OtherGames.Title, Strip(vm.OtherGames.Tabs[OtherGamesViewModel.RumbleTab].Name)), window);
			vm.OtherGames.SelectedTab = OtherGamesViewModel.GameEffectsTab;

			vm.OpenController(ControllerPageViewModel.TestTab);
			fake.LiveHandler!(TesterTests.Sample);
			vm.Tester.Tick();
			await Shot(new ContextShot("controller-test", controller, Strip(vm.Controller.Tabs[ControllerPageViewModel.TestTab].Name)), window);

			if (vm.Controller.Look.IsAvailable)
			{
				vm.Controller.Look.OpenCommand.Execute(null);
				await vm.Controller.Look.ThumbnailsReady;
				// Its scrolled parts add only more colour names (they are not catalogue texts).
				await Shot(new ContextShot("dialog-controller-look", controller, Strip(Text.LookLabel()), Parts: false), window);
				vm.Controller.Look.CloseCommand.Execute(null);
				FitCheck.Settle(window);
			}

			vm.OpenController(ControllerPageViewModel.OutputsTab);
			await Shot(new ContextShot("controller-outputs", controller, Strip(vm.Controller.Tabs[ControllerPageViewModel.OutputsTab].Name)), window);

			vm.OpenController(ControllerPageViewModel.ButtonsTab);
			await Shot(new ContextShot("controller-buttons", controller, Strip(vm.Controller.Tabs[ControllerPageViewModel.ButtonsTab].Name)), window);

			Kopru.Core.Input.GyroSettings savedGyro = vm.Settings.Gyro;
			vm.Settings.Gyro = savedGyro with { Mode = Kopru.Core.Input.GyroMode.WhileL2 };
			vm.OpenController(ControllerPageViewModel.GyroTab);
			await Shot(new ContextShot("controller-gyro-sticks", controller, Strip(vm.Controller.Tabs[ControllerPageViewModel.GyroTab].Name)), window);
			vm.Settings.Gyro = savedGyro;

			vm.Navigate(vm.Light);
			await Shot(new ContextShot("light", vm.Light.Title, vm.Light.Title), window);

			vm.Navigate(vm.SettingsPage);
			string[] ids = ["general", "controllers", "audio", "shortcuts", "mouse", "notifications"];
			for (int tab = 0; tab < vm.SettingsPage.Tabs.Count; tab++)
			{
				vm.SettingsPage.SelectedTab = tab;
				string id = tab < ids.Length ? ids[tab] : $"tab{tab}";
				await Shot(new ContextShot($"settings-{id}", vm.SettingsPage.Title, Strip(vm.SettingsPage.Tabs[tab].Name)), window);
			}

			// DSX mod support on, a mod heard from a while ago.
			fake.DsxStatus = new DsxStatus(DsxListenerState.Listening, 6969, true, DateTimeOffset.UtcNow.AddMinutes(-12).AddSeconds(-20));
			vm.Settings.DsxModSupport = true;
			fake.RaiseDsxStatusChanged();
			vm.SettingsPage.SelectedTab = SettingsViewModel.ControllersTab;
			await Shot(new ContextShot("settings-controllers-dsx", vm.SettingsPage.Title,
				Strip(vm.SettingsPage.Tabs[SettingsViewModel.ControllersTab].Name) + " (DSX)"), window);

			// The virtual controller stopped: the Overview's state card instead of the live numbers.
			vm.Overview.ToggleBridgeCommand.Execute(null);
			fake.Tick();
			fake.RaiseStateChanged();
			vm.Navigate(vm.Overview);
			await Shot(new ContextShot("overview-stopped", vm.Overview.Title, vm.Overview.Title + " (Virtual Controller Off)"), window);
		}
		finally
		{
			window.Close();
		}
	}

	// The Games page (the supported list, then the Unsupported view) with a made-up game that is not on the list running,
	// then that game's panel and its three tabs.
	private static async Task GamesShots(Language language, Func<ContextShot, Window, Task> shot)
	{
		FakeControllerService fake = NewService();
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings
		{
			GameListCheckedAt = DateTimeOffset.Now,
			UserGames = [new UserGame(UnlistedGame, UnlistedPath), new UserGame("Pixel Harbor", @"D:\Games\Pixel Harbor\PixelHarbor.exe")]
		}), InstalledGames: FakeFoundGames.Installed(FoundGames())));
		Apply(language, fake);
		vm.BackToGame.Return = (_, _) => { };
		vm.Overview.ToggleBridgeCommand.Execute(null);
		fake.Tick();
		await vm.SupportedGames.ScanTask;
		fake.Watcher.Exit(RunningExe);
		fake.Watcher.Source.Show(UnlistedExe, UnlistedPath);
		fake.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		var window = new MainWindow { DataContext = vm, Width = Width, Height = Height };
		window.Show();
		try
		{
			vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
			string games = vm.SupportedGames.Title;
			await shot(new ContextShot("games", games, games), window);

			vm.SupportedGames.ShowUnsupported = true;
			Dispatcher.UIThread.RunJobs();
			await shot(new ContextShot("games-unsupported", games, Strip(Text.UnsupportedGamesTitle())), window);

			GameItemViewModel running = vm.SupportedGames.Games.Single(g => g.Name == UnlistedGame);
			vm.SupportedGames.OpenGameCommand.Execute(running);
			GamePanelViewModel panel = vm.SupportedGames.Panel;
			panel.UseOwnSettingsCommand.Execute(null);
			panel.SaveProfileNameCommand.Execute(null);
			string[] ids = ["game", "vibration", "triggers"];
			for (int segment = 0; segment < panel.Tabs.Count; segment++)
			{
				panel.SelectedSegment = segment;
				string id = segment < ids.Length ? ids[segment] : $"tab{segment}";
				await shot(new ContextShot($"dialog-game-panel-{id}", games, "Game Panel: " + Strip(panel.Tabs[segment].Name)), window);
			}

			// A search that finds nothing.
			panel.CloseCommand.Execute(null);
			vm.SupportedGames.ShowUnsupported = false;
			vm.SupportedGames.SearchText = "Quasar";
			Dispatcher.UIThread.RunJobs();
			await shot(new ContextShot("games-no-results", games, games + " (No Results)"), window);
		}
		finally
		{
			window.Close();
		}
	}

	// Installed games found on this PC: a catalog game through Steam, two games that are not on the list.
	private static FakeGameFolders FoundGames()
	{
		var folders = new FakeGameFolders(@"C:\Steam\steamapps\common\Hollow Tide", FakeFoundGames.RacerFolder, @"C:\Steam\steamapps\common\Moss Garden",
			@"E:\Epic\Hollow Peak");
		folders.File(@"C:\Steam\steamapps\appmanifest_99000002.acf", SteamManifest(99000002, "Hollow Tide", "Hollow Tide"));
		folders.File(@"C:\Steam\steamapps\appmanifest_9009.acf", SteamManifest(9009, "Pixel Racer", "PixelRacer"));
		folders.File(@"C:\Steam\steamapps\appmanifest_9020.acf", SteamManifest(9020, "Moss Garden", "Moss Garden"));
		folders.File(FakeFoundGames.Epic + @"\A.item",
			"""{ "DisplayName": "Hollow Peak", "AppName": "HollowPeakApp", "AppCategories": ["public", "games"], "InstallLocation": "E:\\Epic\\Hollow Peak", "LaunchExecutable": "HollowPeak.exe" }""");
		return folders;
	}

	private static string SteamManifest(long id, string name, string dir) =>
		$"\"AppState\"\n{{\n\t\"appid\"\t\t\"{id}\"\n\t\"name\"\t\t\"{name}\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"{dir}\"\n}}\n";

	// Settings > General with a new version found (the update check's own fake server and installer).
	private static async Task UpdateShot(Language language, Func<ContextShot, Window, Task> shot)
	{
		FakeControllerService fake = NewService();
		var updates = new UpdateFakes();
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now }),
			Updates: updates.Services()));
		Apply(language, fake);
		vm.Sticks.StopPreview();
		await vm.Updates.ActCommand.ExecuteAsync(null);
		Dispatcher.UIThread.RunJobs();
		var window = new MainWindow { DataContext = vm, Width = Width, Height = Height };
		window.Show();
		try
		{
			vm.OpenSettingsAt(SettingsViewModel.GeneralTab);
			await shot(new ContextShot("settings-general-update", vm.SettingsPage.Title,
				Strip(vm.SettingsPage.Tabs[SettingsViewModel.GeneralTab].Name) + " (" + Strip(Text.NotifyUpdateTitle()) + ")"), window);
		}
		finally
		{
			window.Close();
		}
	}

	// No controller connected: the Overview's empty state.
	private static async Task NoControllerShot(Language language, Func<ContextShot, Window, Task> shot)
	{
		var fake = new FakeControllerService(ControllerKind.None, animate: false);
		UseMadeUpGames(fake);
		UseGenericSoundDevices(fake.AudioEndpoints);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now })));
		Apply(language, fake);
		vm.Sticks.StopPreview();
		var window = new MainWindow { DataContext = vm, Width = Width, Height = Height };
		window.Show();
		try
		{
			vm.Navigate(vm.Overview);
			await shot(new ContextShot("overview-no-controller", vm.Overview.Title, vm.Overview.Title + " (No Controller)"), window);
		}
		finally
		{
			window.Close();
		}
	}

	// The toast cards (they show in their own small windows at the screen's corner): eight to a picture, on the app's
	// backdrop.
	private static async Task NotificationShots(Language language, Func<ContextShot, Window, Task> shot)
	{
		Apply(language);
		AppNotification[] cards =
		[
			NotificationCenter.GameDetected(RunningGame, VirtualPadType.DualSense, 78),
			new(NotificationType.Connected, NotificationKind.Success, Text.NotifyConnectedTitle(), Text.NotifyConnectedBody("Living Room Pad"), AppPage.Overview, 78),
			new(NotificationType.Disconnected, NotificationKind.Info, Text.NotifyDisconnectedTitle(), Text.NotifyDisconnectedBody(), AppPage.Overview),
			new(NotificationType.BatteryLow, NotificationKind.Warning, Text.NotifyBatteryLowTitle(), Text.NotifyBatteryLowBody(15), AppPage.Overview, 15),
			new(NotificationType.BatteryCritical, NotificationKind.Error, Text.NotifyBatteryCriticalTitle(), Text.NotifyBatteryCriticalBody(5), AppPage.Overview, 5),
			new(NotificationType.Charged, NotificationKind.Success, Text.NotifyChargedTitle(), Text.NotifyChargedBody(), AppPage.Overview, 100),
			new(NotificationType.SteamInput, NotificationKind.Warning, Text.NotifySteamInputTitle(), Text.NotifySteamInputBody(), AppPage.Overview),
			new(NotificationType.PoweredOffIdle, NotificationKind.Info, Text.NotifyPoweredOffTitle(), Text.NotifyPoweredOffBody(), AppPage.SettingsGeneral),
			new(NotificationType.BridgeError, NotificationKind.Error, Text.NotifyBridgeErrorTitle(), NotificationCenter.BridgeReason(Kopru.Core.Bridge.BridgeError.NoCapture),
				AppPage.Overview),
			new(NotificationType.HidHideMissing, NotificationKind.Warning, Text.NotifyHidHideTitle(), Text.NotifyHidHideBody(), AppPage.Overview),
			new(NotificationType.CableConnected, NotificationKind.Info, Text.NotifyCableTitle(), Text.NotifyCableBody(), AppPage.Overview),
			NotificationCenter.LinkWeak(),
			NotificationCenter.GameListUpdated(3),
			NotificationCenter.UpdateAvailable("0.3.0"),
			NotificationCenter.TrayHint(),
			NotificationCenter.MouseModeOff(null),
			NotificationCenter.PadTypeNotice(VirtualPadType.Xbox360, PadTypeSource.Default, gameRunning: true),
			NotificationCenter.XboxSeriesFallback(),
			new(NotificationType.GameDetected, NotificationKind.Error, Text.GameLaunchFailedTitle(), Text.GameLaunchFailedBody(RunningGame), AppPage.SupportedGames),
		];
		const int perShot = 8;
		for (int page = 0; page * perShot < cards.Length; page++)
		{
			var grid = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Avalonia.Thickness(150, 40, 0, 0), ItemSpacing = 40, LineSpacing = 28 };
			foreach (AppNotification card in cards.Skip(page * perShot).Take(perShot))
			{
				grid.Children.Add(new ToastView { DataContext = new ToastViewModel(card) });
			}
			var window = new Window
			{
				Width = Width,
				Height = Height,
				Background = Avalonia.Application.Current!.FindResource("BackdropBrush") as Avalonia.Media.IBrush,
				Content = grid
			};
			window.Show();
			try
			{
				string title = Strip(Text.GroupNotifications());
				await shot(new ContextShot($"notifications-{page + 1}", title, $"{title} {page + 1}"), window);
			}
			finally
			{
				window.Close();
			}
		}
	}

	// The Edge tab (shown only while a DualSense Edge is connected).
	private static async Task EdgeTabShot(Language language, Func<ContextShot, Window, Task> shot)
	{
		var fake = new FakeControllerService(ControllerKind.DualSenseEdge, animate: false)
		{
			Details = ControllerInfoAppTests.Details(),
			ConnectedSince = DateTimeOffset.UtcNow - ConnectedFor
		};
		UseMadeUpGames(fake);
		UseGenericSoundDevices(fake.AudioEndpoints);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now })));
		Apply(language, fake);
		vm.Sticks.StopPreview();
		vm.OpenController(ControllerPageViewModel.EdgeTab);
		var window = new MainWindow { DataContext = vm, Width = Width, Height = Height };
		window.Show();
		try
		{
			await shot(new ContextShot("controller-edge", vm.Controller.Title, Strip(vm.Controller.Tabs[ControllerPageViewModel.EdgeTab].Name)), window);
		}
		finally
		{
			window.Close();
		}
	}

	// The setup page: Drivers open (default with drivers missing), then Adapter Test open.
	private static async Task SetupShot(Language language, Func<ContextShot, Window, Task> shot)
	{
		var probes = new RealServiceTests.Probes(drivers: false);
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(new MemorySettingsStore(new AppSettings()), null,
			() => new SetupWizard(new SystemCheck(probes, probes, probes, probes), new RealServiceTests.Links())));
		Apply(language);
		var window = new MainWindow { DataContext = vm, Width = Width, Height = Height };
		window.Show();
		try
		{
			await shot(new ContextShot("setup", "Setup", "Setup"), window);
			vm.Setup!.ExpandStep(SetupStepId.AdapterTest);
			await shot(new ContextShot("setup-adapter", "Setup", "Setup (Adapter Test)"), window);
		}
		finally
		{
			window.Close();
		}
	}

	private static string Strip(string text) => KeyTagLocale.Strip(text);
}
