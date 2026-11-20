// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Kopru.Core.Bluetooth;
using Kopru.Core.Diagnostics;

namespace Kopru.Core.Tests;

public class PowerThrottlingTests
{
	private sealed class FakeThrottling : IProcessPowerThrottling
	{
		public List<(uint Control, uint State)> Calls { get; } = [];

		// Errors returned call by call (0 = set); past the end, 0.
		public int[] Errors { get; init; } = [];

		public Exception? Throw { get; init; }

		public int Set(uint controlMask, uint stateMask)
		{
			Calls.Add((controlMask, stateMask));
			if (Throw != null)
			{
				throw Throw;
			}
			return Calls.Count <= Errors.Length ? Errors[Calls.Count - 1] : 0;
		}
	}

	[Fact]
	public void State_IsThreeUlongs_WithTheWindowsValues()
	{
		Assert.Equal(12, Marshal.SizeOf<ProcessPowerThrottlingState>());
		Assert.Equal(0, (int)Marshal.OffsetOf<ProcessPowerThrottlingState>(nameof(ProcessPowerThrottlingState.Version)));
		Assert.Equal(4, (int)Marshal.OffsetOf<ProcessPowerThrottlingState>(nameof(ProcessPowerThrottlingState.ControlMask)));
		Assert.Equal(8, (int)Marshal.OffsetOf<ProcessPowerThrottlingState>(nameof(ProcessPowerThrottlingState.StateMask)));
		Assert.Equal(4, PowerThrottling.ProcessPowerThrottlingClass);
		Assert.Equal(1u, PowerThrottling.CurrentVersion);
		Assert.Equal(0x1u, PowerThrottling.ExecutionSpeed);
		Assert.Equal(0x4u, PowerThrottling.IgnoreTimerResolution);
	}

	[Fact]
	public void OptOut_SetsBothBitsOnce_WithTheStateClear()
	{
		FakeThrottling fake = new();

		string line = PowerThrottling.OptOut(fake);

		Assert.Equal(new[] { (0x5u, 0u) }, fake.Calls);
		Assert.Equal("power: throttling opt-out ok (execution speed, timer resolution)", line);
	}

	[Fact]
	public void OptOut_TimerBitRejected_RetriesWithExecutionSpeedOnly()
	{
		FakeThrottling fake = new() { Errors = [87] };

		string line = PowerThrottling.OptOut(fake);

		Assert.Equal(new[] { (0x5u, 0u), (0x1u, 0u) }, fake.Calls);
		Assert.Equal("power: throttling opt-out ok (execution speed; timer resolution rejected, error 87)", line);
	}

	[Fact]
	public void OptOut_BothRejected_ReportsTheError()
	{
		FakeThrottling fake = new() { Errors = [87, 5] };

		Assert.Equal("power: throttling opt-out failed (error 5)", PowerThrottling.OptOut(fake));
		Assert.Equal(2, fake.Calls.Count);
	}

	[Fact]
	public void OptOutAndLog_ApiThrows_IsNotFatal_AndLogsOneLine()
	{
		ConcurrentQueue<string> lines = new();
		void Watch(string line)
		{
			if (line.StartsWith("power: throttling", StringComparison.Ordinal))
			{
				lines.Enqueue(line);
			}
		}
		FakeThrottling fake = new() { Throw = new EntryPointNotFoundException() };
		AppLog.Written += Watch;
		try
		{
			PowerThrottling.OptOutAndLog(fake);
		}
		finally
		{
			AppLog.Written -= Watch;
		}

		Assert.Single(fake.Calls);
		Assert.Contains("power: throttling opt-out failed (EntryPointNotFoundException)", lines);
	}

	[Fact]
	public void OptOut_RealCall_IsAcceptedOnThisWindows()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}
		Assert.StartsWith("power: throttling opt-out ok (execution speed", PowerThrottling.OptOut());
	}
}

public class HapticsTimingTests
{
	// A fake clock: 1000 ticks per ms.
	private const long Frequency = 1_000_000;

	private const long Ms = 1000;

	private const long LateTicks = 1 * Ms;

	private const long SlowTicks = 2 * Ms;

	[Fact]
	public void TimerWokeLate_CountsAsWakeLate_NotAsSlowWrite()
	{
		HapticsTiming t = default;

		t.Wrote(0, 300, SlowTicks);
		long lateBy = t.Woke(waitStarted: 300, deadline: 10 * Ms, now: 13 * Ms, LateTicks);

		Assert.Equal(3 * Ms, lateBy);
		Assert.Equal((1L, 3 * Ms), (t.Late, t.Worst));
		Assert.Equal((1L, 3 * Ms), (t.WakeLate, t.WakeWorst));
		Assert.Equal((0L, 300L), (t.SlowWrites, t.WriteWorst));
	}

	[Fact]
	public void SlowWritePastTheDeadline_IsLate_ButNotWakeLate()
	{
		HapticsTiming t = default;

		// The write ran 12 ms, past the 10 ms deadline: the wait returns at once, 2 ms late.
		t.Wrote(0, 12 * Ms, SlowTicks);
		long lateBy = t.Woke(waitStarted: 12 * Ms, deadline: 10 * Ms, now: 12 * Ms + 10, LateTicks);

		Assert.Equal(2 * Ms + 10, lateBy);
		Assert.Equal((1L, 2 * Ms + 10), (t.Late, t.Worst));
		Assert.Equal((0L, 0L), (t.WakeLate, t.WakeWorst));
		Assert.Equal((1L, 12 * Ms), (t.SlowWrites, t.WriteWorst));
	}

	[Fact]
	public void OnTime_AndThresholdsAreExclusive()
	{
		HapticsTiming t = default;

		t.Wrote(0, SlowTicks, SlowTicks);
		t.Woke(waitStarted: 0, deadline: 10 * Ms, now: 10 * Ms + LateTicks, LateTicks);
		t.Woke(waitStarted: 20 * Ms, deadline: 30 * Ms, now: 30 * Ms - 5, LateTicks);

		Assert.Equal((0L, LateTicks), (t.Late, t.Worst));
		Assert.Equal((0L, LateTicks), (t.WakeLate, t.WakeWorst));
		Assert.Equal((0L, SlowTicks), (t.SlowWrites, t.WriteWorst));
	}

	[Fact]
	public void Add_SumsCounts_AndKeepsTheWorst()
	{
		HapticsTiming a = new() { Late = 2, Worst = 5 * Ms, WakeLate = 1, WakeWorst = 4 * Ms, SlowWrites = 3, WriteWorst = 1 * Ms };
		HapticsTiming b = new() { Late = 1, Worst = 2 * Ms, WakeLate = 1, WakeWorst = 6 * Ms, SlowWrites = 0, WriteWorst = 9 * Ms };

		a.Add(b);

		Assert.Equal((3L, 5 * Ms, 2L, 6 * Ms, 3L, 9 * Ms), (a.Late, a.Worst, a.WakeLate, a.WakeWorst, a.SlowWrites, a.WriteWorst));
	}

	[Fact]
	public void Describe_GivesBothCauses()
	{
		HapticsTiming t = new() { Late = 3, Worst = 12_400, WakeLate = 1, WakeWorst = 1_200, SlowWrites = 2, WriteWorst = 11_900 };

		Assert.Equal("3 late, worst 12.40 ms, wake 1 late worst 1.20 ms, write 2 slow worst 11.90 ms", t.Describe(Frequency));
	}
}
