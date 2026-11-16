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
// play (then a short silence, so the end isn't cut off). While a game streams audio to the virtual pad (FeedHaptics),
// silent or not, the stream keeps running as it always did; it stops only once nothing feeds it at all. With
// PauseHapticsInSilence on (opt-in, and in Low Power mode) silence from the game does not keep it running either. While
// stopped, its one long-lived thread waits without a timer, sends nothing and holds no timer resolution.
public delegate void InputBodyHandler(ReadOnlySpan<byte> body);

public sealed class BluetoothSession : IDisposable
{
	// Reading the calibration feature report switches the controller from its short reports to full 0x31 reports.
	public const byte CalibrationFeature = 0x05;

	// About 1 s with nothing to play and nothing feeding the stream (no game audio packets, sound, rumble, speaker audio or
	// mic) before it stops: the game closed or released the pad's audio device. A stream fed by the game's audio never
	// stops on silence (stopping and starting again on every effect after a quiet moment would risk added latency).
	public const int SilentReportsBeforeStop = 94;

	// A game audio packet within this time counts as the game still feeding the stream (its packets come about every
	// 10 ms, sometimes held back by 20-30 ms).
	public static readonly TimeSpan FeedWindow = TimeSpan.FromMilliseconds(100);

	// A stream that never carried sound (the mic-off burst, a rumble start that stayed silent) stops after this many
	// silent reports instead (~250 ms).
	public const int ShortSilentReportsBeforeStop = 24;

	// Lateness beyond this many report intervals (a stalled thread, a suspended machine) restarts the schedule from now
	// instead of sending the missed reports back to back.
	public const int ResyncAfterIntervals = 3;

	private readonly IHidLink link;

	private readonly object outputGate = new();

	private readonly byte[] block = new byte[OutputBlock.Size];

	private readonly object hapticsGate = new();

	private Thread? reader;

	private Thread? hapticsThread;

	// The stream is running (guarded by hapticsGate); the thread waits on hapticsGate while it is not.
	private bool hapticsActive;

	private string startReason = string.Empty;

	private long hapticsStreams, hapticsThreadsStarted;

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
	public const int MicOffReports = ShortSilentReportsBeforeStop;

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

	// True while the haptics stream runs (it stops itself after a silent stretch; the thread then waits, idle).
	public bool IsHapticsStreaming
	{
		get
		{
			lock (hapticsGate)
			{
				return hapticsActive;
			}
		}
	}

	// Times the stream started (diagnostics, tests).
	public long HapticsStreams => Interlocked.Read(ref hapticsStreams);

	// Haptics threads created: 1 once the stream first ran, whatever the number of streams (diagnostics, tests).
	public long HapticsThreadsStarted => Interlocked.Read(ref hapticsThreadsStarted);

	// Silent frames kept buffered while the stream is idle: a sound that starts plays behind this cushion (~21 ms, as much
	// as the rumble keeps ahead), so the game's audio arriving in ~10 ms bursts does not run the first reports dry.
	public const int IdleCushionFrames = 2 * BluetoothReports.HapticsFrames;

	// Called on the haptics thread before each report takes its frames, for a source that keeps frames buffered ahead
	// (the bridge's rumble while the game sends no audio). Must be quick and must not call back into the stream.
	public Action? HapticsRefill { get; set; }

	// Makes the wait the haptics thread paces its reports with (tests); null = PreciseWait.Create.
	public Func<IPreciseWait>? PreciseWaitFactory { get; set; }

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

	// The light as last written to the controller (whoever set it); read-only, any thread.
	public Lighting.LightOutput SentLight => Volatile.Read(ref sentLight);

	private Lighting.LightOutput sentLight = Lighting.LightOutput.None;

	// Changes the output state and sends it at once.
	public void UpdateOutput(Action<byte[]> change)
	{
		lock (outputGate)
		{
			change(block);
			byte[] sent = (byte[])block.Clone();
			Volatile.Write(ref sentLight, sentLight.After(sent));
			if (HapticsMode)
			{
				OutputBlock.SetHapticsMode(sent);
			}
			byte[] report = new byte[Math.Max(link.Device.OutputLength, BluetoothReports.OutputSize)];
			BluetoothReports.BuildOutput(sent, NextSequence(), report);
			link.Write(report);
		}
	}

	// A source that streams continuously (the game's audio to the virtual pad) is still sending, sound or silence: the
	// stream starts if it is stopped and keeps running while the source keeps feeding it (call after pushing its frames).
	// With PauseHapticsInSilence on, only its sound counts (WakeHaptics).
	public void FeedHaptics()
	{
		Volatile.Write(ref lastFed, Time.GetTimestamp());
		WakeHaptics();
	}

