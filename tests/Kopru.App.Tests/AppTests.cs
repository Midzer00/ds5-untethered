// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

public class AppTests
{
	private static MainWindowViewModel NewViewModel(ControllerKind kind = ControllerKind.DualSense) =>
		new(new FakeControllerService(kind, animate: false));

	private static void WithLanguage(Language? language, Action action)
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = language;
			action();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static IEnumerable<(string Name, string? Value)> StringProperties(object o) =>
		o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Where(p => p.PropertyType == typeof(string) && p.Name is not (nameof(SettingsViewModel.ToolResult) or nameof(OverviewViewModel.Warning) or nameof(SupportedGamesViewModel.SearchText)
				or nameof(SticksViewModel.CalibrationText) or nameof(OverviewViewModel.PowerOffResult) or nameof(OverviewViewModel.SteamWarning)
				or nameof(SupportedGamesViewModel.SteamText)))
			.Select(p => (p.Name, (string?)p.GetValue(o)));

	[AvaloniaFact]
	public void EveryPageViewModel_Builds_WithNonEmptyTexts()
	{
		foreach (Language language in new[] { Language.Turkish, Language.English })
		{
			WithLanguage(language, () =>
			{
				MainWindowViewModel vm = NewViewModel();
				Assert.Equal(7, vm.Pages.Count);
				Assert.Equal(vm.Pages.Count, vm.Pages.Select(p => p.Title).Distinct().Count());
				foreach (object o in vm.Pages.Cast<object>().Append(vm).Append(vm.Settings))
				{
					foreach ((string name, string? value) in StringProperties(o))
					{
						Assert.False(string.IsNullOrWhiteSpace(value), $"{o.GetType().Name}.{name} is empty ({language})");
					}
				}
				foreach (object o in new object[] { vm.Sticks.LeftStick, vm.Sticks.RightStick, vm.Sticks.L2, vm.Sticks.R2 })
				{
					foreach ((string name, string? value) in StringProperties(o))
					{
						Assert.False(string.IsNullOrWhiteSpace(value), $"{o.GetType().Name}.{name} is empty ({language})");
					}
				}
				Assert.Equal(4, vm.Triggers.Effects.Count);
				Assert.Equal(6, vm.Light.Presets.Count);
			});
		}
	}

	[AvaloniaFact]
	public void QuickSettings_HaveRecommendedDefaults_AndReachTheService()
	{
		var service = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(service);
		Assert.Equal(150, vm.Settings.GameHapticsLevel);
		Assert.Equal(100, vm.Settings.RumbleLevel);
		vm.Overview.Settings.GameHapticsLevel = 120;
		Assert.Equal(120, vm.SupportedGames.Settings.GameHapticsLevel);
		Assert.Equal(120, service.Settings.GameHapticsLevel);
	}

	[AvaloniaFact]
	public async Task BridgeButton_TogglesState()
	{
		var vm = NewViewModel();
		Assert.False(vm.Overview.IsBridgeRunning);
		await vm.Overview.ToggleBridgeCommand.ExecuteAsync(null);
		Assert.True(vm.Overview.IsBridgeRunning);
		await vm.Overview.ToggleBridgeCommand.ExecuteAsync(null);
		Assert.False(vm.Overview.IsBridgeRunning);
	}

	[AvaloniaFact]
	public void LanguageSwitch_UpdatesViewModelsAndRenderedText()
	{
		WithLanguage(Language.Turkish, () =>
		{
			MainWindowViewModel vm = NewViewModel();
			var window = new MainWindow { DataContext = vm };
			window.Show();
			Dispatcher.UIThread.RunJobs();
			Assert.Contains(Texts(window), t => t == "Genel bakış");

			var changed = new List<string?>();
			vm.Overview.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
			vm.SettingsPage.SelectedLanguage = vm.SettingsPage.Languages.Single(o => o.Language == Language.English);
			Dispatcher.UIThread.RunJobs();

			Assert.Equal(Language.English, Localizer.Current);
			Assert.NotEmpty(changed);
			Assert.Equal("Overview", vm.Overview.Title);
			Assert.Equal("Start bridge", vm.Overview.BridgeButtonText);
			Assert.Equal("Off", vm.Triggers.Effects[0].Name);
			IReadOnlyList<string> texts = Texts(window);
			Assert.Contains("Overview", texts);
			Assert.Contains("Settings", texts);
			Assert.DoesNotContain("Genel bakış", texts);
			window.Close();
		});
	}

	[Fact]
	public void AppName_IsTheSameInEveryLanguage()
	{
		// The product name is never translated and keeps its exact casing.
		Assert.Equal("DS5 Untethered", AppInfo.Name);
	}

	[Fact]
	public void Views_HaveNoHardCodedStrings()
	{
		string dir = Path.Combine(RepoRoot(), "src", "Kopru.App");
		var attribute = new Regex(@"\b(Text|Content|Header|Title|ToolTip\.Tip|Watermark|PlaceholderText|OnContent|OffContent)=""([^""]*)""");
		var problems = new List<string>();
		foreach (string file in Directory.EnumerateFiles(dir, "*.axaml", SearchOption.AllDirectories))
		{
			foreach (Match m in attribute.Matches(File.ReadAllText(file)))
			{
				string value = m.Groups[2].Value;
				if (!value.StartsWith('{'))
				{
					problems.Add($"{Path.GetFileName(file)}: {m.Value}");
				}
			}
			// Text between tags (e.g. <TextBlock>Hello</TextBlock>) is not allowed either.
			foreach (Match m in Regex.Matches(File.ReadAllText(file), @">\s*([^<>\s][^<>]*?)\s*</"))
			{
				if (!Regex.IsMatch(m.Groups[1].Value, "^#[0-9A-Fa-f]{6,8}$")) // colour resources
				{
					problems.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
				}
			}
		}
		Assert.Empty(problems);
	}

