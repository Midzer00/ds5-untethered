// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Kopru.Core.Diagnostics;

// Measured input latency: LinkP50/P99 is the extra delay of input reports over the best seen, judged against the
// controller's own sensor clock (radio, Bluetooth stack and Windows); RelayMs is our part, from the report being read to
// the virtual pad having it (median).
public sealed record LatencyReading(double LinkP50Ms, double LinkP99Ms, double RelayMs);

// What arrived since the previous LatencyMeter.TakeCounts: reports, reports missing from the sequence, and gaps between
// two reports of at least LinkLimits.LongGapMs / VeryLongGapMs.
public readonly record struct LinkCounts(int Reports, int Lost, int LongGaps, int VeryLongGaps);

// Rolling window of the last input reports (thread safe: recorded on the reader thread, read by the monitor).
public sealed class LatencyMeter
{
	public const int DefaultCapacity = 1000;

	private readonly object gate = new();

	private readonly LinkSample[] samples;

	private readonly long[] relays;

	private int next, count;

	private int countedReports, countedLost, countedLongGaps, countedVeryLongGaps;

	public LatencyMeter(int capacity = DefaultCapacity)
	{
		samples = new LinkSample[capacity];
		relays = new long[capacity];
	}

	// arrivalTicks: when the report was read; relayDoneTicks: when the virtual pad had it (Stopwatch ticks).
	public void Record(long arrivalTicks, byte sequence, uint sensorTicks, long relayDoneTicks)
	{
		lock (gate)
		{
			if (count > 0)
			{
				LinkSample previous = samples[(next - 1 + samples.Length) % samples.Length];
				countedLost += Math.Max(0, ((sequence - previous.Sequence) & 0xFF) - 1);
				double gapMs = (arrivalTicks - previous.ArrivalTicks) * 1000.0 / Stopwatch.Frequency;
				if (gapMs >= LinkLimits.LongGapMs)
				{
					countedLongGaps++;
				}
				if (gapMs >= LinkLimits.VeryLongGapMs)
				{
					countedVeryLongGaps++;
				}
			}
			countedReports++;
			samples[next] = new LinkSample(arrivalTicks, sequence, sensorTicks);
			relays[next] = Math.Max(0, relayDoneTicks - arrivalTicks);
			next = (next + 1) % samples.Length;
			count = Math.Min(count + 1, samples.Length);
		}
	}

	// The counts since the previous call (LinkQualityMonitor's per-second sample), and starts counting again.
	public LinkCounts TakeCounts()
	{
		lock (gate)
		{
			LinkCounts counts = new(countedReports, countedLost, countedLongGaps, countedVeryLongGaps);
			countedReports = countedLost = countedLongGaps = countedVeryLongGaps = 0;
			return counts;
		}
	}

	// Null until enough reports arrived.
	public LatencyReading? Snapshot()
	{
		LinkSample[] window;
		double[] relay;
		lock (gate)
		{
			window = new LinkSample[count];
			relay = new double[count];
			int start = (next - count + samples.Length) % samples.Length;
			for (int i = 0; i < count; i++)
			{
				int k = (start + i) % samples.Length;
				window[i] = samples[k];
				relay[i] = relays[k] * 1000.0 / Stopwatch.Frequency;
			}
		}
		if (window.Length < 10)
		{
			return null;
		}
		LinkResult? link = LinkAnalyzer.Analyze(window);
		if (link == null)
		{
			return null;
		}
		Array.Sort(relay);
		return new LatencyReading(link.ExtraDelayMs.P50, link.ExtraDelayMs.P99, LinkAnalyzer.Percentile(relay, 50));
	}
}
