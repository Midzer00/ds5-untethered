// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Diagnostics;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

// The link quality the user sees: the weak link toast and its rules, the Overview's state word, sentence and chart, and
// the adapter test's explanation on the setup page. Renders (KOPRU_RENDER_SCREENS=1) go to docs/screens:
// overview-link-{state}-{size}-{lang}.png and setup-adapter-{outcome}-{size}-{lang}.png.
public class LinkQualityAppTests
{
	private static readonly LinkQualityReading Good = new(LinkQuality.Good, 4.6, 0, 0, 640);
	private static readonly LinkQualityReading Borderline = new(LinkQuality.Borderline, 19.5, 0.2, 2, 610);
	private static readonly LinkQualityReading Poor = new(LinkQuality.Poor, 41, 1.4, 6, 520);

	private sealed class Harness
	{
		public long Now = 1_000_000;
		public bool FullScreen;
		public FakeControllerService Fake { get; }
		public MainWindowViewModel Vm { get; }
		public List<AppNotification> Raised { get; } = [];

		public Harness()
		{
			Fake = new FakeControllerService(animate: false) { IsConnected = true };
			Vm = new MainWindowViewModel(Fake, new AppEnvironment(null, FullScreen: () => FullScreen, Clock: () => Now));
			Vm.Sticks.StopPreview();
			Vm.Notifications.Raised += Raised.Add;
		}

		public void Link(LinkQualityReading? reading)
		{
			Fake.LinkQuality = reading;
			Fake.RaiseStateChanged();
		}

		public int Warnings => Raised.Count(n => n.Type == NotificationType.LinkWeak);
	}

	[AvaloniaFact]
	public void WeakLinkToast_ShowsOnce_WhenTheMonitorSaysItIsDue()
	{
		var h = new Harness();
		h.Link(Poor);
		Assert.Equal(0, h.Warnings);
		h.Link(Poor with { WarningDue = true });
		AppNotification toast = Assert.Single(h.Raised, n => n.Type == NotificationType.LinkWeak);
		Assert.Equal(NotificationKind.Warning, toast.Kind);
		Assert.Equal(Text.NotifyLinkTitle(), toast.Title);
		Assert.Equal(Text.NotifyLinkBody(), toast.Body);
		Assert.Equal(AppPage.Overview, toast.Page);
		Assert.Equal(1, h.Fake.LinkWarningsAcknowledged);
		// Acknowledged: the next state changes do not repeat it.
		h.Fake.RaiseStateChanged();
		h.Fake.RaiseStateChanged();
		Assert.Equal(1, h.Warnings);
	}

	[AvaloniaFact]
	public void WeakLinkToast_FollowsItsSwitch_AndStaysDueWhileOff()
	{
		var h = new Harness();
		h.Vm.Settings.NotifyLinkQuality = false;
		h.Link(Poor with { WarningDue = true });
		Assert.Equal(0, h.Warnings);
		Assert.Equal(0, h.Fake.LinkWarningsAcknowledged);
		h.Vm.Settings.NotificationsEnabled = false;
		h.Vm.Settings.NotifyLinkQuality = true;
		h.Fake.RaiseStateChanged();
		Assert.Equal(0, h.Warnings);
	}

	[AvaloniaFact]
	public void WeakLinkToast_RespectsQuietWhilePlaying_AndShowsAfterTheGameIfStillWeak()
	{
		var h = new Harness();
		h.Vm.Settings.QuietWhilePlaying = true;
		h.FullScreen = true;
		h.Link(Poor with { WarningDue = true });
		Assert.Equal(0, h.Warnings);
		Assert.Equal(0, h.Fake.LinkWarningsAcknowledged);
		h.FullScreen = false;
		h.Fake.RaiseStateChanged();
		Assert.Equal(1, h.Warnings);

		// With the setting off (the default), it shows during a game: that is when it matters.
		var playing = new Harness { FullScreen = true };
		playing.Link(Poor with { WarningDue = true });
		Assert.Equal(1, playing.Warnings);
	}

	[AvaloniaFact]
	public void WeakLinkToast_NotWithoutAController()
	{
		var h = new Harness();
		h.Fake.IsConnected = false;
		h.Link(Poor with { WarningDue = true });
		Assert.Equal(0, h.Warnings);
	}

