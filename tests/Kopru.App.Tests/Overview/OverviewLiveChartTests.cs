// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Diagnostics;

namespace Kopru.App.Tests;

// The Overview's live card: the delay chart (a bounded height, a scale that never zooms in tighter than its minimum, a
// minute of the bridge's history with ticks under the troubled seconds), the quality gauge, and the Signals view (rings,
// traces drawn again only when something changed, a timer that runs only while the view is in sight).
public class OverviewLiveChartTests
{
	[Fact]
	public void Scale_NeverBelowTheMinimum_SoSmallJitterStaysFlat()
	{
		// ~1 ms with sub-millisecond jitter: the axis stays at 0-5 ms.
		Assert.Equal(5, Sparkline.ScaleTop(5, [0.9, 1.1, 1.3], [1.8, 2.2]));
		// Nothing yet.
		Assert.Equal(5, Sparkline.ScaleTop(5, [], []));
		// A real spike above the minimum: the highest value with 15 % headroom.
		Assert.Equal(12 * 1.15, Sparkline.ScaleTop(5, [1.0, 1.2], [3.0, 12.0]), 6);
		Assert.Equal(6 * 1.15, Sparkline.ScaleTop(5, [6.0]), 6);
		// Just under the minimum after headroom: still the minimum; a second without a reading is no value.
		Assert.Equal(5, Sparkline.ScaleTop(5, [4.3, double.NaN]));
	}

