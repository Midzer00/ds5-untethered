// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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

	// A high-resolution waitable timer (Windows 10 1803+), else the fallback.
	public static IPreciseWait Create()
	{
		if (OperatingSystem.IsWindows())
		{
			HighResolutionWait? high = HighResolutionWait.TryCreate();
			if (high != null)
			{
				return high;
			}
		}
		return new FallbackWait();
	}

	// The high-resolution timer with its arming call replaced (tests: setTimer gets the relative due time in 100 ns units
	// and returns whether the timer was armed); null when the timer cannot be created.
	public static IPreciseWait? CreateHighResolution(Func<long, bool>? setTimer = null) =>
		OperatingSystem.IsWindows() ? HighResolutionWait.TryCreate(setTimer) : null;

	// The method used before: 1 ms timer resolution while active, sleep, then spin.
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

internal sealed class HighResolutionWait : IPreciseWait
{
	private const uint CreateWaitableTimerHighResolution = 0x00000002;
	private const uint TimerAllAccess = 0x1F0003;

	private readonly SafeWaitHandle handle;

	private readonly TimerHandle timer;

	private readonly WaitHandle[] pair = new WaitHandle[2];

	private readonly Func<long, bool>? setTimer;

	// Set once arming the timer failed: every wait from then on uses the fallback.
	private FallbackWait? fallback;

	private HighResolutionWait(SafeWaitHandle handle, Func<long, bool>? setTimer)
	{
		this.handle = handle;
		this.setTimer = setTimer;
		timer = new TimerHandle(handle);
		pair[0] = timer;
	}

	public bool HighResolution => fallback == null;

	public static HighResolutionWait? TryCreate(Func<long, bool>? setTimer = null)
	{
		try
		{
			SafeWaitHandle h = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
			if (h.IsInvalid)
			{
				h.Dispose();
				return null;
			}
			return new HighResolutionWait(h, setTimer);
		}
		catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
		{
			return null;
		}
	}

	public bool WaitUntil(long timestamp, bool spinFinal, WaitHandle? wake = null)
	{
		if (fallback != null)
		{
			return fallback.WaitUntil(timestamp, spinFinal, wake);
		}
		long margin = spinFinal ? PreciseWait.SpinMarginTicks : 0;
		long remaining = timestamp - margin - Stopwatch.GetTimestamp();
		// Relative due time in 100 ns units (negative = relative).
		long due = -(long)((double)remaining * 10_000_000 / Stopwatch.Frequency);
		if (due < 0 && !Arm(due))
		{
			// Never fall through as if the deadline had passed (that would send a report every ~1 ms on a pegged core):
			// switch to the fallback for good and wait there.
			fallback = new FallbackWait();
			Diagnostics.AppLog.Write($"precise wait: arming the high-resolution timer failed (error {Marshal.GetLastPInvokeError()}), using the fallback");
			return fallback.WaitUntil(timestamp, spinFinal, wake);
		}
		if (due < 0)
		{
			if (wake != null)
			{
				pair[1] = wake;
				int index = WaitHandle.WaitAny(pair);
				pair[1] = null!;
				if (index == 1)
				{
					return false;
				}
			}
			else
			{
				timer.WaitOne();
			}
		}
		else if (wake != null && wake.WaitOne(0))
		{
			return false;
		}
		if (spinFinal)
		{
			PreciseWait.Spin(timestamp, margin + Stopwatch.Frequency / 4000);
		}
		return true;
	}

	private bool Arm(long due) => setTimer != null ? setTimer(due)
		: SetWaitableTimerEx(handle, in due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0);

	public void Idle() => fallback?.Idle();

	public void Dispose()
	{
		fallback?.Dispose();
		timer.Dispose();
		handle.Dispose();
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, in long dueTime, int period, IntPtr completion,
		IntPtr argument, IntPtr wakeContext, uint tolerableDelay);

	private sealed class TimerHandle : WaitHandle
	{
		public TimerHandle(SafeWaitHandle handle)
		{
			SafeWaitHandle = handle;
		}

		// The SafeWaitHandle is owned (and closed) by HighResolutionWait.
		protected override void Dispose(bool explicitDisposing)
		{
		}
	}
}

// The method used before the high-resolution timer: holds the 1 ms timer resolution while active, sleeps, then spins.
internal sealed class FallbackWait : IPreciseWait
{
	private bool holding;

	public bool HighResolution => false;

	public bool WaitUntil(long timestamp, bool spinFinal, WaitHandle? wake = null)
	{
		if (!holding)
		{
			TimerResolution.Begin();
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
			TimerResolution.End();
			holding = false;
		}
	}

	public void Dispose() => Idle();
}

// Windows sleeps in 15.6 ms steps unless a program asks for 1 ms; asked only by the fallback wait while active.
internal static class TimerResolution
{
	[DllImport("winmm.dll")]
	private static extern uint timeBeginPeriod(uint period);

	[DllImport("winmm.dll")]
	private static extern uint timeEndPeriod(uint period);

	public static void Begin()
	{
		if (OperatingSystem.IsWindows())
		{
			timeBeginPeriod(1);
		}
	}

	public static void End()
	{
		if (OperatingSystem.IsWindows())
		{
			timeEndPeriod(1);
		}
	}
}
