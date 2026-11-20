// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Diagnostics;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The Overview's live card in three states (a good link at rest, a good link with the triggers and haptics moving, a weak
// link with spikes over the "you can feel it" limit), Turkish, at 1100x700 and 1920x1080. With KOPRU_LIVE_RENDER_DIR set
// each is saved as live-{state}-{w}x{h}-tr.png.
public sealed class LiveCardRenderTests
{
	public static readonly string[] States = ["good-rest", "good-active", "weak"];

	[AvaloniaFact]
	public void Render_LiveCard_States()
	{
		string? dir = Environment.GetEnvironmentVariable("KOPRU_LIVE_RENDER_DIR") is { Length: > 0 } d ? d : null;
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.Turkish;
			foreach (string state in States)
			{
				(MainWindowViewModel vm, FakeControllerService fake, MainWindow window) = Show(state);
				foreach ((double w, double h) in new[] { (1100.0, 700.0), (1920.0, 1080.0) })
				{
					window.Width = w;
					window.Height = h;
					Settle();
					using var frame = window.CaptureRenderedFrame();
					Assert.NotNull(frame);
					if (dir != null)
					{
						Directory.CreateDirectory(dir);
						frame!.Save(Path.Combine(dir, $"live-{state}-{w}x{h}-tr.png"));
					}
				}
				window.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The Overview with the bridge running and 30 s of history shaped like the state.
	internal static (MainWindowViewModel Vm, FakeControllerService Fake, MainWindow Window) Show(string state)
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		vm.SelectedNav = vm.NavItems[0];
		vm.Overview.ToggleBridgeCommand.Execute(null);
		fake.Tick();
		Apply(vm, fake, state);
		Settle();
		return (vm, fake, window);
	}

	internal static void Apply(MainWindowViewModel vm, FakeControllerService fake, string state)
	{
		bool weak = state == "weak";
		for (int s = 0; s < LivePanelViewModel.HistoryLength; s++)
		{
			double wave = Math.Sin(s / 2.3);
			(double typical, double worst) = weak ? (6 + 2 * wave, s % 6 == 2 ? 48 + 6 * wave : 14 + 5 * wave) : (1.0 + 0.2 * wave, 4.4 + 0.8 * wave);
			fake.Latency = new(typical, worst, 0.05);
			vm.Overview.Live.Sample();
		}
		fake.LinkQuality = weak ? new LinkQualityReading(LinkQuality.Poor, 48, 1.4, 6, 560) : new LinkQualityReading(LinkQuality.Good, 4.6, 0, 0, 718);
		if (state == "good-active")
		{
			fake.RawInput = [168, 96, 131, 125, 178, 64, 0, 0, 0];
			fake.SetHapticsLevels(0.62, 0.35);
		}
		else
		{
			fake.RawInput = [128, 128, 128, 128, 0, 0, 0, 0, 0];
			fake.SetHapticsLevels(0, 0);
		}
		fake.RaiseStateChanged();
		vm.Overview.Live.Follow();
	}

	internal static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
	}
}