	// The clock FeedWindow is measured on (tests simulate time with it).
	public TimeProvider Time { get; init; } = TimeProvider.System;

	private long lastFed = long.MinValue / 2;

	private bool Fed => !pauseInSilence && Time.GetElapsedTime(Volatile.Read(ref lastFed)) <= FeedWindow;

	// Opt-in (off by default; Low Power turns it on): the stream stops ~1 s after the last sound even while the game keeps
	// streaming silence, and starts again on the next sound behind IdleCushionFrames. Safe to switch at any time: turning
	// it on lets a running stream count its silence from then on; turning it off restarts a stopped stream at once if the
	// game is still feeding it.
	public bool PauseHapticsInSilence
	{
		get => pauseInSilence;
		set
		{
			pauseInSilence = value;
			if (!value)
			{
				WakeHaptics();
			}
		}
	}

	private volatile bool pauseInSilence;

	// Starts the haptics stream if it is idle and there is something to play (call after pushing frames into Haptics):
	// frames that are not silent, speaker audio, the mic, or mic-off reports. Silence alone does not start it; while idle
	// only the newest IdleCushionFrames of it are kept.
	public void WakeHaptics()
	{
		lock (hapticsGate)
		{
			if (hapticsActive || stopping)
			{
				return;
			}
			string? reason = StreamWork();
			if (reason == null)
			{
				Haptics.TrimSilence(IdleCushionFrames);
				return;
			}
			Activate(reason);
		}
	}

	// Starts the haptics stream even with nothing buffered yet (a source about to fill it, e.g. rumble); it stops again
	// after the silent tail if nothing comes.
	public void StartHaptics(string reason)
	{
		lock (hapticsGate)
		{
			if (!hapticsActive && !stopping)
			{
				Activate(reason);
			}
		}
	}

	// Caller holds hapticsGate.
	private void Activate(string reason)
	{
		hapticsActive = true;
		startReason = reason;
		if (hapticsThread == null)
		{
			Interlocked.Increment(ref hapticsThreadsStarted);
			hapticsThread = new Thread(HapticsLoop) { Name = "Controller haptics", IsBackground = true, Priority = ThreadPriority.Highest };
			hapticsThread.Start();
		}
		else
		{
			Monitor.Pulse(hapticsGate);
		}
	}

