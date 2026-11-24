// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Kopru.App.Controls;

namespace Kopru.App.Tests;

// The game panel's drawn controls keep inside their plots whatever the settings are.
public sealed class GamePanelVisualsTests
{
	[Fact]
	public void TriggerTravel_EffectStaysInsideThePlot()
	{
		var plot = new Rect(10, 20, 300, 60);
		foreach (TriggerTravelEffect effect in Enum.GetValues<TriggerTravelEffect>())
		{
			foreach (double start in new[] { 0.0, 20, 70, 90 })
			{
				foreach (double force in new[] { 0.0, 50, 100 })
				{
					Rect r = TriggerTravel.EffectBounds(effect, start, Math.Max(start, 80), force, plot);
					Assert.True(r.Left >= plot.Left - 0.01 && r.Right <= plot.Right + 0.01 && r.Top >= plot.Top - 0.01 && r.Bottom <= plot.Bottom + 0.01,
						$"{effect} start {start} force {force}: {r} outside {plot}");
				}
			}
		}
	}

	[Fact]
	public void GatePlot_ThresholdMovesDownWithSensitivity_AndNeverHugsTheEdges()
	{
		double last = double.PositiveInfinity;
		foreach (double sensitivity in new[] { 0.0, 25, 50, 75, 100 })
		{
			double f = GatePlot.ThresholdFraction(sensitivity);
			Assert.InRange(f, 0.05, 0.95);
			Assert.True(f <= last);
			last = f;
		}
	}
}
