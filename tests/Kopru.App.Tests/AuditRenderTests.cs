// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

// For a visual review of the whole app: every page and sub-tab (Overview, Games with and without a game's pop-up, the pop-up
// of a running and a not running game, Haptics And Triggers, the controller page's three tabs, Light, the six Settings tabs,
// the setup page) at the owner's window (1152x700) and 1100x700, Turkish and English, with the bridge on, a controller with
// details, profiles and a made-up game running. Only with KOPRU_AUDIT_RENDER_DIR set; otherwise it does nothing.
public sealed class AuditRenderTests
{
	internal const string MadeUpGame = "Neon Drift";
	private const string MadeUpExe = "NeonDrift.exe";
	private const string MadeUpPath = @"C:\Games\Neon Drift\NeonDrift.exe";
	private static readonly (double Width, double Height)[] Sizes = [(1152, 700), (1100, 700)];

	[AvaloniaFact]
	public async Task RenderEveryPage()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_AUDIT_RENDER_DIR") is { Length: > 0 } d ? d : null;
		if (outDir == null)
		{
			return;
		}
		Directory.CreateDirectory(outDir);
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height) in Sizes)
				{
					string size = $"{width}x{height}";
					(MainWindowViewModel vm, FakeControllerService _) = SettingsFitTests.FullCase(code);
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.Navigate(vm.Overview);
					Save(window, outDir, $"overview-{size}-{code}.png");
					vm.Navigate(vm.OtherGames);
					Save(window, outDir, $"haptics-{size}-{code}.png");
					foreach ((int tab, string name) in new[] { (ControllerPageViewModel.TestTab, "test"), (ControllerPageViewModel.OutputsTab, "outputs"), (ControllerPageViewModel.RemapTab, "remap") })
					{
						vm.OpenController(tab);
						Save(window, outDir, $"controller-{name}-{size}-{code}.png");
					}
					vm.Navigate(vm.Light);
					Save(window, outDir, $"light-{size}-{code}.png");
					string[] tabs = ["general", "controllers", "audio", "shortcuts", "mouse", "notifications"];
					vm.Navigate(vm.SettingsPage);
					for (int tab = 0; tab < tabs.Length; tab++)
					{
						vm.SettingsPage.SelectedTab = tab;
						Save(window, outDir, $"settings-{tabs[tab]}-{size}-{code}.png");
					}
					window.Close();

					// The Games page: the list, then the pop-up of the running made-up game and of a game that is not running.
					(MainWindowViewModel games, MainWindow gamesWindow) = await OpenGames(code, width, height);
					Save(gamesWindow, outDir, $"games-{size}-{code}.png");
					games.SupportedGames.OpenGameCommand.Execute(games.SupportedGames.Games.Single(g => g.Name == MadeUpGame));
					games.SupportedGames.Panel.UseOwnSettingsCommand.Execute(null); games.SupportedGames.Panel.SaveProfileNameCommand.Execute(null);
					Save(gamesWindow, outDir, $"popup-running-{size}-{code}.png");
					games.SupportedGames.Panel.CloseCommand.Execute(null);
					GameItemViewModel other = games.SupportedGames.Games.First(g => !g.IsRunning && g.CanLaunch);
					games.SupportedGames.OpenGameCommand.Execute(other);
					Save(gamesWindow, outDir, $"popup-idle-{size}-{code}.png");
					games.SupportedGames.Panel.UseOwnSettingsCommand.Execute(null); games.SupportedGames.Panel.SaveProfileNameCommand.Execute(null);
					Save(gamesWindow, outDir, $"popup-idle-own-{size}-{code}.png");
					gamesWindow.Close();

					// The setup page with the drivers missing.
					var probes = new RealServiceTests.Probes(drivers: false);
					var setupVm = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(new MemorySettingsStore(new AppSettings { Language = code }), null,
						() => new SetupWizard(new SystemCheck(probes, probes, probes, probes), new RealServiceTests.Links())));
					var setupWindow = new MainWindow { DataContext = setupVm, Width = width, Height = height };
					setupWindow.Show();
					Save(setupWindow, outDir, $"setup-{size}-{code}.png");
					setupWindow.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The Games page (Unsupported view) with the made-up game running, the sample catalog and found games around it.
	internal static async Task<(MainWindowViewModel Vm, MainWindow Window)> OpenGames(string code, double width, double height)
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings
		{
			GameListCheckedAt = DateTimeOffset.Now,
			Language = code,
			UserGames = [new UserGame(MadeUpGame, MadeUpPath), new UserGame("Pixel Harbor", @"D:\Games\Pixel Harbor\PixelHarbor.exe")]
		}), InstalledGames: FakeFoundGames.Installed(FakeFoundGames.Folders())));
		vm.BackToGame.Return = (_, _) => { };
		vm.Overview.ToggleBridgeCommand.Execute(null);
		fake.Tick();
		await vm.SupportedGames.ScanTask;
		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Watcher.Source.Show(MadeUpExe, MadeUpPath);
		fake.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		vm.SupportedGames.ShowUnsupported = true;
		Dispatcher.UIThread.RunJobs();
		var window = new MainWindow { DataContext = vm, Width = width, Height = height };
		window.Show();
		vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
		FitCheck.Settle(window);
		return (vm, window);
	}

	private static void Save(Window window, string outDir, string name)
	{
		FitCheck.Settle(window);
		Thread.Sleep(320);
		FitCheck.Settle(window);
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		using var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		frame!.Save(Path.Combine(outDir, name));
	}
}
