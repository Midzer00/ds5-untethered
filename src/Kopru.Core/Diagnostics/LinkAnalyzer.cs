// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Kopru.Core.Diagnostics;

// One input report: when it arrived (Stopwatch ticks), its 8-bit sequence and the controller's own clock (0.33 us ticks).
public readonly record struct LinkSample(long ArrivalTicks, byte Sequence, uint SensorTicks);

public sealed record Spread(double P50, double P95, double P99, double Max);

// LongGaps / VeryLongGaps: intervals of at least LinkLimits.LongGapMs / VeryLongGapMs between two reports.
public sealed record LinkResult(int Reports, double Seconds, double RateHz, Spread IntervalMs, Spread ExtraDelayMs, int Lost, int LongGaps = 0,
	int VeryLongGaps = 0)
{
	public double LostShare => Reports + Lost == 0 ? 0 : (double)Lost / (Reports + Lost);
}

// How well input reports come through: comparing arrival times with the controller's own clock separates what the
// controller does (its rate) from what the radio, Windows and this program add (extra delay above the best seen).
public static class LinkAnalyzer
{
	private const double SensorTicksPerMs = 3000;

	public static LinkResult? Analyze(IReadOnlyList<LinkSample> samples, double windowMs = 500)
	{
		int n = samples.Count;
		if (n < 3)
		{
			return null;
		}
		double[] arrival = samples.Select(s => (s.ArrivalTicks - samples[0].ArrivalTicks) * 1000.0 / Stopwatch.Frequency).ToArray();
		double[] device = new double[n];
		long offset = 0;
		for (int i = 0; i < n; i++)
		{
			if (i > 0 && samples[i].SensorTicks < samples[i - 1].SensorTicks && samples[i - 1].SensorTicks - samples[i].SensorTicks > 0x80000000u)
			{
				offset += 1L << 32;
			}
			device[i] = (samples[i].SensorTicks + offset - samples[0].SensorTicks) / SensorTicksPerMs;
		}
		int lost = 0;
		for (int i = 1; i < n; i++)
		{
			int step = (samples[i].Sequence - samples[i - 1].Sequence) & 0xFF;
			lost += Math.Max(0, step - 1);
		}
		double[] intervals = new double[n - 1];
		for (int i = 1; i < n; i++)
		{
			intervals[i - 1] = arrival[i] - arrival[i - 1];
		}

		// Clock drift removed by a line through the best (smallest) offset of each window.
		double[] lag = new double[n];
		for (int i = 0; i < n; i++)
		{
			lag[i] = arrival[i] - device[i];
		}
		List<(double X, double Y)> best = [];
		for (int start = 0; start < n;)
		{
			int min = start;
			int end = start;
			while (end < n && arrival[end] < arrival[start] + windowMs)
			{
				if (lag[end] < lag[min])
				{
					min = end;
				}
				end++;
			}
			best.Add((arrival[min], lag[min]));
			start = end;
		}
		(double a, double b) = FitLine(best);
		double[] extra = new double[n];
		for (int i = 0; i < n; i++)
		{
			extra[i] = lag[i] - (a + b * arrival[i]);
		}
		double floor = extra.Min();
		for (int i = 0; i < n; i++)
		{
			extra[i] -= floor;
		}
		double seconds = arrival[^1] / 1000;
		int longGaps = intervals.Count(i => i >= LinkLimits.LongGapMs);
		int veryLongGaps = intervals.Count(i => i >= LinkLimits.VeryLongGapMs);
		return new LinkResult(n, seconds, seconds > 0 ? (n - 1) / seconds : 0, SpreadOf(intervals), SpreadOf(extra), lost, longGaps, veryLongGaps);
	}

	public static double Percentile(double[] sorted, double p)
	{
		double k = (sorted.Length - 1) * p / 100;
		int lo = (int)Math.Floor(k);
		int hi = Math.Min(lo + 1, sorted.Length - 1);
		return sorted[lo] + (sorted[hi] - sorted[lo]) * (k - lo);
	}

	private static Spread SpreadOf(double[] values)
	{
		double[] sorted = [.. values.Order()];
		return new Spread(Percentile(sorted, 50), Percentile(sorted, 95), Percentile(sorted, 99), sorted[^1]);
	}

	private static (double A, double B) FitLine(List<(double X, double Y)> points)
	{
		if (points.Count < 2)
		{
			return (points.Count == 1 ? points[0].Y : 0, 0);
		}
		double mx = points.Average(p => p.X);
		double my = points.Average(p => p.Y);
		double sxx = points.Sum(p => (p.X - mx) * (p.X - mx));
		if (sxx == 0)
		{
			return (my, 0);
		}
		double slope = points.Sum(p => (p.X - mx) * (p.Y - my)) / sxx;
		return (my - slope * mx, slope);
	}
}
