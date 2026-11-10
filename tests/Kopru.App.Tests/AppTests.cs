// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
				or nameof(SupportedGamesViewModel.SteamText) or nameof(OverviewViewModel.PrimaryTip)
				or nameof(OverviewViewModel.LastBatteryText) or nameof(OverviewViewModel.HeroActionText)))
			.Select(p => (p.Name, (string?)p.GetValue(o)));

	[AvaloniaFact]
	public void EveryPageViewModel_Builds_WithNonEmptyTexts()
	{
		foreach (Language language in new[] { Language.Turkish, Language.English })
		{
			WithLanguage(language, () =>
			{
				MainWindowViewModel vm = NewViewModel();
				Assert.Equal(6, vm.Pages.Count);
				Assert.Equal(vm.Pages.Count, vm.Pages.Select(p => p.Title).Distinct().Count());
				foreach (object o in vm.Pages.Cast<object>().Append(vm).Append(vm.Settings).Append(vm.Sticks).Append(vm.Tester))
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
			Assert.Contains(Texts(window), t => t == "Genel Bakış");

			var changed = new List<string?>();
			vm.Overview.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
			vm.SettingsPage.SelectedLanguage = vm.SettingsPage.Languages.Single(o => o.Language == Language.English);
			Dispatcher.UIThread.RunJobs();

			Assert.Equal(Language.English, Localizer.Current);
			Assert.NotEmpty(changed);
			Assert.Equal("Overview", vm.Overview.Title);
			Assert.Equal("Start Virtual Controller", vm.Overview.BridgeButtonText);
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
				// Colour resources and numeric design tokens (radius, spacing, font size: "8", "20,18", "12.5")
				if (!Regex.IsMatch(m.Groups[1].Value, "^#[0-9A-Fa-f]{6,8}$") && !Regex.IsMatch(m.Groups[1].Value, @"^-?\d+(\.\d+)?(,-?\d+(\.\d+)?){0,3}$"))
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
		string[] names = ["overview", "supported-games", "other-games", "controller", "light", "settings"];
		foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
		{
			WithLanguage(language, () =>
			{
				foreach (ControllerKind kind in new[] { ControllerKind.DualSense, ControllerKind.DualSenseEdge })
				{
					// With a list updater (checked just now, so nothing is requested) to show the list update row.
					MainWindowViewModel vm = new(new FakeControllerService(kind, animate: false), new AppEnvironment(
						new MemorySettingsStore(new AppSettings
						{
							GameListCheckedAt = DateTimeOffset.Now,
							Language = code,
							AlwaysGames = ["Returnal"],
							UserGames = [new Kopru.Core.Games.UserGame("Indie Game", @"C:\Games\Indie\Indie.exe")],
							GamePadTypes = new() { [Kopru.Core.Games.ForegroundGame.KeyOf(null, "Indie.exe")] = Kopru.Core.Virtual.VirtualPadType.Xbox360 }
						}),
						GameList: new Kopru.Core.Games.GameCatalogUpdater(new HttpClient(), Path.Combine(Path.GetTempPath(), "kopru-render-games.json"), "https://example.invalid/list.json")));
					var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
					window.Show();
					for (int i = 0; i < vm.NavItems.Count; i++)
					{
						if (kind == ControllerKind.DualSenseEdge && i > 0)
						{
							break;
						}
						if (vm.NavItems[i].Page is ControllerPageViewModel)
						{
							continue; // Every tab rendered with live input by TesterTests.
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
						vm.Overview.IsSupportedModeOn = true;
						var running = (FakeControllerService)ServiceOf(vm);
						running.Tick();
						// 30 s of live history (a varying delay), with the speaker and the microphone on.
						vm.Settings.SpeakerEnabled = true;
						vm.Settings.MicEnabled = true;
						running.MicLevel = 0.35;
						for (int s = 0; s < Kopru.App.ViewModels.LivePanelViewModel.HistoryLength; s++)
						{
							running.Latency = new(0.9 + 0.35 * Math.Sin(s / 3.0), 2.6 + 0.9 * Math.Sin(s / 2.0 + 1), 0.05);
							vm.Overview.Live.Sample();
						}
						vm.Overview.Live.Follow();
						Settle();
						Assert.True(vm.Overview.IsRunning);
						Assert.Equal(Kopru.App.ViewModels.LivePanelViewModel.HistoryLength, vm.Overview.Live.TypicalHistory.Count);
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

						// States from the health model: never paired, Bluetooth off, paired but asleep (with the last battery),
						// on a cable, a bridge problem; then connected with the bridge off.
						vm.SelectedNav = vm.NavItems[0];
						fake.SteamInput = null;
						if (vm.Overview.IsBridgeRunning)
						{
							vm.Overview.ToggleBridgeCommand.Execute(null);
						}
						fake.LastKnownBattery = new Kopru.Core.Health.LastKnownBattery(62, false, DateTimeOffset.Now.AddHours(-2));
						foreach ((string name, Kopru.Core.Health.HealthReport report, bool connected) in new[]
						{
							("overview-not-paired", OverviewHealthTests.Report(paired: false, noPad: true), false),
							("overview-bluetooth-off", OverviewHealthTests.Report(radio: Kopru.Core.Setup.RadioState.Off, noPad: true), false),
							("overview-disconnected", OverviewHealthTests.Report(noPad: true), false),
							("overview-cable", OverviewHealthTests.Report(pad: OverviewHealthTests.UsbPad), false),
							("overview-problem", OverviewHealthTests.Report(bridge: Kopru.Core.Bridge.BridgeState.Error, error: Kopru.Core.Bridge.BridgeError.UsbIpMissing,
								adapter: new Kopru.Core.Health.AdapterSummary(Kopru.Core.Diagnostics.AdapterVerdict.Borderline, DateTimeOffset.Now.AddDays(-3))), true),
							("overview-connected", OverviewHealthTests.Report(), true),
						})
						{
							fake.IsConnected = connected;
							fake.Health = report;
							fake.RaiseStateChanged();
							Settle();
							Assert.Equal(!connected, vm.Overview.ShowsNoController);
							using var state = window.CaptureRenderedFrame();
							Assert.NotNull(state);
							if (writeFiles)
							{
								state!.Save(Path.Combine(outDir, $"{name}-{code}.png"));
							}
						}

						// Low Power on: the rows it overrides dimmed with the user's own values, the note under the switch.
						vm.Settings.LowPowerOn = true;
						Settle();
						Assert.False(vm.Overview.Quick.CanEditOverridden);
						using (var low = window.CaptureRenderedFrame())
						{
							Assert.NotNull(low);
							if (writeFiles)
							{
								low!.Save(Path.Combine(outDir, $"overview-low-power-{code}.png"));
							}
						}
						vm.Settings.LowPowerOn = false;

						// Settings scrolled to the switch for Low Power below 20 % battery.
						vm.Navigate(vm.SettingsPage);
						Settle();
						ToggleSwitch auto = window.GetVisualDescendants().OfType<ToggleSwitch>().First(t => AutomationProperties.GetName(t) == Text.AutoLowPower());
						auto.BringIntoView(new Avalonia.Rect(0, 0, auto.Bounds.Width, 260));
						Settle();
						using (var settingsFrame = window.CaptureRenderedFrame())
						{
							Assert.NotNull(settingsFrame);
							if (writeFiles)
							{
								settingsFrame!.Save(Path.Combine(outDir, $"settings-low-power-{code}.png"));
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
				// Setup page with the required steps done (adapter test not run), then with the adapter test half-way.
				var doneProbes = new RealServiceTests.Probes(drivers: true);
				var setupService = (FakeControllerService)ServiceOf(setupVm);
				var setupClock = new SetupFlowTests.FakeClock();
				var complete = new SetupViewModel(new Kopru.Core.Setup.SetupWizard(new Kopru.Core.Setup.SystemCheck(doneProbes, doneProbes, doneProbes, doneProbes),
					new RealServiceTests.Links()), setupService, setupVm.CloseSetup, clock: setupClock, tick: _ => Task.Delay(Timeout.Infinite));
				setupVm.Setup = complete;
				Settle();
				using (var frame = setupWindow.CaptureRenderedFrame())
				{
					Assert.NotNull(frame);
					if (writeFiles)
					{
						frame!.Save(Path.Combine(outDir, $"setup-complete-{code}.png"));
					}
				}
				setupService.AdapterGate = new TaskCompletionSource<AdapterTestResult>();
				Task adapterRun = complete.RunAsync(complete.Wizard.Steps.Single(s => s.Id == Kopru.Core.Setup.SetupStepId.AdapterTest).Actions.Single());
				setupClock.Elapsed = TimeSpan.FromSeconds(6.5);
				complete.RefreshProgress();
				Settle();
				using (var frame = setupWindow.CaptureRenderedFrame())
				{
					Assert.NotNull(frame);
					if (writeFiles)
					{
						frame!.Save(Path.Combine(outDir, $"setup-adapter-test-{code}.png"));
					}
				}
				complete.CancelTestCommand.Execute(null);
				Settle();
				Assert.True(adapterRun.IsCompleted);
				setupService.AdapterGate = null;
				// Overview with the driver missing: the driver chip is red and the main action installs it.
				setupVm.Setup = null;
				var setupFake = (FakeControllerService)ServiceOf(setupVm);
				setupFake.Health = OverviewHealthTests.Report(driver: false);
				setupFake.RaiseStateChanged();
				Settle();
				Assert.True(setupVm.Overview.Chip(Kopru.Core.Health.HealthChipKind.Driver).IsProblem);
				Assert.Equal(Text.FixInstallDriver(), setupVm.Overview.PrimaryText);
				using (var frame = setupWindow.CaptureRenderedFrame())
				{
					Assert.NotNull(frame);
					if (writeFiles)
					{
						frame!.Save(Path.Combine(outDir, $"overview-setup-{code}.png"));
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
