// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

// The setup page (not in the nav, so PageSizeRenderTests skips it) with the drivers missing and with the required steps done,
// at the smallest (1100x700) and a large (1680x1050) window, Turkish and English. Writes setup-{missing|done}-{small|large}-{tr|en}.png
// to KOPRU_PAGE_RENDER_DIR when it is set; otherwise only checks that the page renders.
public sealed class SetupRenderTests
{
	[AvaloniaFact]
	public void RenderSetup_AtBothSizes()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_PAGE_RENDER_DIR");
		if (!string.IsNullOrEmpty(outDir))
		{
			Directory.CreateDirectory(outDir);
		}
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height, string size) in PageSizeRenderTests.Sizes)
				{
					var probes = new RealServiceTests.Probes(drivers: false);
					var vm = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(new MemorySettingsStore(), null,
						() => new SetupWizard(new SystemCheck(probes, probes, probes, probes), new RealServiceTests.Links())));
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					Settle();
					Assert.IsType<SetupViewModel>(vm.CurrentPage);
					Capture(window, outDir, $"setup-missing-{size}-{code}.png");

					var done = new RealServiceTests.Probes(drivers: true);
					var service = (IControllerService)typeof(MainWindowViewModel).GetField("_service", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
					vm.Setup = new SetupViewModel(new SetupWizard(new SystemCheck(done, done, done, done), new RealServiceTests.Links()), service, vm.CloseSetup,
						clock: new SetupFlowTests.FakeClock(), tick: _ => Task.Delay(Timeout.Infinite));
					Settle();
					Capture(window, outDir, $"setup-done-{size}-{code}.png");
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static void Capture(Avalonia.Controls.Window window, string? outDir, string name)
	{
		using var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		if (!string.IsNullOrEmpty(outDir))
		{
			frame!.Save(Path.Combine(outDir, name));
		}
	}

	private static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Thread.Sleep(320);
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
	}
}
