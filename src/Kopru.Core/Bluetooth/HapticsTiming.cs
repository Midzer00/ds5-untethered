// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Core.Bluetooth;

// How on time the haptics reports of one stream (or of a session's streams, added up) went out, in Stopwatch ticks.
// Late: the deadline had passed by more than the threshold when the wait returned, any cause. Two causes are counted
// apart: the timer woke late (the wait began before the deadline and returned after it), and link.Write took long
// (work before the wait running past the deadline). Plain fields, no allocation: updated once per report.
public struct HapticsTiming
{
	// Late by any cause, and the worst lateness.
	public long Late, Worst;

	// Late although the wait began before the deadline, and the worst of those.
	public long WakeLate, WakeWorst;

	// Writes that took longer than the slow-write threshold, and the longest write.
	public long SlowWrites, WriteWorst;

	// One link.Write from started to ended.
	public void Wrote(long started, long ended, long slowTicks)
	{
		long took = ended - started;
		if (took > slowTicks)
		{
			SlowWrites++;
		}
		WriteWorst = Math.Max(WriteWorst, took);
	}

	// One wait for deadline, begun at waitStarted, returned at now. Returns how late (negative: early).
	public long Woke(long waitStarted, long deadline, long now, long lateTicks)
	{
		long lateBy = now - deadline;
		if (lateBy > lateTicks)
		{
			Late++;
		}
		Worst = Math.Max(Worst, lateBy);
		if (waitStarted < deadline)
		{
			if (lateBy > lateTicks)
			{
				WakeLate++;
			}
			WakeWorst = Math.Max(WakeWorst, lateBy);
		}
		return lateBy;
	}

	public void Add(in HapticsTiming other)
	{
		Late += other.Late;
		Worst = Math.Max(Worst, other.Worst);
		WakeLate += other.WakeLate;
		WakeWorst = Math.Max(WakeWorst, other.WakeWorst);
		SlowWrites += other.SlowWrites;
		WriteWorst = Math.Max(WriteWorst, other.WriteWorst);
	}

	// "3 late, worst 12.40 ms, wake 1 late worst 1.20 ms, write 2 slow worst 11.90 ms" (frequency: ticks per second).
	public readonly string Describe(long frequency) => string.Create(CultureInfo.InvariantCulture,
		$"{Late} late, worst {Ms(Worst, frequency):F2} ms, wake {WakeLate} late worst {Ms(WakeWorst, frequency):F2} ms, write {SlowWrites} slow worst {Ms(WriteWorst, frequency):F2} ms");

	private static double Ms(long ticks, long frequency) => ticks * 1000.0 / frequency;
}