	public void Dispose()
	{
		stopping = true;
		link.CancelRead();
		reader?.Join(1000);
		Thread? haptics;
		lock (hapticsGate)
		{
			haptics = hapticsThread;
			Monitor.PulseAll(hapticsGate);
		}
		haptics?.Join(1000);
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

	// The haptics thread: started once, then waits (no timer, no spinning, nothing sent) until WakeHaptics or StartHaptics
	// makes it active, streams while there is something to play, and goes back to waiting after the silent tail.
	private void HapticsLoop()
	{
		sbyte[] left = new sbyte[BluetoothReports.HapticsFrames];
		sbyte[] right = new sbyte[BluetoothReports.HapticsFrames];
		byte[] report = new byte[Math.Max(link.Device.OutputLength, Audio.SpeakerReports.Size)];
		byte[] opus = new byte[Audio.SpeakerReports.OpusFrameBytes];
		IPreciseWait wait;
		try
		{
			wait = (PreciseWaitFactory ?? PreciseWait.Create)();
		}
		catch (Exception)
		{
			wait = PreciseWait.CreateFallback();
		}
		StreamTotals totals = new();
		try
		{
			while (true)
			{
				string reason;
				lock (hapticsGate)
				{
					while (!hapticsActive && !stopping)
					{
						Monitor.Wait(hapticsGate);
					}
					if (stopping)
					{
						break;
					}
					reason = startReason;
				}
				Stream(wait, reason, left, right, report, opus, ref totals);
			}
		}
		finally
		{
			wait.Dispose();
			lock (hapticsGate)
			{
				hapticsActive = false;
			}
			if (totals.Streams > 0)
			{
				Diagnostics.AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"bt: haptics stream: totals {totals.Streams} streams, {totals.ActiveMs / 1000.0:F1} s active, {totals.Reports} reports, {totals.Late} late, worst {totals.WorstMs:F2} ms, {totals.Resyncs} resyncs, timer {PreciseWait.Name(wait)}"));
			}
		}
	}

	// Reports more than this late (after the deadline) count as late in the stream log lines.
	public static readonly TimeSpan LateReport = TimeSpan.FromMilliseconds(1);

	// Start/stop lines written per session at most (the totals line at the end is always written).
	public const int StreamLogLimit = 100;

	private struct StreamTotals
	{
		public long Streams, Reports, Late, Resyncs;
		public double WorstMs, ActiveMs;
	}

	// One active stretch: a report every 10.67 ms until SilentReportsBeforeStop silent reports in a row (or
	// ShortSilentReportsBeforeStop when nothing audible was sent) with nothing left to play (and the mic off), then idle.
	private void Stream(IPreciseWait wait, string reason, sbyte[] left, sbyte[] right, byte[] report, byte[] opus, ref StreamTotals totals)
	{
		long interval = (long)(BluetoothReports.HapticsInterval.TotalSeconds * Stopwatch.Frequency);
		long lateTicks = (long)(LateReport.TotalSeconds * Stopwatch.Frequency);
		long started = Stopwatch.GetTimestamp();
		long next = started;
		long reports = 0, late = 0, worst = 0, resyncs = 0;
		int silent = 0;
		bool hadSound = false;
		byte counter = 0;
		long stream = ++totals.Streams;
		Interlocked.Increment(ref hapticsStreams);
		try
		{
			while (!stopping)
			{
				try
				{
					HapticsRefill?.Invoke();
				}
				catch (Exception)
				{
					// A refill source failing must not stop the stream.
				}
				bool sound = Haptics.Take(left, right);
				SpeakerAudioSender? s = speaker;
				bool speech = s != null && s.TakeFrame(opus);
				sound |= speech;
				// The game's audio still arriving (even silent) counts like sound: the stream keeps running.
				bool fed = sound || Fed;
				silent = fed ? 0 : silent + 1;
				hadSound |= fed;
				bool micOn = micStreamEnabled;
				bool sendingOff = !micOn && Volatile.Read(ref micOffToSend) > 0;
				if (silent > (hadSound ? SilentReportsBeforeStop : ShortSilentReportsBeforeStop) && !micOn && !sendingOff)
				{
					lock (hapticsGate)
					{
						if (StreamWork() == null)
						{
							hapticsActive = false;
							break;
						}
					}
					// Something arrived in the last moment (as before, when the stream started again at once).
					silent = 0;
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
				if (reports++ == 0 && stream <= StreamLogLimit)
				{
					// After the first report, so the log's file write never delays the first hit after silence.
					Diagnostics.AppLog.Write($"bt: haptics stream: started ({reason})");
				}
				next += interval;
				wait.WaitUntil(next, true);
				long now = Stopwatch.GetTimestamp();
				long lateBy = now - next;
				if (lateBy > lateTicks)
				{
					late++;
				}
				worst = Math.Max(worst, lateBy);
				if (lateBy > ResyncAfterIntervals * interval)
				{
					next = now;
					resyncs++;
				}
			}
		}
		finally
		{
			wait.Idle();
			if (stopping)
			{
				lock (hapticsGate)
				{
					hapticsActive = false;
				}
			}
		}
		double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
		double worstMs = worst * 1000.0 / Stopwatch.Frequency;
		totals.Reports += reports;
		totals.Late += late;
		totals.Resyncs += resyncs;
		totals.ActiveMs += ms;
		totals.WorstMs = Math.Max(totals.WorstMs, worstMs);
		if (stream <= StreamLogLimit)
		{
			Diagnostics.AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
				$"bt: haptics stream: stopped after {ms:F0} ms ({reports} reports, {late} late, worst {worstMs:F2} ms, {resyncs} resyncs, timer {PreciseWait.Name(wait)})"));
		}
		if (stream == StreamLogLimit)
		{
			Diagnostics.AppLog.Write($"bt: haptics stream: start/stop lines stop here ({StreamLogLimit} streams), totals follow when the session ends");
		}
	}

	// Why the stream should run now (a log word), or null when there is nothing to play: frames that are not silent,
	// speaker audio, the mic stream, or mic-off reports still to send. Caller holds hapticsGate.
	private string? StreamWork()
	{
		if (micStreamEnabled)
		{
			return "mic";
		}
		if (Volatile.Read(ref micOffToSend) > 0)
		{
			return "mic off";
		}
		if (Fed)
		{
			return "game audio";
		}
		if (Haptics.Audible > 0)
		{
			return "haptics";
		}
		return speaker?.Buffered > 0 ? "speaker" : null;
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
}