	[AvaloniaFact]
	public void WeakLinkSwitch_IsSaved_AndOnByDefault()
	{
		Assert.True(new AppSettings().NotifyLinkQuality);
		var store = new MemorySettingsStore();
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(store));
		vm.Settings.NotifyLinkQuality = false;
		vm.Settings.SaveNow();
		Assert.False(vm.Settings.ToAppSettings().NotifyLinkQuality);
		var again = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(store));
		Assert.False(again.Settings.NotifyLinkQuality);
	}

	[AvaloniaFact]
	public void Overview_ShowsTheStateAsAColouredWord_AndOneSentence()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		OverviewViewModel o = vm.Overview;
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.Turkish;
			fake.LinkQuality = null;
			Assert.Equal("Ölçülüyor", o.LinkStateText);
			Assert.True(o.IsLinkMeasuring);
			Assert.False(o.HasLinkAdvice);
			// Good: the word says it all, no sentence.
			fake.LinkQuality = Good;
			Assert.Equal("İyi", o.LinkStateText);
			Assert.True(o.IsLinkGood && !o.IsLinkBorderline && !o.IsLinkPoor);
			Assert.False(o.HasLinkAdvice);
			fake.LinkQuality = Borderline;
			Assert.Equal("Orta", o.LinkStateText);
			Assert.True(o.IsLinkBorderline);
			Assert.True(o.HasLinkAdvice);
			Assert.Equal("Ara sıra gecikme var: kumandayı adaptöre yaklaştır.", o.LinkAdvice);
			fake.LinkQuality = Poor;
			Assert.Equal("Zayıf", o.LinkStateText);
			Assert.True(o.IsLinkPoor);
			Assert.Contains("USB uzatma kablosu", o.LinkAdvice);
			Assert.Contains("2,4 GHz", o.LinkAdvice);
			Localizer.Chosen = Language.English;
			Assert.Equal("Weak", o.LinkStateText);
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The chart's top: about 1.2 times the highest sample, rounded up to a round value, never under 10 ms.
	[Fact]
	public void Chart_ScalesToTheData_WithARoundTop()
	{
		Assert.Equal(10, LivePanelViewModel.ChartTopFor([]));
		Assert.Equal(10, LivePanelViewModel.ChartTopFor([1.0, 4.5, 6.6]));
		Assert.Equal(10, LivePanelViewModel.ChartTopFor([8.0]));
		Assert.Equal(25, LivePanelViewModel.ChartTopFor([19.5]));
		Assert.Equal(60, LivePanelViewModel.ChartTopFor([12, 48]));
		Assert.Equal(100, LivePanelViewModel.ChartTopFor([75]));
		Assert.Equal(50, LivePanelViewModel.ChartTopFor([38]));
		Assert.Equal(1500, LivePanelViewModel.ChartTopFor([900]));
	}

	// The chart scales to the data: a good link (a few ms) fills it on a 0-10 ms axis with the "you can feel it" line left
	// out and a note at the top instead; a weak link's spikes bring the scale up and the line in.
	[AvaloniaFact]
	public void Chart_ScalesToTheData_TheLimitOnlyInRange()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		vm.SelectedNav = vm.NavItems[0];
		vm.Overview.ToggleBridgeCommand.Execute(null);
		fake.Tick();
		fake.LinkQuality = Good;
		Feed(vm, fake, LinkQuality.Good);
		Settle();
		Sparkline graph = window.GetVisualDescendants().OfType<Sparkline>().Single(g => g.IsEffectivelyVisible);
		TextBlock note = window.GetVisualDescendants().OfType<TextBlock>().Single(b => b.Name == "ChartNote");
		Assert.Equal(10, graph.Maximum);
		Assert.Equal(0, graph.Limit);
		Assert.True(note.IsEffectivelyVisible);
		Assert.Equal(Text.LiveLast60s(), note.Text);
		Assert.Equal("ms", graph.Unit);
		// The area is the worst delay of each second (what the limit is about), in the verdict's colour; the thin line the
		// typical delay.
		Assert.Same(vm.Overview.Live.WorstHistory, graph.Values);
		Assert.Same(vm.Overview.Live.TypicalHistory, graph.Secondary);
		Assert.Equal(((ISolidColorBrush)window.FindResource("IndicatorOnBrush")!).Color, ((ISolidColorBrush)graph.Stroke!).Color);
		// The worst sample reaches at least half the height: the line uses the chart.
		double top = graph.YOf(graph.Maximum), bottom = graph.YOf(0);
		Assert.True(graph.YOf(vm.Overview.Live.WorstHistory.Max()) < (top + bottom) / 2, "the line sits low in an empty chart");
		// No gutter: the plot takes the full width, the scale label sits inside it at the top right.
		Assert.Equal(graph.Bounds.Width, graph.PlotWidth);

		fake.LinkQuality = Poor;
		Feed(vm, fake, LinkQuality.Poor);
		Settle();
		Assert.Equal(LinkLimits.PoorDelayMs, graph.Limit);
		Assert.True(graph.Maximum >= 1.2 * 75 - 0.5, $"top {graph.Maximum}");
		Assert.Equal(((ISolidColorBrush)window.FindResource("DangerBrush")!).Color, ((ISolidColorBrush)graph.Stroke!).Color);
		Assert.Equal(top, graph.YOf(graph.Maximum * 3), 3);
		window.Close();
	}

	// The Overview in each link state and the setup step in each test outcome, Turkish and English, at the default and a
	// large window. Files only with KOPRU_RENDER_SCREENS=1.
	[AvaloniaFact]
	public void Render_OverviewLinkStates_AndAdapterOutcomes()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string outDir = Path.Combine(RepoRoot(), "docs", "screens");
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double w, double h, string size) in new[] { (1100.0, 700.0, "default"), (1680.0, 1050.0, "large") })
				{
					foreach ((LinkQualityReading? reading, string name) in new[] { (Good, "good"), (Borderline, "borderline"), (Poor, "poor"), ((LinkQualityReading?)LinkQualityReading.Measuring, "measuring") })
					{
						var fake = new FakeControllerService(animate: false);
						var vm = new MainWindowViewModel(fake);
						var window = new MainWindow { DataContext = vm, Width = w, Height = h };
						window.Show();
						vm.SelectedNav = vm.NavItems[0];
						vm.Overview.ToggleBridgeCommand.Execute(null);
						fake.Tick();
						fake.LinkQuality = reading;
						if (reading!.State != LinkQuality.Measuring)
						{
							Feed(vm, fake, reading.State);
						}
						fake.RaiseStateChanged();
						Settle();
						OverviewView overview = window.GetLogicalDescendants().OfType<OverviewView>().Single();
						TextBlock word = overview.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "LinkWord");
						Assert.Equal(vm.Overview.LinkStateText, word.Text);
						Assert.True(word.IsEffectivelyVisible);
						// The sentence only when something must be done (Borderline, Weak), inside its card.
						TextBlock sentence = overview.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "LinkAdvice");
						Assert.Equal(reading.State is LinkQuality.Borderline or LinkQuality.Poor, sentence.IsEffectivelyVisible);
						Border card = overview.GetLogicalDescendants().OfType<Border>().First(b => b.Classes.Contains("panel") && b.GetLogicalDescendants().Contains(sentence));
						Point? end = sentence.TranslatePoint(new Point(0, sentence.Bounds.Height), card);
						Assert.True(end!.Value.Y <= card.Bounds.Height, $"{code} {size} {name}: the sentence ends below its card");
						using var frame = window.CaptureRenderedFrame();
						Assert.NotNull(frame);
						if (writeFiles)
						{
							frame!.Save(Path.Combine(outDir, $"overview-link-{name}-{size}-{code}.png"));
						}
						window.Close();
					}

					foreach ((AdapterTestResult? result, string name) in Outcomes())
					{
						var probes = new RealServiceTests.Probes(drivers: true);
						var fake = new FakeControllerService(animate: false);
						var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(), null,
							() => new SetupWizard(new SystemCheck(probes, probes, probes, probes), new RealServiceTests.Links())));
						var window = new MainWindow { DataContext = vm, Width = w, Height = h };
						window.Show();
						var setup = new SetupViewModel(new SetupWizard(new SystemCheck(probes, probes, probes, probes), new RealServiceTests.Links()), fake, vm.CloseSetup,
							clock: new SetupFlowTests.FakeClock(), tick: _ => Task.Delay(Timeout.Infinite));
						vm.Setup = setup;
						setup.ShowAdapterResult(result);
						Settle();
						SetupView view = window.GetLogicalDescendants().OfType<SetupView>().Single();
						TextBlock headline = view.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == setup.AdapterHeadline && t.Classes.Contains("state"));
						Assert.True(headline.IsEffectivelyVisible);
						((Control)headline.Parent!).BringIntoView();
						Settle();
						using var frame = window.CaptureRenderedFrame();
						Assert.NotNull(frame);
						if (writeFiles)
						{
							frame!.Save(Path.Combine(outDir, $"setup-adapter-{name}-{size}-{code}.png"));
						}
						window.Close();
					}
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	[AvaloniaFact]
	public void SetupStep_ShowsTheVerdictTheValuesAndTheAdvice()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.Turkish;
			var probes = new RealServiceTests.Probes(drivers: true);
			var fake = new FakeControllerService(animate: false);
			var setup = new SetupViewModel(new SetupWizard(new SystemCheck(probes, probes, probes, probes), new RealServiceTests.Links()), fake, () => { },
				clock: new SetupFlowTests.FakeClock(), tick: _ => Task.Delay(Timeout.Infinite));
			SetupStepItem step = setup.Steps.Single(s => s.Id == SetupStepId.AdapterTest);
			Assert.Same(setup, step.AdapterTest);
			Assert.False(setup.HasAdapterResult);

			setup.ShowAdapterResult(FakeControllerService.GoodAdapter());
			Assert.True(setup.HasAdapterResult && setup.IsAdapterGood);
			Assert.Equal(Text.AdapterHeadlineSuitable(), setup.AdapterHeadline);
			Assert.Equal(["Saniyede 640 rapor", "En kötü gecikme 4,8 ms", "Kayıp rapor yok"], setup.AdapterLines.Select(l => l.Measured));
			Assert.All(setup.AdapterLines, l => Assert.True(l.IsGood));
			Assert.False(setup.HasAdapterAdvice);
			Assert.Equal(StepStatus.Done, setup.Steps.Single(s => s.Id == SetupStepId.AdapterTest).Status);
			Assert.Empty(setup.Message);

			setup.ShowAdapterResult(FakeControllerService.Adapter(610, 21, 0, 93.9));
			Assert.True(setup.IsAdapterBorderline);
			Assert.Contains(setup.AdapterLines, l => l.IsWarn && l.Measured == "En kötü gecikme 21,0 ms" && l.Status == "Sınırda");
			Assert.True(setup.HasAdapterAdvice);
			Assert.Equal(StepStatus.Warning, setup.Steps.Single(s => s.Id == SetupStepId.AdapterTest).Status);

			// The controller went quiet half-way: no verdict, the test asks to be run again.
			setup.ShowAdapterResult(FakeControllerService.Adapter(640, 4.8, 0, 93.9, coverage: 0.3));
			Assert.Null(setup.Wizard.AdapterVerdict);
			Assert.Equal(Text.AdapterHeadlineIncomplete(), setup.AdapterHeadline);
			Assert.False(setup.IsAdapterGood || setup.IsAdapterBorderline || setup.IsAdapterBad);
			Assert.Equal(StepStatus.Needed, setup.Steps.Single(s => s.Id == SetupStepId.AdapterTest).Status);

			// The language follows the app, not the language of the test.
			setup.ShowAdapterResult(FakeControllerService.GoodAdapter());
			Localizer.Chosen = Language.English;
			Assert.Equal("640 reports a second", setup.AdapterLines[0].Measured);
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static IEnumerable<(AdapterTestResult? Result, string Name)> Outcomes() =>
	[
		(FakeControllerService.GoodAdapter(), "suitable"),
		(FakeControllerService.Adapter(610, 21, 0.2, 93.9), "borderline"),
		(FakeControllerService.Adapter(380, 38, 3.1, 80, gaps: 8), "unsuitable"),
		(FakeControllerService.Adapter(640, 4.8, 0, 93.9, coverage: 0.3), "incomplete"),
		(RealControllerService.Describe(null), "nodata"),
	];

	// A minute of history shaped like the state: low and flat when good; recurring delay with a few spikes over the limit
	// when borderline; mostly over the limit, some off the scale, when weak.
	private static void Feed(MainWindowViewModel vm, FakeControllerService fake, LinkQuality state)
	{
		for (int s = 0; s < LivePanelViewModel.HistoryLength; s++)
		{
			double wave = Math.Sin(s / 2.3);
			(double typical, double worst) = state switch
			{
				LinkQuality.Good => (1.0 + 0.2 * wave, 4.4 + 0.8 * wave),
				LinkQuality.Borderline => (2.5 + wave, s % 7 == 3 ? 34 : 17 + 4 * wave),
				_ => (8 + 2 * wave, s % 5 == 2 ? 75 : 38 + 9 * wave)
			};
			fake.AddSecond(typical, worst, lost: state == LinkQuality.Good || s % 9 != 4 ? 0 : 3);
		}
		vm.Overview.Live.Sample();
		fake.RaiseStateChanged();
	}

	private static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
	}

	private static string RepoRoot()
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "screens")))
		{
			dir = dir.Parent;
		}
		return dir?.FullName ?? AppContext.BaseDirectory;
	}
}
