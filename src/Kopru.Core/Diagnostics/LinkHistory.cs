// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Diagnostics;

// One second of the running link as the Overview draws it: the typical and worst-1 % delay of the latest reports (NaN when
// there was no reading that second), the input rate, and what arrived in that second (LatencyMeter.TakeCounts).
public readonly record struct LinkSecondPoint(double TypicalMs, double WorstMs, double RateHz, int Reports, int Lost, int LongGaps)
{
	public bool HasDelay => !double.IsNaN(WorstMs);

	// A second with lost reports or a long pause (a small tick under the Overview's chart).
	public bool HasTrouble => Lost > 0 || LongGaps > 0;
}

// The last Seconds seconds of the link, kept while the bridge runs whatever page the UI shows: BridgeHost adds one sample a
// second from its monitor pass and resets it on every (re)connect. Cheap: a fixed ring, and the snapshot (Recent, oldest
// first) is built once a second, so reading it costs nothing.
public sealed class LinkHistory
{
	public const int Seconds = 60;

	private readonly object gate = new();
	private readonly LinkSecondPoint[] ring = new LinkSecondPoint[Seconds];
	private int next, count;
	private LinkSecondPoint[] recent = [];

	// The latest snapshot, oldest first (empty after a reset, until the first sample).
	public IReadOnlyList<LinkSecondPoint> Recent => Volatile.Read(ref recent);

	// A new connection: the history starts over.
	public void Reset()
	{
		lock (gate)
		{
			next = count = 0;
			Volatile.Write(ref recent, []);
		}
	}

	// One second: what arrived (counts), the latest delay reading (null: none yet) and the input rate.
	public void Add(LinkCounts counts, LatencyReading? latency, double rateHz)
	{
		lock (gate)
		{
			ring[next] = new LinkSecondPoint(latency?.LinkP50Ms ?? double.NaN, latency?.LinkP99Ms ?? double.NaN, Math.Max(0, rateHz),
				counts.Reports, counts.Lost, counts.LongGaps);
			next = (next + 1) % ring.Length;
			count = Math.Min(count + 1, ring.Length);
			LinkSecondPoint[] snapshot = new LinkSecondPoint[count];
			int start = (next - count + ring.Length) % ring.Length;
			for (int i = 0; i < count; i++)
			{
				snapshot[i] = ring[(start + i) % ring.Length];
			}
			Volatile.Write(ref recent, snapshot);
		}
	}
}
