// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Kopru.Core.UsbIp;

namespace Kopru.Core.Virtual;

// Completes a virtual pad's isochronous transfers on a 1 ms frame clock, as a real bus would: consecutive transfers get
// consecutive frames and each completes when its last frame is due. Shared by the pads with USB audio (DualSense,
// DualShock 4). One background thread per pad, started with the pad: starting it on the first transfer would delay that
// submit by the thread start, and the next transfer would then be late and lose its contiguous start frame. It waits
// without a timer while nothing is scheduled.
internal sealed class IsoFrameClock : IDisposable
{
	private readonly object gate = new();

	// Scheduled completions, in due order (guarded by gate).
	private readonly List<IsoCompletion> scheduled = [];

	// Completions taken out as due (only touched by the iso thread).
	private readonly List<IsoCompletion> due = [];

	// Set when the iso thread should look again: something scheduled earlier than it waits for, or disposal.
	private readonly AutoResetEvent wake = new(false);

	private readonly Stopwatch clock = Stopwatch.StartNew();

	private readonly Thread thread;

	// Read when the first transfer is scheduled; null = Bluetooth.PreciseWait.Create.
	private readonly Func<Func<Bluetooth.IPreciseWait>?> waitFactory;

	// The due time the iso thread waits for (ms on clock; +inf while nothing is scheduled). Guarded by gate.
	private double waitingFor = double.PositiveInfinity;

	private long wakeups;

	private long? frameBase;

	private long nextFrame;

	private volatile bool disposed;

	private sealed record IsoCompletion(uint Sequence, double DueMs, Action<SubmitResult> Complete, SubmitResult Result);

	public IsoFrameClock(string threadName, Func<Func<Bluetooth.IPreciseWait>?> waitFactory)
	{
		this.waitFactory = waitFactory;
		thread = new Thread(Loop) { Name = threadName, IsBackground = true, Priority = ThreadPriority.AboveNormal };
		thread.Start();
	}

	// Times the iso thread woke up: once per due completion, none while nothing is scheduled.
	public long Wakeups => Interlocked.Read(ref wakeups);

	// An OUT transfer (game audio): every packet done in full; data is the caller's to use.
	public void ScheduleOut(SubmitCommand command, Action<SubmitResult> complete)
	{
		IsoPacket[] done = new IsoPacket[command.Packets.Length];
		for (int i = 0; i < done.Length; i++)
		{
			done[i] = command.Packets[i] with { ActualLength = command.Packets[i].Length, Status = 0 };
		}
		int startFrame = NextFrames(command, out double dueMs);
		Schedule(command.Sequence, dueMs, complete, new SubmitResult(0, command.Length, [], startFrame, done));
	}

	// An IN transfer (microphone): each packet gets up to bytesPerFrame bytes; fill writes the samples (or leaves silence).
	public void ScheduleIn(SubmitCommand command, Action<SubmitResult> complete, int bytesPerFrame, Action<byte[]>? fill)
	{
		IsoPacket[] done = new IsoPacket[command.Packets.Length];
		int offset = 0;
		for (int i = 0; i < done.Length; i++)
		{
			int actual = Math.Min(bytesPerFrame, command.Packets[i].Length);
			done[i] = command.Packets[i] with { ActualLength = actual, Status = 0 };
			offset += actual;
		}
		byte[] data = new byte[offset];
		try
		{
			fill?.Invoke(data);
		}
		catch (Exception)
		{
			Array.Clear(data);
		}
		int startFrame = NextFrames(command, out double dueMs);
		Schedule(command.Sequence, dueMs, complete, new SubmitResult(0, offset, data, startFrame, done));
	}

	public bool Unlink(uint sequence)
	{
		lock (gate)
		{
			return scheduled.RemoveAll(c => c.Sequence == sequence) > 0;
		}
	}

	// The host let go: pending transfers are dropped and the next one starts a new frame count.
	public void Reset()
	{
		lock (gate)
		{
			scheduled.Clear();
			frameBase = null;
		}
	}

	public void Dispose()
	{
		disposed = true;
		wake.Set();
		thread.Join(500);
	}

	// Consecutive transfers get consecutive frames; a gap (the host was late) starts again from now.
	private int NextFrames(SubmitCommand command, out double dueMs)
	{
		lock (gate)
		{
			double now = clock.Elapsed.TotalMilliseconds;
			frameBase ??= (long)now - command.StartFrame;
			long current = (long)now - frameBase.Value;
			long start = Math.Max(nextFrame, current);
			nextFrame = start + command.Packets.Length;
			dueMs = frameBase.Value + nextFrame;
			return (int)start;
		}
	}

	private void Schedule(uint sequence, double dueMs, Action<SubmitResult> complete, SubmitResult result)
	{
		bool earlier;
		lock (gate)
		{
			// Usually appended (consecutive transfers get later frames).
			int at = scheduled.Count;
			while (at > 0 && scheduled[at - 1].DueMs > dueMs)
			{
				at--;
			}
			scheduled.Insert(at, new IsoCompletion(sequence, dueMs, complete, result));
			earlier = dueMs < waitingFor;
			if (earlier)
			{
				waitingFor = dueMs;
			}
		}
		if (earlier)
		{
			wake.Set();
		}
	}

	// Completes scheduled transfers when they are due: waits for the earliest due time (high-resolution timer), or without
	// a timer while nothing is scheduled. Allocates nothing per pass.
	private void Loop()
	{
		Bluetooth.IPreciseWait? wait = null;
		try
		{
			Passes(ref wait);
		}
		finally
		{
			wait?.Dispose();
		}
	}

	private void Passes(ref Bluetooth.IPreciseWait? wait)
	{
		while (!disposed)
		{
			double dueMs;
			lock (gate)
			{
				double now = clock.Elapsed.TotalMilliseconds;
				int taken = 0;
				while (taken < scheduled.Count && scheduled[taken].DueMs <= now)
				{
					due.Add(scheduled[taken]);
					taken++;
				}
				scheduled.RemoveRange(0, taken);
				dueMs = scheduled.Count > 0 ? scheduled[0].DueMs : double.PositiveInfinity;
				waitingFor = dueMs;
			}
			foreach (IsoCompletion c in due)
			{
				c.Complete(c.Result);
			}
			due.Clear();
			if (double.IsPositiveInfinity(dueMs))
			{
				wait?.Idle();
				wake.WaitOne();
			}
			else
			{
				wait ??= CreateWait();
				long at = Stopwatch.GetTimestamp() + (long)((dueMs - clock.Elapsed.TotalMilliseconds) * Stopwatch.Frequency / 1000);
				wait.WaitUntil(at, false, wake);
			}
			Interlocked.Increment(ref wakeups);
		}
	}

	private Bluetooth.IPreciseWait CreateWait()
	{
		try
		{
			return (waitFactory() ?? Bluetooth.PreciseWait.Create)();
		}
		catch (Exception)
		{
			return Bluetooth.PreciseWait.CreateFallback();
		}
	}
}
