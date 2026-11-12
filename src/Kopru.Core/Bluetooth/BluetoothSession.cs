// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Kopru.Core.Audio;
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

	// EXPERIMENTAL Bluetooth speaker / headset audio (null = off, the default). While it has audio, the haptics stream
	// sends report 0x36 (haptics + one Opus frame) instead of 0x32; route and volume follow the headphone jack.
	public SpeakerAudioSender? Speaker
	{
		get => speaker;
		set
		{
			if (value != null)
			{
				value.Headset = HeadphonesPlugged;
			}
			SpeakerAudioSender? was = speaker;
			speaker = value;
			if ((was != null) != (value != null))
			{
				Diagnostics.AppLog.Write(value != null ? "bt: speaker stream attached"
					: $"bt: speaker stream detached ({SpeakerReports} speaker reports, {was!.Underruns} underruns, {was.DroppedSamples} samples dropped)");
			}
			ApplySpeakerRoute();
		}
	}

	private SpeakerAudioSender? speaker;

	public long SpeakerReports => Interlocked.Read(ref speakerReports);

	private long speakerReports;

	// Feeds the game's 4-channel audio (channels 1-2 used) to Speaker and wakes the stream; no-op when Speaker is off.
	public void PushSpeakerAudio(ReadOnlySpan<byte> audio)
	{
		SpeakerAudioSender? s = speaker;
		if (s == null)
		{
			return;
		}
		s.Push(audio);
		WakeHaptics();
	}

	// Sends the current speaker volume and route (call after changing Speaker.Volume or HeadphoneVolume).
	public void ApplySpeakerRoute()
	{
		SpeakerAudioSender? s = speaker;
		UpdateOutput(b =>
		{
			if (s == null)
			{
				AudioOutput.Clear(b);
			}
			else
			{
				AudioOutput.SetRoute(b, s.Headset, s.OutputVolume);
			}
		});
	}

	// Puts the speaker route and volume into an output block about to be sent (e.g. the game's own output, which would
	// otherwise overwrite them); no change while Speaker is off.
	public void WriteSpeakerRoute(Span<byte> block)
	{
		SpeakerAudioSender? s = speaker;
		if (s != null)
		{
			AudioOutput.SetRoute(block, s.Headset, s.OutputVolume);
		}
	}

	// EXPERIMENTAL Bluetooth microphone (off by default): asks for the mic in the stream-control sub-packet of every
	// 0x32 / 0x36 report and keeps that stream running while on; reports marked as mic audio (MicPackets) go to
	// MicReportReceived. Mic reports (MicPackets: id 0x31, byte-1 type 2) are NEVER read as gamepad input, whether or
	// not the mic is on: measured on hardware, a controller asked once keeps sending them (about 100/s, valid input CRC)
	// in later sessions too, and read as input they move the left stick and press random buttons.
	// Switching off sends MicOffReports stream reports with the off value before the stream may stop (before, the stream
	// stopped at once and the controller never heard "off"). Mic reports arriving while the mic is off trigger the same
	// burst, at most once per StrayMicOffInterval (StopStrayMic).
	public bool MicStreamEnabled
	{
		get => micStreamEnabled;
		set
		{
			bool was = micStreamEnabled;
			micStreamEnabled = value;
			if (value != was)
			{
				Diagnostics.AppLog.Write(value
					? $"bt: mic stream on (streaming {IsHapticsStreaming})"
					: $"bt: mic stream off, sending mic off ({MicReports} mic reports, {StrayMicReports} while off, {InputReports} inputs, {RejectedReports} rejected)");
			}
			if (value)
			{
				WakeHaptics();
			}
			else if (was)
			{
				SendMicOff(MicOffReports);
			}
		}
	}

	// Stream reports carrying the mic-off value after the mic is switched off (about 250 ms).
	public const int MicOffReports = SilentReportsBeforeStop;

	public static readonly TimeSpan StrayMicOffInterval = TimeSpan.FromSeconds(2);

	// Mic reports while the mic is off make the session send "mic off" (bounded, see MicStreamEnabled). The probe's
	// control run turns this off to watch the controller untouched.
	public bool StopStrayMic { get; set; } = true;

	// Sends this many stream reports with the mic-off value (the stream runs at least that long) while the mic is off.
	public void SendMicOff(int reports)
	{
		Volatile.Write(ref micOffToSend, Math.Max(0, reports));
		WakeHaptics();
	}

	// Stream reports with the mic-off value still to send.
	public int MicOffPending => Volatile.Read(ref micOffToSend);

	// Mic-off stream reports sent in all (diagnostics, tests).
	public long MicOffSent => Interlocked.Read(ref micOffSent);

	private long micOffSent;

	private volatile bool micStreamEnabled;

	private int micOffToSend;

	private long lastStrayOff = long.MinValue;

	private int strayLogged;

	// Raised on the reader thread with each raw mic report (only while MicStreamEnabled).
	public event InputBodyHandler? MicReportReceived;

	// Every report read, before any filtering (diagnostics: Kopru.Probe mic dumps the unknown ones).
	public event InputBodyHandler? RawReportReceived;

	public long MicReports => Interlocked.Read(ref micReports);

	private long micReports;

	// Mic reports read while the mic was off (kept out of the input path, not decoded).
	public long StrayMicReports => Interlocked.Read(ref strayMicReports);

	private long strayMicReports;

	// Reports that were neither mic audio nor gamepad input with a valid CRC (dropped).
	public long RejectedReports => Interlocked.Read(ref rejectedReports);

	private long rejectedReports;

	// Headphones in the controller's jack (input status bit), tracked whether or not the speaker is on.
	public bool HeadphonesPlugged { get; private set; }

	public long InputReports => Interlocked.Read(ref inputReports);

	private long inputReports;

	// Stopwatch timestamp of the last successful read (valid on the reader thread, e.g. inside InputBodyReceived).
	public long LastReadTicks { get; private set; }

	public long HapticsReports => Interlocked.Read(ref hapticsReports);

	private long hapticsReports;

	// True while the haptics thread runs (it stops itself after a silent stretch).
	public bool IsHapticsStreaming
	{
		get
		{
			lock (hapticsGate)
			{
				return hapticsThread != null;
			}
		}
	}

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
			catch (Exception e)
			{
				if (stopping)
				{
					return;
				}
				if (++failures >= LostAfterFailures)
				{
					Diagnostics.AppLog.Write($"bt: {failures} reads failed in a row, controller lost ({e.GetType().Name}: {e.Message})");
					Lost?.Invoke();
					return;
				}
				Thread.Sleep(10);
				continue;
			}
			failures = 0;
			LastReadTicks = Stopwatch.GetTimestamp();
			ReadOnlySpan<byte> report = buffer.AsSpan(0, length);
			RawReportReceived?.Invoke(report);
			if (MicPackets.IsMicReport(report))
			{
				if (micStreamEnabled)
				{
					Interlocked.Increment(ref micReports);
					MicReportReceived?.Invoke(report);
				}
				else
				{
					OnStrayMicReport();
				}
				continue;
			}
			if (!BluetoothReports.InputCrcValid(report))
			{
				Interlocked.Increment(ref rejectedReports);
				continue;
			}
			ReadOnlySpan<byte> body = InputState.Body(report);
			LastInput = InputState.Parse(body);
			bool plugged = AudioOutput.HeadphonesPlugged(body);
			HeadphonesPlugged = plugged;
			SpeakerAudioSender? s = speaker;
			if (s != null && plugged != s.Headset)
			{
				s.Headset = !s.Headset;
				ApplySpeakerRoute();
			}
			Interlocked.Increment(ref inputReports);
			InputBodyReceived?.Invoke(body);
			InputReceived?.Invoke(LastInput);
		}
	}

	private void HapticsLoop()
	{
		sbyte[] left = new sbyte[BluetoothReports.HapticsFrames];
		sbyte[] right = new sbyte[BluetoothReports.HapticsFrames];
		byte[] report = new byte[Math.Max(link.Device.OutputLength, Audio.SpeakerReports.Size)];
		byte[] opus = new byte[Audio.SpeakerReports.OpusFrameBytes];
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
				SpeakerAudioSender? s = speaker;
				bool speech = s != null && s.TakeFrame(opus);
				sound |= speech;
				silent = sound ? 0 : silent + 1;
				bool micOn = micStreamEnabled;
				bool sendingOff = !micOn && Volatile.Read(ref micOffToSend) > 0;
				if (silent > SilentReportsBeforeStop && !micOn && !sendingOff)
				{
					break;
				}
				if (speech)
				{
					Audio.SpeakerReports.Build(left, right, opus, s!.Headset, NextSequence(), counter++, HapticsBufferDepth, report);
					Interlocked.Increment(ref speakerReports);
				}
				else
				{
					report.AsSpan(BluetoothReports.HapticsSize).Clear();
					BluetoothReports.BuildHaptics(left, right, NextSequence(), counter++, HapticsBufferDepth, report);
				}
				if (sendingOff)
				{
					// Both builders write the off value (MicPackets.StreamMicOff) into the stream-control byte.
					Interlocked.Decrement(ref micOffToSend);
					Interlocked.Increment(ref micOffSent);
				}
				if (micOn)
				{
					MicPackets.SetStreamControl(report, true);
					int crcAt = speech ? Audio.SpeakerReports.CrcOffset : BluetoothReports.HapticsSize - 4;
					System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(crcAt),
						Crc.Compute(Crc.OutputSeed, report.AsSpan(0, crcAt)));
				}
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
		// Frames pushed while the stream was ending start it again; so does the mic stream switched on in that moment (its
		// WakeHaptics still saw this thread and started nothing).
		if (!stopping && (Haptics.Buffered > 0 || speaker?.Buffered > 0 || micStreamEnabled || Volatile.Read(ref micOffToSend) > 0))
		{
			WakeHaptics();
		}
	}

	// A mic report while the mic is off: counted, never input; asks the controller to stop (bounded, see MicStreamEnabled).
	private void OnStrayMicReport()
	{
		Interlocked.Increment(ref strayMicReports);
		if (!StopStrayMic)
		{
			return;
		}
		long now = Stopwatch.GetTimestamp();
		long last = Interlocked.Read(ref lastStrayOff);
		if (last != long.MinValue && Stopwatch.GetElapsedTime(last, now) < StrayMicOffInterval)
		{
			return;
		}
		Interlocked.Exchange(ref lastStrayOff, now);
		if (Interlocked.Exchange(ref strayLogged, 1) == 0)
		{
			Diagnostics.AppLog.Write($"bt: mic reports arriving while the mic is off, sending mic off (at most every {StrayMicOffInterval.TotalSeconds:F0} s)");
		}
		SendMicOff(MicOffReports);
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
