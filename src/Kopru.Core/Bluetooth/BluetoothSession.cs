// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Kopru.Core.Haptics;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;

namespace Kopru.Core.Bluetooth;

// A DualSense over Bluetooth: switches it to full reports, reads its input, keeps one output state (triggers, lights,
// rumble) that goes out as report 0x31, and streams haptics as report 0x32 every 10.67 ms while there is something to
// play (then a short silence, so the end isn't cut off).
public delegate void InputBodyHandler(ReadOnlySpan<byte> body);

public sealed class BluetoothSession : IDisposable
{
	// Reading the calibration feature report switches the controller from its short reports to full 0x31 reports.
	public const byte CalibrationFeature = 0x05;

	// About 250 ms of silence after the last sound before the stream stops.
	public const int SilentReportsBeforeStop = 24;

	private readonly IHidLink link;

	private readonly object outputGate = new();

	private readonly byte[] block = new byte[OutputBlock.Size];

	private readonly object hapticsGate = new();

	private Thread? reader;

	private Thread? hapticsThread;

	private volatile bool stopping;

	private int sequence;

	public BluetoothSession(IHidLink link)
	{
		if (link.Device.Transport != Transport.Bluetooth)
		{
			throw new ArgumentException("Not a Bluetooth controller", nameof(link));
		}
		this.link = link;
	}

	public HidDevice Device => link.Device;

	public HapticsMixer Haptics { get; } = new();

	// Report 0x31 keeps the actuators free for the haptics stream (no rumble bits, power controls at 0).
	public bool HapticsMode { get; set; } = true;

	// How much the controller buffers before playing: 0x20 is about 50 ms, 0x08 about 25 ms.
	public byte HapticsBufferDepth { get; set; } = BluetoothReports.DefaultBufferDepth;

	public InputState LastInput { get; private set; }

	public long InputReports => Interlocked.Read(ref inputReports);

	private long inputReports;

	// Stopwatch timestamp of the last successful read (valid on the reader thread, e.g. inside InputBodyReceived).
	public long LastReadTicks { get; private set; }

	public long HapticsReports => Interlocked.Read(ref hapticsReports);

	private long hapticsReports;

	// Raised on the reader thread for every full input report.
	public event Action<InputState>? InputReceived;

	// Raised on the reader thread with the raw 63-byte input body (sticks, buttons, gyro, touch...) of every full input
	// report, before InputReceived. The span is only valid during the call.
	public event InputBodyHandler? InputBodyReceived;

	// Raised once (on the reader thread) when reads keep failing: the controller was switched off, went out of range or
	// was removed. Reading stops; dispose the session.
	public event Action? Lost;

	// Consecutive failed reads (10 ms apart) before the controller counts as lost.
	public int LostAfterFailures { get; set; } = 50;

	public void Start()
	{
		try
		{
			link.GetFeature(CalibrationFeature);
		}
		catch (Exception)
		{
			// Some controllers answer late right after connecting; the first 0x31 output switches them too.
		}
		// Stop the blue start-up glow, so the lightbar takes the colour that is set.
		UpdateOutput(b =>
		{
			b[OutputBlock.Flag2] |= OutputBlock.LightbarSetupControl;
			b[OutputBlock.LightbarSetup] = OutputBlock.LightOut;
		});
		UpdateOutput(b =>
		{
			b[OutputBlock.Flag2] &= unchecked((byte)~OutputBlock.LightbarSetupControl);
			b[OutputBlock.LightbarSetup] = 0;
		});
		reader = new Thread(ReadLoop) { Name = "Controller input", IsBackground = true, Priority = ThreadPriority.AboveNormal };
		reader.Start();
	}

	// Changes the output state and sends it at once.
	public void UpdateOutput(Action<byte[]> change)
	{
		lock (outputGate)
		{
			change(block);
			byte[] sent = (byte[])block.Clone();
			if (HapticsMode)
			{
				OutputBlock.SetHapticsMode(sent);
			}
			byte[] report = new byte[Math.Max(link.Device.OutputLength, BluetoothReports.OutputSize)];
			BluetoothReports.BuildOutput(sent, NextSequence(), report);
			link.Write(report);
		}
	}

