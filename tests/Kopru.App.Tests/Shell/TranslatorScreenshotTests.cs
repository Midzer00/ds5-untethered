// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

// Renders every page and dialog headless per language into KOPRU_I18N_SCREENSHOTS_DIR/<lang>/*.png for translators
// (invoked via tools/i18n/screenshots.ps1). Does nothing unless KOPRU_I18N_SCREENSHOTS_DIR is set.
public sealed class TranslatorScreenshotTests
{
	private static readonly string[] SettingsTabs = ["general", "controllers", "audio", "shortcuts", "mouse", "notifications"];

	[AvaloniaFact]
	public async Task RenderTranslatorScreenshots()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_I18N_SCREENSHOTS_DIR") is { Length: > 0 } d ? d : null;
		if (outDir == null)
		{
			return;
		}

		double width = ParseDimension("KOPRU_I18N_SCREENSHOTS_WIDTH", 1152);
		double height = ParseDimension("KOPRU_I18N_SCREENSHOTS_HEIGHT", 700);
		IReadOnlyList<Language> languages = SelectedLanguages(Environment.GetEnvironmentVariable("KOPRU_I18N_SCREENSHOTS_LANGS"));

		Language? before = Localizer.Chosen;
		try
		{
			foreach (Language language in languages)
			{
				string code = language.Code;
				string langDir = Path.Combine(outDir, code);
				Directory.CreateDirectory(langDir);

				Localizer.Chosen = language;
				(MainWindowViewModel vm, FakeControllerService fake) = SettingsFitTests.FullCase(code);
				Localizer.Chosen = language;
				var window = new MainWindow { DataContext = vm, Width = width, Height = height };
				window.Show();
				try
				{
					// Main pages
					vm.Navigate(vm.Overview);
					Save(window, langDir, "overview.png");

					vm.Navigate(vm.OtherGames);
					Save(window, langDir, "haptics-triggers.png");

					// Controller tabs + Look picker dialog
					vm.OpenController(ControllerPageViewModel.TestTab);
					fake.LiveHandler!(TesterTests.Sample);
					vm.Tester.Tick();
					Save(window, langDir, "controller-test.png");

					if (vm.Controller.Look.IsAvailable)
					{
						vm.Controller.Look.OpenCommand.Execute(null);
						await vm.Controller.Look.ThumbnailsReady;
						Save(window, langDir, "dialog-controller-look.png");
						vm.Controller.Look.CloseCommand.Execute(null);
						FitCheck.Settle(window);
					}

					vm.OpenController(ControllerPageViewModel.OutputsTab);
					Save(window, langDir, "controller-outputs.png");

					vm.OpenController(ControllerPageViewModel.RemapTab);
					Save(window, langDir, "controller-remap.png");

					vm.Navigate(vm.Light);
					Save(window, langDir, "light.png");

					// All 6 Settings tabs
					vm.Navigate(vm.SettingsPage);
					for (int tab = 0; tab < SettingsTabs.Length; tab++)
					{
						vm.SettingsPage.SelectedTab = tab;
						Save(window, langDir, $"settings-{SettingsTabs[tab]}.png");
					}
				}
				finally
				{
					window.Close();
				}

				// Games page and the per-game settings dialog (all 3 segments)
				Localizer.Chosen = language;
				(MainWindowViewModel gamesVm, MainWindow gamesWindow) = await AuditRenderTests.OpenGames(code, width, height);
				Localizer.Chosen = language;
				try
				{
					Save(gamesWindow, langDir, "games.png");

					GameItemViewModel running = gamesVm.SupportedGames.Games.Single(g => g.Name == AuditRenderTests.MadeUpGame);
					gamesVm.SupportedGames.OpenGameCommand.Execute(running);
					gamesVm.SupportedGames.Panel.UseOwnSettingsCommand.Execute(null);
					gamesVm.SupportedGames.Panel.SaveProfileNameCommand.Execute(null);

					gamesVm.SupportedGames.Panel.SelectedSegment = GamePanelViewModel.GameSegment;
					Save(gamesWindow, langDir, "dialog-game-panel-game.png");

					gamesVm.SupportedGames.Panel.SelectedSegment = GamePanelViewModel.VibrationSegment;
					Save(gamesWindow, langDir, "dialog-game-panel-vibration.png");

					gamesVm.SupportedGames.Panel.SelectedSegment = GamePanelViewModel.TriggersSegment;
					Save(gamesWindow, langDir, "dialog-game-panel-triggers.png");
				}
				finally
				{
					gamesWindow.Close();
				}

				// Setup wizard page
				Localizer.Chosen = language;
				var probes = new RealServiceTests.Probes(drivers: false);
				var setupVm = new MainWindowViewModel(
					new FakeControllerService(animate: false),
					new AppEnvironment(
						new MemorySettingsStore(new AppSettings { Language = code }),
						null,
						() => new SetupWizard(new SystemCheck(probes, probes, probes, probes), new RealServiceTests.Links())));
				Localizer.Chosen = language;
				var setupWindow = new MainWindow { DataContext = setupVm, Width = width, Height = height };
				setupWindow.Show();
				try
				{
					Save(setupWindow, langDir, "setup.png");
				}
				finally
				{
					setupWindow.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static IReadOnlyList<Language> SelectedLanguages(string? filter)
	{
		if (string.IsNullOrWhiteSpace(filter))
		{
			return Localizer.Available;
		}
		return filter
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(c => new Language(c))
			.ToArray();
	}

	private static double ParseDimension(string envName, double fallback) =>
		double.TryParse(Environment.GetEnvironmentVariable(envName), out double v) && v >= 640 ? v : fallback;

	private static void Save(Window window, string dir, string fileName)
	{
		FitCheck.Settle(window);
		Thread.Sleep(200);
		FitCheck.Settle(window);
		using var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		frame!.Save(Path.Combine(dir, fileName));
	}
}
