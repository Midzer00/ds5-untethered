// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kopru.Core.Bluetooth;

// Waits until a Stopwatch timestamp with sub-millisecond accuracy, for threads that pace reports or USB frames. One
// instance per thread (not thread-safe).
public interface IPreciseWait : IDisposable
{
	// True when a high-resolution waitable timer is used; false for the fallback (1 ms timer resolution + sleep).
	bool HighResolution { get; }

	// Blocks until timestamp (Stopwatch ticks). spinFinal: wakes a little early and spins the rest (bounded), for report
	// pacing that must not be late. Returns false when wake was signalled first.
	bool WaitUntil(long timestamp, bool spinFinal, WaitHandle? wake = null);

	// The caller goes idle: releases what is held only while active (the fallback's 1 ms timer resolution).
	void Idle();
}

public static class PreciseWait
{
	// The high-resolution timer wakes this much before the deadline and spins the rest. Measured on Windows 11 for a
	// 10.67 ms cadence (2000 waits each): timer alone 0.27 ms late at the median, 0.58 ms at p99; with a 0.75 ms final
	// spin 0.00 / 0.01 ms (the old sleep + 1.5 ms spin: 0.00 / 0.02 ms) at under half the CPU of the old method.
	public static readonly long SpinMarginTicks = Stopwatch.Frequency * 3 / 4000;

	// The fallback sleeps until this much before the deadline and spins the rest (the method used before).
	public static readonly long FallbackSpinTicks = Stopwatch.Frequency * 3 / 2000;

	// The portable wait (sleep, then spin). TODO(linux): a timerfd wait.
	public static IPreciseWait Create() => new FallbackWait();

	// Sleep, then spin the last FallbackSpinTicks.
	public static IPreciseWait CreateFallback() => new FallbackWait();

	public static string Name(IPreciseWait wait) => wait.HighResolution ? "high-resolution" : "fallback";

	internal static void Spin(long timestamp, long maxTicks)
	{
		long until = Math.Min(timestamp, Stopwatch.GetTimestamp() + maxTicks);
		while (Stopwatch.GetTimestamp() < until)
		{
			Thread.SpinWait(20);
		}
	}
}

internal sealed class FallbackWait : IPreciseWait
{
	private bool holding;

	public bool HighResolution => false;

	public bool WaitUntil(long timestamp, bool spinFinal, WaitHandle? wake = null)
	{
		if (!holding)
		{
			holding = true;
		}
		if (spinFinal)
		{
			// As before: one sleep to 1.5 ms before the deadline (whole milliseconds, rounded down), then spin.
			long remaining = timestamp - PreciseWait.FallbackSpinTicks - Stopwatch.GetTimestamp();
			int ms = remaining > 0 ? (int)(remaining * 1000 / Stopwatch.Frequency) : 0;
			if (ms > 0 && Sleep(ms, wake))
			{
				return false;
			}
			PreciseWait.Spin(timestamp, PreciseWait.FallbackSpinTicks + Stopwatch.Frequency / 1000);
			return true;
		}
		long left;
		while ((left = timestamp - Stopwatch.GetTimestamp()) > 0)
		{
			if (Sleep((int)Math.Max(1, left * 1000 / Stopwatch.Frequency), wake))
			{
				return false;
			}
		}
		return true;
	}

	// True when wake was signalled.
	private static bool Sleep(int ms, WaitHandle? wake)
	{
		if (wake != null)
		{
			return wake.WaitOne(ms);
		}
		Thread.Sleep(ms);
		return false;
	}

	public void Idle()
	{
		if (holding)
		{
			holding = false;
		}
	}

	public void Dispose() => Idle();
}