	// Starts the haptics stream if it is idle (call after pushing frames into Haptics).
	public void WakeHaptics()
	{
		lock (hapticsGate)
		{
			if (hapticsThread == null && !stopping)
			{
				hapticsThread = new Thread(HapticsLoop) { Name = "Controller haptics", IsBackground = true, Priority = ThreadPriority.Highest };
				hapticsThread.Start();
			}
		}
	}

	public void Dispose()
	{
		stopping = true;
		link.CancelRead();
		reader?.Join(1000);
		hapticsThread?.Join(1000);
		link.Dispose();
	}

	private byte NextSequence() => (byte)(Interlocked.Increment(ref sequence) & 0x0F);

	private void ReadLoop()
	{
		byte[] buffer = new byte[Math.Max(link.Device.InputLength, BluetoothReports.InputSize)];
		int failures = 0;
		while (!stopping)
		{
			int length;
			try
			{
				length = link.Read(buffer);
			}
			catch (Exception)
			{
				if (stopping)
				{
					return;
				}
				if (++failures >= LostAfterFailures)
				{
					Lost?.Invoke();
					return;
				}
				Thread.Sleep(10);
				continue;
			}
			failures = 0;
			LastReadTicks = Stopwatch.GetTimestamp();
			ReadOnlySpan<byte> report = buffer.AsSpan(0, length);
			if (!BluetoothReports.InputCrcValid(report))
			{
				continue;
			}
			ReadOnlySpan<byte> body = InputState.Body(report);
			LastInput = InputState.Parse(body);
			Interlocked.Increment(ref inputReports);
			InputBodyReceived?.Invoke(body);
			InputReceived?.Invoke(LastInput);
		}
	}

	private void HapticsLoop()
	{
		sbyte[] left = new sbyte[BluetoothReports.HapticsFrames];
		sbyte[] right = new sbyte[BluetoothReports.HapticsFrames];
		byte[] report = new byte[Math.Max(link.Device.OutputLength, BluetoothReports.HapticsSize)];
		long interval = (long)(BluetoothReports.HapticsInterval.TotalSeconds * Stopwatch.Frequency);
		long next = Stopwatch.GetTimestamp();
		int silent = 0;
		byte counter = 0;
		TimerResolution.Begin();
		try
		{
			while (!stopping)
			{
				bool sound = Haptics.Take(left, right);
				silent = sound ? 0 : silent + 1;
				if (silent > SilentReportsBeforeStop)
				{
					break;
				}
				BluetoothReports.BuildHaptics(left, right, NextSequence(), counter++, HapticsBufferDepth, report);
				try
				{
					link.Write(report);
					Interlocked.Increment(ref hapticsReports);
				}
				catch (Exception)
				{
					// A lost report is a few ms of haptics; keep the stream going.
				}
				next += interval;
				WaitUntil(next);
			}
		}
		finally
		{
			TimerResolution.End();
			lock (hapticsGate)
			{
				hapticsThread = null;
			}
		}
		// Frames pushed while the stream was ending start it again.
		if (!stopping && Haptics.Buffered > 0)
		{
			WakeHaptics();
		}
	}

	private static void WaitUntil(long timestamp)
	{
		long remaining = timestamp - Stopwatch.GetTimestamp();
		long sleepMargin = Stopwatch.Frequency * 3 / 2000;
		if (remaining > sleepMargin)
		{
			Thread.Sleep(TimeSpan.FromSeconds((double)(remaining - sleepMargin) / Stopwatch.Frequency));
		}
		while (Stopwatch.GetTimestamp() < timestamp)
		{
			Thread.SpinWait(50);
		}
	}
}

// Windows sleeps in 15.6 ms steps unless a program asks for 1 ms; asked only while streaming.
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
