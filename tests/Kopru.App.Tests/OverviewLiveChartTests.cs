// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.ViewModels;
using Kopru.App.Views;

namespace Kopru.App.Tests;

// The Overview's live delay graph: a bounded height that does not grow with the window, and a vertical axis that
// never zooms in tighter than 0-5 ms.
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
		// Just under the minimum after headroom: still the minimum.
		Assert.Equal(5, Sparkline.ScaleTop(5, [4.3]));
	}

	[AvaloniaFact]
	public void Graph_KeepsItsHeight_AtTheDefaultAndALargeWindow()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		vm.SelectedNav = vm.NavItems[0];
		vm.Overview.ToggleBridgeCommand.Execute(null);
		fake.Tick();
		for (int s = 0; s < LivePanelViewModel.HistoryLength; s++)
		{
			fake.Latency = new(1.0, 2.0, 0.05);
			vm.Overview.Live.Sample();
		}
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();

		// The graph takes the little height the live card gets to end on the quick settings' line: never under its
		// minimum (72) and never over its cap (200; a tall window's spare height also reaches it), whatever the window size.
		Sparkline graph = window.GetVisualDescendants().OfType<Sparkline>().Single(g => g.IsEffectivelyVisible);
		Assert.InRange(graph.Bounds.Height, 71, 201);
		// A fixed scale now (0 to twice the "you can feel it" limit), so the same height always means the same delay.
		Assert.Equal(2 * Kopru.Core.Diagnostics.LinkLimits.PoorDelayMs, graph.Maximum);
		// The level bars: full-width meters of a fixed height.
		ProgressBar bar = window.GetVisualDescendants().OfType<ProgressBar>().First(p => p.Classes.Contains("level") && p.IsEffectivelyVisible);
		double barHeight = bar.Bounds.Height;
		Assert.True(bar.Bounds.Width >= 100, $"level bar {bar.Bounds.Width}");

		window.Width = 1680;
		window.Height = 1050;
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
		Assert.InRange(graph.Bounds.Height, 71, 201);
		Assert.Equal(barHeight, bar.Bounds.Height);
		// A larger window shows the same layout scaled up (UiScale), not stretched cards: the live card keeps its layout height
		// and is drawn 1.5 times as large.
		Assert.Equal(1.5, window.ContentScale);
		Border card = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("panel") && b.GetVisualDescendants().Contains(graph));
		Avalonia.Point? top = card.TranslatePoint(new Avalonia.Point(0, 0), window);
		Avalonia.Point? bottom = card.TranslatePoint(new Avalonia.Point(0, card.Bounds.Height), window);
		Assert.InRange(bottom!.Value.Y - top!.Value.Y, card.Bounds.Height * 1.5 - 1, card.Bounds.Height * 1.5 + 1);
		window.Close();
	}
}