	[AvaloniaFact]
	public void Graph_KeepsItsHeight_AtTheDefaultAndALargeWindow()
	{
		(MainWindowViewModel vm, FakeControllerService fake, MainWindow window) = Show();
		for (int s = 0; s < LivePanelViewModel.HistoryLength; s++)
		{
			fake.AddSecond(1.0, 2.0);
		}
		vm.Overview.Live.Sample();
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();

		// The graph takes the little height the live card gets to end on the quick settings' line: never under its
		// minimum (40) and never over its cap (260; a tall window's spare height also reaches it), whatever the window size.
		Sparkline graph = window.GetVisualDescendants().OfType<Sparkline>().Single(g => g.IsEffectivelyVisible);
		Assert.InRange(graph.Bounds.Height, 39, 261);
		// Scaled to the data: a 2 ms worst on a 0-10 ms axis; a minute across the full width.
		Assert.Equal(10, graph.Maximum);
		Assert.Equal(LivePanelViewModel.HistoryLength, graph.Capacity);
		Assert.Equal(LivePanelViewModel.HistoryLength, graph.Values!.Count);

		window.Width = 1680;
		window.Height = 1050;
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
		Assert.InRange(graph.Bounds.Height, 39, 261);
		// A larger window shows the same layout scaled up (UiScale), not stretched cards: the live card keeps its layout height
		// and is drawn 1.5 times as large.
		Assert.Equal(1.5, window.ContentScale);
		Border card = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("panel") && b.GetVisualDescendants().Contains(graph));
		Avalonia.Point? top = card.TranslatePoint(new Avalonia.Point(0, 0), window);
		Avalonia.Point? bottom = card.TranslatePoint(new Avalonia.Point(0, card.Bounds.Height), window);
		Assert.InRange(bottom!.Value.Y - top!.Value.Y, card.Bounds.Height * 1.5 - 1, card.Bounds.Height * 1.5 + 1);
		window.Close();
	}

	// The chart is fed from the bridge's own history (kept while the page is hidden): a minute with its lost seconds and
	// pauses as ticks, and nothing once the bridge stops.
	[AvaloniaFact]
	public void Chart_ShowsTheBridgesMinute_WithTicksForTroubledSeconds()
	{
		(MainWindowViewModel vm, FakeControllerService fake, MainWindow window) = Show();
		vm.Overview.SetVisible(false);
		for (int s = 0; s < LivePanelViewModel.HistoryLength + 10; s++)
		{
			fake.AddSecond(1.0, 3.0 + s % 3, lost: s == 50 ? 4 : 0, longGaps: s == 60 ? 1 : 0);
		}
		// Shown again: the whole minute is there at once, without waiting for the page's own samples.
		vm.Overview.SetVisible(true);
		LivePanelViewModel live = vm.Overview.Live;
		Assert.Equal(LivePanelViewModel.HistoryLength, live.WorstHistory.Count);
		Assert.Equal(LivePanelViewModel.HistoryLength, live.TroubleMarks.Count);
		// Seconds 50 and 60 of 70, in a window of the last 60 (10 to 69).
		Assert.Equal([40, 50], live.TroubleMarks.Select((t, i) => (t, i)).Where(x => x.t).Select(x => x.i));
		Settle();
		Sparkline graph = window.GetVisualDescendants().OfType<Sparkline>().Single(g => g.IsEffectivelyVisible);
		Assert.Same(live.TroubleMarks, graph.Marks);
		// The newest second sits at the right edge of the plot.
		Assert.Equal(graph.PlotWidth, graph.XOf(59, 60), 3);

		fake.StopBridgeAsync();
		Assert.Empty(live.WorstHistory);
		Assert.Empty(live.TroubleMarks);
		window.Close();
	}

	// The gauge: three pieces in the verdict's colour for Good, two for Fair, one for Weak, none while measuring.
	[AvaloniaFact]
	public void Gauge_LightsAPieceForEachStepOfQuality()
	{
		(MainWindowViewModel vm, FakeControllerService fake, MainWindow window) = Show();
		QualityGauge gauge = window.GetVisualDescendants().OfType<QualityGauge>().Single(g => g.Name == "LinkGauge");
		foreach ((LinkQuality state, int level, string brush) in new[] { (LinkQuality.Good, 3, "IndicatorOnBrush"), (LinkQuality.Borderline, 2, "IndicatorWaitBrush"),
			(LinkQuality.Poor, 1, "DangerBrush"), (LinkQuality.Measuring, 0, "MutedBrush") })
		{
			fake.LinkQuality = new LinkQualityReading(state, 4, 0, 0, 700);
			fake.RaiseStateChanged();
			Settle();
			Assert.Equal(level, gauge.Level);
			Assert.Equal(((Avalonia.Media.ISolidColorBrush)window.FindResource(brush)!).Color, ((Avalonia.Media.ISolidColorBrush)gauge.Fill!).Color);
		}
		window.Close();
	}

	[Fact]
	public void SignalRing_KeepsTheLatestSamples_OldestFirst_Clamped()
	{
		var ring = new SignalRing(4);
		Assert.Equal(0, ring.Count);
		Assert.Equal(0, ring.Latest);
		foreach (double v in new[] { 0.1, 0.2, 0.3, 0.4, 0.5, 1.7, -2, double.NaN })
		{
			ring.Push(v);
		}
		Assert.Equal(4, ring.Count);
		Assert.Equal([0.5f, 1f, 0f, 0f], Enumerable.Range(0, 4).Select(i => ring[i]));
		ring.Clear();
		Assert.Equal(0, ring.Count);
		ring.Push(0.25);
		Assert.Equal(0.25f, ring[0]);
	}

	// A trace builds one geometry per series, and builds again only for a new revision or size: a redraw of the same
	// samples reuses them. Areas grow up from the baseline; mirrored lanes grow away from the centre line.
	[AvaloniaFact]
	public void SignalTrace_BuildsItsGeometryOnlyForANewRevision()
	{
		var primary = new SignalRing(LivePanelViewModel.SignalSamples);
		var secondary = new SignalRing(LivePanelViewModel.SignalSamples);
		for (int i = 0; i < 50; i++)
		{
			primary.Push(i / 50.0);
			secondary.Push(0.5);
		}
		var trace = new SignalTrace { Primary = primary, Secondary = secondary, Mode = SignalTraceMode.Areas, Width = 240, Height = 48 };
		var window = new Window { Content = trace, Width = 300, Height = 100 };
		window.Show();
		Settle();
		Assert.Equal(1, trace.Builds);
		Assert.NotNull(trace.PrimaryGeometry);
		Assert.NotNull(trace.SecondaryGeometry);
		// The newest sample reaches the right edge: 49 / 50 of the height above the baseline.
		Rect bounds = trace.PrimaryGeometry!.Bounds;
		Assert.Equal(240, bounds.Right, 1);
		Assert.Equal(trace.Extent(49 / 50.0, false).Edge, bounds.Top, 1);
		Assert.Equal(trace.Extent(0, false).Base, bounds.Bottom, 1);

		trace.InvalidateVisual();
		Settle();
		Assert.Equal(1, trace.Builds);
		primary.Push(1);
		trace.Revision++;
		Settle();
		Assert.Equal(2, trace.Builds);

		trace.Mode = SignalTraceMode.Mirrored;
		Settle();
		(double mid, double up) = trace.Extent(1, false);
		(_, double down) = trace.Extent(1, true);
		Assert.Equal(24, mid);
		Assert.True(up < mid && down > mid && Math.Abs(mid - up - (down - mid)) < 0.01, $"{up} {mid} {down}");
		Assert.InRange(trace.SecondaryGeometry!.Bounds.Bottom, mid + 23 * 0.5 - 0.5, mid + 23 * 0.5 + 0.5);
		window.Close();
	}

	// The Signals view samples only while it is selected on a shown page with the bridge live, and the traces are asked to
	// redraw only while what they show changes.
	[AvaloniaFact]
	public void Signals_SampleOnlyWhileInSight_AndRedrawOnlyOnChange()
	{
		(MainWindowViewModel vm, FakeControllerService fake, MainWindow window) = Show();
		LivePanelViewModel live = vm.Overview.Live;
		Assert.True(vm.Overview.IsFollowing);
		Assert.False(live.IsSignalsRunning);
		live.SelectedView = LivePanelViewModel.SignalsView;
		Assert.True(live.IsSignalsRunning);
		Settle();
		Assert.True(window.GetVisualDescendants().OfType<SignalTrace>().Single(t => t.Name == "TriggerTrace").IsEffectivelyVisible);
		Assert.False(window.GetVisualDescendants().OfType<Sparkline>().Single().IsEffectivelyVisible);

		vm.Overview.SetVisible(false);
		Assert.False(live.IsSignalsRunning);
		vm.Overview.SetVisible(true);
		Assert.True(live.IsSignalsRunning);
		live.SelectedView = LivePanelViewModel.LinkView;
		Assert.False(live.IsSignalsRunning);
		live.SelectedView = LivePanelViewModel.SignalsView;
		fake.StopBridgeAsync();
		Assert.False(live.IsSignalsRunning);
		fake.StartBridgeAsync();
		Assert.True(live.IsSignalsRunning);

		// Values that move: every sample is a new revision, with the values as small numbers.
		fake.RawInput = [128, 128, 128, 128, 0, 0, 0, 0, 0];
		long before = live.SignalRevision;
		for (int i = 0; i < 10; i++)
		{
			fake.RawInput = [128, 128, 128, 128, (byte)(i * 20), 0, 0, 0, 0];
			live.Follow();
		}
		Assert.Equal(before + 10, live.SignalRevision);
		Assert.Equal("71", live.L2Text);
		// Held still: once the whole window shows the same values nothing new is drawn.
		for (int i = 0; i < LivePanelViewModel.SignalSamples + 20; i++)
		{
			live.Follow();
		}
		long still = live.SignalRevision;
		live.Follow();
		live.Follow();
		Assert.Equal(still, live.SignalRevision);
		fake.SetHapticsLevels(0.5, 0.2);
		live.Follow();
		Assert.Equal(still + 1, live.SignalRevision);
		Assert.Equal(("50", "20"), (live.HapticsLeftText, live.HapticsRightText));
		window.Close();
	}

	// The microphone lane shows only while the controller's microphone is live.
	[AvaloniaFact]
	public void MicLane_OnlyWhileTheMicrophoneIsLive()
	{
		(MainWindowViewModel vm, FakeControllerService fake, MainWindow window) = Show();
		LivePanelViewModel live = vm.Overview.Live;
		live.SelectedView = LivePanelViewModel.SignalsView;
		Assert.False(live.HasMic);
		vm.Settings.MicEnabled = true;
		Assert.True(live.HasMic);
		Settle();
		Assert.True(window.GetVisualDescendants().OfType<SignalTrace>().Single(t => t.Name == "MicTrace").IsEffectivelyVisible);
		fake.MicLevel = 0.4;
		live.Follow();
		Assert.Equal("40", live.MicText);
		vm.Settings.LowPowerOn = true;
		Assert.False(live.HasMic);
		window.Close();
	}

	// The lane follows the microphone switch both ways (no bridge state change needed) and the button's Mute mode; a lane
	// shown again starts from the current level.
	[AvaloniaFact]
	public void MicLane_FollowsTheSwitch_OnOffAndMute()
	{
		(MainWindowViewModel vm, FakeControllerService fake, MainWindow window) = Show();
		LivePanelViewModel live = vm.Overview.Live;
		live.SelectedView = LivePanelViewModel.SignalsView;
		fake.MicLevel = 0.3;
		vm.Settings.MicEnabled = true;
		Assert.True(live.HasMic);
		live.Follow();
		Assert.Equal("30", live.MicText);
		vm.Settings.MicEnabled = false;
		Assert.False(live.HasMic);
		live.Follow();
		Assert.Equal("0", live.MicText);
		vm.Settings.MicEnabled = true;
		Assert.True(live.HasMic);
		vm.Settings.MicMuted = true;
		Assert.False(live.HasMic);
		vm.Settings.MicMuted = false;
		Assert.True(live.HasMic);
		live.Follow();
		Assert.Equal("30", live.MicText);
		window.Close();
	}

	private static (MainWindowViewModel, FakeControllerService, MainWindow) Show()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		vm.SelectedNav = vm.NavItems[0];
		vm.Overview.ToggleBridgeCommand.Execute(null);
		fake.Tick();
		fake.LinkQuality = new LinkQualityReading(LinkQuality.Good, 4.6, 0, 0, 718);
		fake.RaiseStateChanged();
		Settle();
		return (vm, fake, window);
	}

	private static void Settle() => LiveCardRenderTests.Settle();
}
