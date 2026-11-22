// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Diagnostics;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The Overview's live card with realistic data: a minute of a good link with a short borderline stretch and a few seconds
// that lost reports, the triggers, haptics and microphone moving; and a weak link with spikes over the "you can feel it"
// limit. Both views, Turkish and English, at 1100x700 and 1152x700. With KOPRU_LIVE_RENDER_DIR set each is saved as
// live-{link|signals}-{w}x{h}-{lang}.png (the weak link as live-link-weak-...).
public sealed class LiveCardRenderTests
{
	public static readonly string[] States = ["good-rest", "good-active", "weak"];

	[AvaloniaFact]
	public void Render_LiveCard_BothViews()
	{
		string? dir = Environment.GetEnvironmentVariable("KOPRU_LIVE_RENDER_DIR") is { Length: > 0 } d ? d : null;
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.English, "en"), (Language.Turkish, "tr") })
			{
				Localizer.Chosen = language;
				foreach (string state in new[] { "good-active", "weak" })
				{
					(MainWindowViewModel vm, FakeControllerService fake, MainWindow window) = Show(state);
					foreach ((double w, double h) in new[] { (1100.0, 700.0), (1152.0, 700.0) })
					{
						window.Width = w;
						window.Height = h;
						foreach (int view in state == "weak" ? new[] { LivePanelViewModel.LinkView } : new[] { LivePanelViewModel.LinkView, LivePanelViewModel.SignalsView })
						{
							vm.Overview.Live.SelectedView = view;
							if (view == LivePanelViewModel.SignalsView)
							{
								Animate(vm, fake);
							}
							Settle();
							using var frame = window.CaptureRenderedFrame();
							Assert.NotNull(frame);
							if (dir != null)
							{
								Directory.CreateDirectory(dir);
								string name = view == LivePanelViewModel.SignalsView ? "signals" : state == "weak" ? "link-weak" : "link";
								frame!.Save(Path.Combine(dir, $"live-{name}-{w}x{h}-{code}.png"));
							}
						}
					}
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The Overview with the bridge running and a minute of history shaped like the state.
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
			if (weak)
			{
				fake.AddSecond(6 + 2 * wave, s % 6 == 2 ? 48 + 6 * wave : 14 + 5 * wave, 560 + 30 * wave, lost: s % 7 == 3 ? 9 : 0, longGaps: s % 11 == 5 ? 2 : 0);
			}
			else
			{
				// A good link with a borderline stretch around 20-28 s ago and three seconds that lost a few reports.
				bool borderline = s is >= 32 and <= 40;
				double worst = borderline ? 15 + 5 * Math.Sin((s - 32) / 8.0 * Math.PI) + 2 * wave : 4.4 + 0.8 * wave;
				fake.AddSecond(borderline ? 2.4 + 0.6 * wave : 1.0 + 0.2 * wave, worst, borderline ? 640 + 20 * wave : 718 + 6 * wave,
					lost: s is 12 or 35 or 38 ? 3 : 0, longGaps: s == 36 ? 1 : 0);
			}
		}
		vm.Overview.Live.Sample();
		fake.LinkQuality = weak ? new LinkQualityReading(LinkQuality.Poor, 48, 1.4, 6, 560) : new LinkQualityReading(LinkQuality.Good, 4.6, 0, 0, 718);
		if (state == "good-active")
		{
			vm.Settings.MicEnabled = true;
			fake.MicLevel = 0.4;
			fake.RawInput = [168, 96, 131, 125, 178, 64, 0, 0, 0];
			fake.SetHapticsLevels(0.62, 0.35);
		}
		else
		{
			fake.RawInput = [128, 128, 128, 128, 0, 0, 0, 0, 0];
			fake.SetHapticsLevels(0, 0);
		}
		fake.RaiseStateChanged();
	}

	// Ten seconds of play into the Signals rings: trigger squeezes (L2 held longer, R2 in short pulls), haptics that swell
	// with the game's sound (the right motor a little behind), and speech-like microphone bursts.
	internal static void Animate(MainWindowViewModel vm, FakeControllerService fake)
	{
		for (int i = 0; i < LivePanelViewModel.SignalSamples; i++)
		{
			double t = i * LivePanelViewModel.SignalInterval.TotalSeconds;
			double l2 = Math.Pow(Math.Max(0, Math.Sin(t * 0.9 - 0.4)), 0.6) * 0.92;
			double pull = Math.Max(0, Math.Sin(t * 2.6 + 1));
			double r2 = pull > 0.55 ? (pull - 0.55) / 0.45 * 0.85 : 0;
			double beat = 0.5 + 0.5 * Math.Sin(t * 5.1);
			double swell = 0.35 + 0.3 * Math.Sin(t * 0.7 + 0.5);
			double left = Math.Clamp(swell * (0.55 + 0.45 * beat) + 0.03 * Math.Sin(t * 23), 0, 1);
			double right = Math.Clamp((swell - 0.06) * (0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(t * 5.1 - 0.9))) + 0.03 * Math.Sin(t * 19 + 2), 0, 1);
			double word = Math.Max(0, Math.Sin(t * 1.9 + 0.9)) * (0.55 + 0.45 * Math.Abs(Math.Sin(t * 11.0)));
			fake.RawInput = [128, 128, 128, 128, (byte)Math.Round(l2 * 255), (byte)Math.Round(r2 * 255), 0, 0, 0];
			fake.SetHapticsLevels(left, right);
			fake.MicLevel = Math.Clamp(word * 0.8, 0, 1);
			vm.Overview.Live.Follow();
		}
	}

	internal static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
	}
}