	[AvaloniaFact]
	public void FakeService_SimulatesChanges()
	{
		var service = new FakeControllerService(ControllerKind.DualSenseEdge, animate: false);
		var vm = new MainWindowViewModel(service);
		Assert.True(vm.IsEdge);
		Assert.Equal("DualSense Edge", vm.DeviceName);
		int raised = 0;
		vm.Overview.PropertyChanged += (_, _) => raised++;
		double before = service.BatteryPercent;
		service.Tick();
		Dispatcher.UIThread.RunJobs();
		Assert.True(raised > 0);
		Assert.True(service.BatteryPercent < before);
		Assert.InRange(service.InputRateHz, 900, 1100);
	}

	// Renders every page in both languages to docs/screens (also checks the views build and render without errors).
	[AvaloniaFact]
	public void RenderScreens()
	{
		// Writes PNGs to docs/screens only when KOPRU_RENDER_SCREENS=1; otherwise renders to memory.
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string outDir = Path.Combine(RepoRoot(), "docs", "screens");
		if (writeFiles)
		{
			Directory.CreateDirectory(outDir);
		}
		string[] names = ["overview", "supported-games", "other-games", "triggers", "sticks", "light", "settings"];
		foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
		{
			WithLanguage(language, () =>
			{
				foreach (ControllerKind kind in new[] { ControllerKind.DualSense, ControllerKind.DualSenseEdge })
				{
					MainWindowViewModel vm = NewViewModel(kind);
					var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
					window.Show();
					for (int i = 0; i < vm.NavItems.Count; i++)
					{
						if (kind == ControllerKind.DualSenseEdge && i > 0)
						{
							break;
						}
						vm.SelectedNav = vm.NavItems[i];
						Settle();
						using var frame = window.CaptureRenderedFrame();
						Assert.NotNull(frame);
						string suffix = kind == ControllerKind.DualSenseEdge ? "-edge" : "";
						using var png = new MemoryStream();
						frame!.Save(png);
						Assert.True(png.Length > 0);
						if (writeFiles)
						{
							File.WriteAllBytes(Path.Combine(outDir, $"{names[i]}{suffix}-{code}.png"), png.ToArray());
						}
					}
					if (kind == ControllerKind.DualSense)
					{
						// Overview with the bridge running and a (simulated) game connected.
						vm.SelectedNav = vm.NavItems[0];
						vm.Overview.ToggleBridgeCommand.Execute(null);
						((FakeControllerService)ServiceOf(vm)).Tick();
						Settle();
						Assert.True(vm.Overview.IsRunning);
						using var frame = window.CaptureRenderedFrame();
						Assert.NotNull(frame);
						if (writeFiles)
						{
							frame!.Save(Path.Combine(outDir, $"overview-running-{code}.png"));
						}

						// The same page in a large window: the layout grows with it.
						window.Width = 1600;
						window.Height = 1000;
						Settle();
						using var wide = window.CaptureRenderedFrame();
						Assert.NotNull(wide);
						if (writeFiles)
						{
							wide!.Save(Path.Combine(outDir, $"overview-wide-{code}.png"));
						}

						// Steam Input on: the warning on Overview and on the supported games page.
						window.Width = 1100;
						window.Height = 700;
						var fake = (FakeControllerService)ServiceOf(vm);
						fake.SteamInput = new Kopru.Core.Steam.SteamInputResult(Kopru.Core.Steam.SteamInputStatus.OnGlobally, true,
							Text.SteamInputAdvice(Kopru.Core.Steam.SteamInputStatus.OnGlobally));
						fake.Tick();
						foreach ((int page, string name) in new[] { (0, "overview-steam"), (1, "supported-games-steam") })
						{
							vm.SelectedNav = vm.NavItems[page];
							Settle();
							using var steam = window.CaptureRenderedFrame();
							Assert.NotNull(steam);
							if (writeFiles)
							{
								steam!.Save(Path.Combine(outDir, $"{name}-{code}.png"));
							}
						}
					}
					window.Close();
				}
				// Setup page (first run with the drivers missing).
				var probes = new RealServiceTests.Probes(drivers: false);
				var setupVm = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(new MemorySettingsStore(), null,
					() => new Kopru.Core.Setup.SetupWizard(new Kopru.Core.Setup.SystemCheck(probes, probes, probes, probes), new RealServiceTests.Links())));
				var setupWindow = new MainWindow { DataContext = setupVm, Width = 1100, Height = 700 };
				setupWindow.Show();
				Settle();
				Assert.IsType<SetupViewModel>(setupVm.CurrentPage);
				using (var frame = setupWindow.CaptureRenderedFrame())
				{
					Assert.NotNull(frame);
					if (writeFiles)
					{
						frame!.Save(Path.Combine(outDir, $"setup-{code}.png"));
					}
				}
				setupWindow.Close();
			});
		}
	}

	// Lets the 150-250 ms hover/selection transitions finish before a frame is captured.
	private static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Thread.Sleep(320);
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
	}

	private static IControllerService ServiceOf(MainWindowViewModel vm) =>
		(IControllerService)typeof(MainWindowViewModel).GetField("_service", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;

	private static IReadOnlyList<string> Texts(Window window) =>
		window.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

	private static string RepoRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		return dir ?? throw new InvalidOperationException("Kopru.sln not found");
	}
}
