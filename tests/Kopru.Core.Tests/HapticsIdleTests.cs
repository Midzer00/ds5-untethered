// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Haptics;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;
using Xunit;

namespace Kopru.Core.Tests;

// A wait that does not wait: records the deadlines it was asked for, so a stream runs as fast as it can and its
// schedule can be checked exactly.
internal sealed class RecordingWait : IPreciseWait
{
	public ConcurrentQueue<long> Deadlines { get; } = new();

	public int Idles;

	// The call (0-based) that stalls the thread for StallMs instead of returning at once; -1 = none.
	public int StallAt = -1;

	public int StallMs = 80;

	private int calls;

	public bool HighResolution => true;

	public bool WaitUntil(long timestamp, bool spinFinal, WaitHandle? wake = null)
	{
		Deadlines.Enqueue(timestamp);
		if (Interlocked.Increment(ref calls) - 1 == StallAt)
		{
			Thread.Sleep(StallMs);
		}
		return true;
	}

	public void Idle() => Interlocked.Increment(ref Idles);

	public void Dispose()
	{
	}
}

// Most of these run with PauseHapticsInSilence on (the opt-in mode, also used by Low Power); the default mode, where the
// game's audio packets keep the stream running, is covered by HapticsFeedTests.
// Measures real timer pacing: runs one class at a time (collection "RealTime") and, in tools/test.ps1, in its own phase
// with nothing else running (Category=RealTime).
[Trait("Category", "RealTime")]
[Collection("RealTime")]
public class HapticsIdleTests
{
	private static readonly IsoPacket[] Packets = Enumerable.Range(0, 10).Select(i => new IsoPacket(i * 384, 384, 0, 0)).ToArray();

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private static VirtualDualSense Pad(Func<IPreciseWait>? wait = null) => new(DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
		[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
		new Dictionary<int, string> { [1] = "Sony Interactive Entertainment", [2] = "DualSense Wireless Controller" },
		new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] })) { PreciseWaitFactory = wait };

	private static SubmitCommand Audio(uint seq, byte[] data) =>
		new(seq, 0, UsbIpCodes.DirOut, VirtualDualSense.AudioOutEndpoint, 0, data.Length, 0, Packets.Length, 1, new byte[8], data, Packets);

	private static readonly byte[] Silence = HapticsConverterTests.Audio(480);

	private static readonly byte[] Tone = HapticsConverterTests.Audio(480, ch3: HapticsConverterTests.Tone(100, 0.5), ch4: HapticsConverterTests.Tone(100, 0.5));

	private static int HapticsWrites(FakeLink link) => link.Writes.Count(w => w[0] == BluetoothReports.HapticsId);

	[Fact]
	public void PauseMode_SilentGameAudio_NeverStartsTheStream_AndKeepsOnlyACushion()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link) { PauseHapticsInSilence = true };
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);

		for (uint i = 0; i < 50; i++)
		{
			pad.Submit(Audio(i, Silence), _ => { });
		}
		Thread.Sleep(100);

		Assert.False(session.IsHapticsStreaming);
		Assert.Equal(0, session.HapticsStreams);
		Assert.Equal(0, session.HapticsThreadsStarted);
		Assert.Equal(0, HapticsWrites(link));
		Assert.InRange(session.Haptics.Buffered, 1, BluetoothSession.IdleCushionFrames);
	}

	[Fact]
	public void PauseMode_Sound_StartsTheStream_SilenceThatFollowsStopsIt_AfterTheTail()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link) { PauseHapticsInSilence = true };
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);

		// The game keeps streaming (silence after one tone packet) for about 1.8 s, as it does while the pad's audio
		// device is open: the stream must stop after its silent tail (~1 s) all the same.
		pad.Submit(Audio(0, Tone), _ => { });
		Assert.True(session.IsHapticsStreaming);
		Stopwatch clock = Stopwatch.StartNew();
		uint seq = 1;
		bool stopped = false;
		while (clock.ElapsedMilliseconds < 1800)
		{
			pad.Submit(Audio(seq++, Silence), _ => { });
			stopped |= !session.IsHapticsStreaming;
			Thread.Sleep(10);
		}

		Assert.True(stopped, "the stream kept running on silence");
		Assert.False(session.IsHapticsStreaming);
		long reports = session.HapticsReports;
		// The tone (10 ms = 30 frames) behind the cushion: about two reports with sound, then the silent tail.
		Assert.InRange(reports, BluetoothSession.SilentReportsBeforeStop + 1, BluetoothSession.SilentReportsBeforeStop + 6);
		Assert.Equal(1, session.HapticsStreams);
		Thread.Sleep(100);
		Assert.Equal(reports, session.HapticsReports);
	}

	[Fact]
	public void PauseMode_SoundAfterSilence_StartsAgain_OnTheSameThread_WithTheSoundAtTheFront()
	{
		FakeLink link = new();
		RecordingWait wait = new();
		using BluetoothSession session = new(link) { PauseHapticsInSilence = true, PreciseWaitFactory = () => wait };
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { GameHapticsLevel = 1.0 };

		for (int round = 1; round <= 3; round++)
		{
			// Silence (the previous tone's filter tail in it may still play) until the stream is idle.
			for (uint i = 0; i < 20; i++)
			{
				pad.Submit(Audio(i, Silence), _ => { });
			}
			WaitUntil(() => !session.IsHapticsStreaming);
			for (uint i = 20; i < 40; i++)
			{
				pad.Submit(Audio(i, Silence), _ => { });
			}
			Assert.False(session.IsHapticsStreaming);
			long streams = session.HapticsStreams;
			int before = HapticsWrites(link);
			pad.Submit(Audio(100, Tone), _ => { });
			WaitUntil(() => session.HapticsStreams > streams && !session.IsHapticsStreaming);

			byte[][] sent = link.Writes.Where(w => w[0] == BluetoothReports.HapticsId).Skip(before).ToArray();
			// The tone plays within the first reports (behind at most the idle cushion), not after a buffer of silence.
			int firstSound = Array.FindIndex(sent, w => w.AsSpan(13, 64).ToArray().Any(b => b != 0));
			Assert.InRange(firstSound, 0, BluetoothSession.IdleCushionFrames / BluetoothReports.HapticsFrames);
		}

		Assert.Equal(1, session.HapticsThreadsStarted);
		Assert.Equal(session.HapticsStreams, wait.Idles);
	}

	[Fact]
	public void Stream_KeepsTheReportSchedule_EveryInterval()
	{
		FakeLink link = new();
		RecordingWait wait = new();
		using BluetoothSession session = new(link) { PreciseWaitFactory = () => wait };
		for (int i = 0; i < HapticsMixer.MaxFrames; i++)
		{
			session.Haptics.Push(20, -20);
		}

		session.WakeHaptics();
		WaitUntil(() => session.HapticsStreams == 1 && !session.IsHapticsStreaming);

		long interval = (long)(BluetoothReports.HapticsInterval.TotalSeconds * Stopwatch.Frequency);
		long[] deadlines = wait.Deadlines.ToArray();
		Assert.Equal(HapticsMixer.MaxFrames / BluetoothReports.HapticsFrames + BluetoothSession.SilentReportsBeforeStop, deadlines.Length);
		Assert.Equal(deadlines.Length, session.HapticsReports);
		for (int i = 1; i < deadlines.Length; i++)
		{
			Assert.Equal(interval, deadlines[i] - deadlines[i - 1]);
		}
	}

	[Fact]
	public void Stream_LogsStartAndStop_OncePerStream()
	{
		ConcurrentQueue<string> lines = new();
		FakeLink link = new();
		int writesAtStart = -1;
		void Watch(string line)
		{
			if (line.StartsWith("bt: haptics stream:", StringComparison.Ordinal))
			{
				if (line.StartsWith("bt: haptics stream: started", StringComparison.Ordinal))
				{
					// The log is shared with tests running in parallel: keep the highest count seen.
					writesAtStart = Math.Max(writesAtStart, HapticsWrites(link));
				}
				lines.Enqueue(line);
			}
		}
		Diagnostics.AppLog.Written += Watch;
		try
		{
			RecordingWait wait = new();
			using (BluetoothSession session = new(link) { PreciseWaitFactory = () => wait })
			{
				for (int i = 0; i < 64; i++)
				{
					session.Haptics.Push(30, 30);
				}
				session.WakeHaptics();
				WaitUntil(() => session.HapticsStreams == 1 && !session.IsHapticsStreaming);
			}
		}
		finally
		{
			Diagnostics.AppLog.Written -= Watch;
		}

		string[] mine = [.. lines];
		Assert.Contains("bt: haptics stream: started (haptics)", mine);
		Assert.Contains(mine, l => l.StartsWith("bt: haptics stream: stopped after ", StringComparison.Ordinal)
			&& l.Contains($"({2 + BluetoothSession.SilentReportsBeforeStop} reports, 0 late", StringComparison.Ordinal)
			&& l.Contains(", wake 0 late worst ", StringComparison.Ordinal) && l.Contains(" slow worst ", StringComparison.Ordinal)
			&& l.EndsWith("timer high-resolution)", StringComparison.Ordinal));
		Assert.Contains(mine, l => l.StartsWith("bt: haptics stream: totals 1 streams", StringComparison.Ordinal));
		// Logged after the first report went out, so the file write never delays the first hit.
		Assert.True(writesAtStart >= 1, $"{writesAtStart} reports before the start line");
	}

	[Fact]
	public void MicOn_KeepsTheStreamRunning_EvenInSilence()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);

		session.MicStreamEnabled = true;
		WaitUntil(() => session.HapticsReports > BluetoothSession.SilentReportsBeforeStop + 5);
		Assert.True(session.IsHapticsStreaming);

		session.MicStreamEnabled = false;
		WaitUntil(() => !session.IsHapticsStreaming);
		Assert.Equal(1, session.HapticsThreadsStarted);
	}

	[Fact]
	public void IdleBridge_RumbleAndTimingThreads_DoNotWake()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);

		Thread.Sleep(200);

		Assert.Equal(0, pad.IsoWakeups);
		Assert.Equal(0, bridge.RumbleRefills);
		Assert.Equal(0, session.HapticsThreadsStarted);
		Assert.Equal(0, HapticsWrites(link));
	}

	[Fact]
	public void TimingThread_WakesForDueTransfers_ThenWaitsWithoutPolling()
	{
		using VirtualDualSense pad = Pad();
		int completed = 0;

		for (uint i = 0; i < 5; i++)
		{
			pad.Submit(Audio(i, Silence), _ => Interlocked.Increment(ref completed));
		}
		WaitUntil(() => Volatile.Read(ref completed) == 5);
		Thread.Sleep(20);
		long wakeups = pad.IsoWakeups;
		Thread.Sleep(200);

		// 5 transfers of 10 frames each: about one wake per transfer (polling every 1 ms would be ~50 by now, ~250 after).
		Assert.InRange(wakeups, 1, 12);
		Assert.Equal(wakeups, pad.IsoWakeups);
	}

	[Fact]
	public void TimingThread_CompletesInDueOrder_NotEarly()
	{
		using VirtualDualSense pad = Pad();
		ConcurrentQueue<(uint Seq, long At)> done = new();
		Stopwatch clock = Stopwatch.StartNew();

		for (uint i = 0; i < 3; i++)
		{
			uint seq = i;
			pad.Submit(Audio(seq, Silence), _ => done.Enqueue((seq, clock.ElapsedMilliseconds)));
		}
		WaitUntil(() => done.Count == 3);

		(uint Seq, long At)[] order = [.. done];
		Assert.Equal([0u, 1u, 2u], order.Select(o => o.Seq));
		// 10, 20 and 30 frames of 1 ms after the first transfer.
		Assert.True(order[2].At >= 28, $"completed after {order[2].At} ms");
	}

	[Fact]
	public void Rumble_WithoutGameAudio_IsToppedUpByTheStream_AndStopsWithTheMotors()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);

		SendRumble(pad, 200, 200);
		WaitUntil(() => session.HapticsReports > 20);
		Assert.True(bridge.RumbleRefills > 5);
		// Rumble keeps the stream audible: no silent tail while the motors run.
		Assert.True(session.IsHapticsStreaming);

		SendRumble(pad, 0, 0);
		WaitUntil(() => !session.IsHapticsStreaming);
		long refills = bridge.RumbleRefills;
		Thread.Sleep(100);
		Assert.Equal(refills, bridge.RumbleRefills);
		Assert.Equal(1, session.HapticsStreams);
	}

	[Fact]
	public void Rumble_SilentAtLevelZero_StartsAgainWhenTheLevelIsRaised()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { RumbleLevel = 0 };

		SendRumble(pad, 200, 200);
		Thread.Sleep(100);
		Assert.False(session.IsHapticsStreaming);

		// The game keeps the motors where they are (its identical reports are dropped); the user raises the level.
		bridge.RumbleLevel = 1;
		WaitUntil(() => link.Writes.Any(w => w[0] == BluetoothReports.HapticsId && w.AsSpan(13, 64).ToArray().Any(b => b != 0)));
		Assert.True(session.IsHapticsStreaming);
		Assert.True(bridge.RumbleRefills > 0);

		SendRumble(pad, 0, 0);
		WaitUntil(() => !session.IsHapticsStreaming);
		// A profile with rumble while the motors are off starts nothing.
		long streams = session.HapticsStreams;
		bridge.ApplyProfile(new BridgeProfile(RumbleLevel: 0.8));
		Thread.Sleep(50);
		Assert.Equal(streams, session.HapticsStreams);
	}

	[Fact]
	public void Rumble_AppliedProfile_RestartsAStreamThatWentIdle()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { RumbleLevel = 0 };
		SendRumble(pad, 100, 0);
		Thread.Sleep(50);
		Assert.False(session.IsHapticsStreaming);

		bridge.ApplyProfile(new BridgeProfile(RumbleLevel: 1.0));

		WaitUntil(() => session.IsHapticsStreaming && bridge.RumbleRefills > 0);
	}

	[Fact]
	public void PauseMode_Rumble_StartsAtOnce_NotBehindTheIdleCushion()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link) { PauseHapticsInSilence = true };
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		for (uint i = 0; i < 10; i++)
		{
			pad.Submit(Audio(i, Silence), _ => { });
		}
		Assert.Equal(BluetoothSession.IdleCushionFrames, session.Haptics.Buffered);
		Thread.Sleep(100); // the game's audio counts as stopped: the rumble is not mixed into it

		SendRumble(pad, 255, 255);

		WaitUntil(() => HapticsWrites(link) > 0);
		byte[] first = link.Writes.First(w => w[0] == BluetoothReports.HapticsId);
		Assert.Contains(first.AsSpan(13, 64).ToArray(), b => b != 0);
	}

	[Fact]
	public void SpeakerOnly_StartsTheStream_AndStopsAfterTheTail()
	{
		FakeLink link = new();
		RecordingWait wait = new();
		using BluetoothSession session = new(link) { PreciseWaitFactory = () => wait, Speaker = new SpeakerAudioSender(new FakeOpusEncoder()) };
		byte[] speech = HapticsConverterTests.Audio(4800, ch1: HapticsConverterTests.Tone(300, 0.5), ch2: HapticsConverterTests.Tone(300, 0.5));

		session.PushSpeakerAudio(speech);

		WaitUntil(() => session.HapticsStreams == 1 && !session.IsHapticsStreaming);
		Assert.True(session.SpeakerReports > 5);
		Assert.Equal(session.SpeakerReports + BluetoothSession.SilentReportsBeforeStop,session.HapticsReports);
		Assert.Equal(0, session.Speaker!.Buffered);
		Thread.Sleep(50);
		Assert.Equal(1, session.HapticsStreams);
	}

	[Fact]
	public void MicOffBurst_FromIdle_SendsItsReports_ThenStopsWithoutTheLongTail()
	{
		FakeLink link = new();
		RecordingWait wait = new();
		using BluetoothSession session = new(link) { PreciseWaitFactory = () => wait };

		session.SendMicOff(BluetoothSession.MicOffReports);

		WaitUntil(() => session.HapticsStreams == 1 && !session.IsHapticsStreaming);
		Assert.Equal(BluetoothSession.MicOffReports, session.MicOffSent);
		Assert.Equal(BluetoothSession.MicOffReports, HapticsWrites(link));
		Assert.Equal(0, session.MicOffPending);
	}

	[Fact]
	public void Stream_AfterAStall_ResyncsInsteadOfBursting()
	{
		ConcurrentQueue<string> lines = new();
		void Watch(string line)
		{
			if (line.StartsWith("bt: haptics stream: stopped", StringComparison.Ordinal))
			{
				lines.Enqueue(line);
			}
		}
		FakeLink link = new();
		RecordingWait wait = new() { StallAt = 2, StallMs = 80 };
		Diagnostics.AppLog.Written += Watch;
		try
		{
			using BluetoothSession session = new(link) { PreciseWaitFactory = () => wait };
			for (int i = 0; i < 64; i++)
			{
				session.Haptics.Push(30, 30);
			}
			session.WakeHaptics();
			WaitUntil(() => session.HapticsStreams == 1 && !session.IsHapticsStreaming);
		}
		finally
		{
			Diagnostics.AppLog.Written -= Watch;
		}

		long interval = (long)(BluetoothReports.HapticsInterval.TotalSeconds * Stopwatch.Frequency);
		long[] deadlines = wait.Deadlines.ToArray();
		Assert.Equal(interval, deadlines[2] - deadlines[1]);
		// 80 ms late (more than 3 intervals): the next deadline is one interval after the stall ended, the missed reports
		// are not sent back to back.
		Assert.True(deadlines[3] - deadlines[2] > 5 * interval);
		for (int i = 4; i < deadlines.Length; i++)
		{
			Assert.Equal(interval, deadlines[i] - deadlines[i - 1]);
		}
		Assert.Contains(lines, l => l.Contains(" 1 resyncs,", StringComparison.Ordinal));
		// The stall happened inside the wait (it began before the deadline): counted as the timer waking late.
		Assert.Contains(lines, l => l.Contains(", wake ", StringComparison.Ordinal) && !l.Contains(", wake 0 late", StringComparison.Ordinal));
	}

	[Fact]
	public void TimingThread_UsesTheWaitFromItsFactory()
	{
		CountingWait counting = new(PreciseWait.Create());
		using VirtualDualSense pad = Pad(() => counting);
		int completed = 0;

		pad.Submit(Audio(1, Silence), _ => Interlocked.Increment(ref completed));

		WaitUntil(() => Volatile.Read(ref completed) == 1);
		Assert.True(counting.Waits > 0);
	}

	private sealed class CountingWait(IPreciseWait inner) : IPreciseWait
	{
		public int Waits;

		public bool HighResolution => inner.HighResolution;

		public bool WaitUntil(long timestamp, bool spinFinal, WaitHandle? wake = null)
		{
			Interlocked.Increment(ref Waits);
			return inner.WaitUntil(timestamp, spinFinal, wake);
		}

		public void Idle() => inner.Idle();

		public void Dispose() => inner.Dispose();
	}

	private static void SendRumble(VirtualDualSense pad, byte left, byte right)
	{
		byte[] block = new byte[OutputBlock.Size];
		OutputBlock.SetRumble(block, left, right);
		byte[] report = new byte[UsbReports.OutputSize];
		UsbReports.BuildOutput(block, report);
		pad.Submit(new SubmitCommand(5, 0, UsbIpCodes.DirOut, VirtualDualSense.HidOutEndpoint, 0, report.Length, 0, -1, 1, new byte[8], report, []), _ => { });
	}
}

public class HapticsIdleMixerTests
{
	[Fact]
	public void Audible_CountsFramesThatAreNotSilent_ThroughPushTakeAndOverflow()
	{
		HapticsMixer mixer = new();
		mixer.Push(0, 0);
		mixer.Push(5, 0);
		mixer.Push([0, 0, 3], [0, 1, 0]);
		Assert.Equal((5, 3), (mixer.Buffered, mixer.Audible));

		sbyte[] left = new sbyte[2], right = new sbyte[2];
		mixer.Take(left, right);
		Assert.Equal((3, 2), (mixer.Buffered, mixer.Audible));

		for (int i = 0; i < HapticsMixer.MaxFrames; i++)
		{
			mixer.Push(0, 0);
		}
		Assert.Equal((HapticsMixer.MaxFrames, 0), (mixer.Buffered, mixer.Audible));
	}

	[Fact]
	public void TrimSilence_KeepsTheNewestSilence_AndNeverDropsSound()
	{
		HapticsMixer mixer = new();
		for (int i = 0; i < 100; i++)
		{
			mixer.Push(0, 0);
		}
		mixer.TrimSilence(64);
		Assert.Equal(64, mixer.Buffered);

		mixer.Push(9, 9);
		mixer.TrimSilence(10);
		Assert.Equal(65, mixer.Buffered);
	}
}

public class HapticsIdleAllocationTests
{
	[Fact]
	public void SpeakerPush_AllocatesNothingPerSample()
	{
		using SpeakerAudioSender sender = new(new FakeOpusEncoder());
		byte[] audio = HapticsConverterTests.Audio(480, ch1: HapticsConverterTests.Tone(440, 0.5), ch2: HapticsConverterTests.Tone(440, 0.5));
		sender.Push(audio);
		byte[] opus = new byte[SpeakerReports.OpusFrameBytes];
		while (sender.TakeFrame(opus))
		{
		}

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 10; i++)
		{
			sender.Push(audio);
		}
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		// 4800 input samples; before, every one made a new delegate (~64 bytes each).
		Assert.Equal(0, allocated);
	}

	[Fact]
	public void HapticsConversion_IntoTheMixer_AllocatesNothing()
	{
		HapticsConverter converter = new();
		HapticsMixer mixer = new();
		byte[] tone = HapticsConverterTests.Audio(480, ch3: HapticsConverterTests.Tone(100, 0.5));
		sbyte[] left = new sbyte[HapticsConverter.MaxFrames(tone.Length)], right = new sbyte[left.Length];
		converter.Convert(tone, left, right);
		converter.Convert(tone, mixer);

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 10; i++)
		{
			int frames = converter.Convert(tone, left, right);
			mixer.Push(left.AsSpan(0, frames), right.AsSpan(0, frames));
			converter.Convert(tone, mixer);
		}

		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
	}

	[Fact]
	public void SilenceFastPath_ConvertsExactlyAsBefore()
	{
		// Sound, then a long silence, then sound again, in odd chunks: the block converter matches frame-by-frame output.
		byte[] audio = [.. HapticsConverterTests.Audio(1000, ch3: HapticsConverterTests.Tone(80, 0.7), ch4: HapticsConverterTests.Tone(120, 0.3)),
			.. HapticsConverterTests.Audio(48000), .. HapticsConverterTests.Audio(500, ch3: HapticsConverterTests.Tone(200, 0.9))];
		HapticsConverter a = new(), b = new();
		List<(sbyte, sbyte)> byFrame = [];
		a.Convert(audio, (l, r) => byFrame.Add((l, r)));
		List<(sbyte, sbyte)> byBlock = [];
		sbyte[] left = new sbyte[HapticsConverter.MaxFrames(777)], right = new sbyte[left.Length];
		for (int at = 0; at < audio.Length; at += 777)
		{
			int n = b.Convert(audio.AsSpan(at, Math.Min(777, audio.Length - at)), left, right);
			for (int i = 0; i < n; i++)
			{
				byBlock.Add((left[i], right[i]));
			}
		}

		Assert.Equal(audio.Length / 8 / HapticsConverter.Decimation, byFrame.Count);
		Assert.Equal(byFrame, byBlock);
		Assert.Contains(byFrame.Take(60), f => f.Item1 != 0);
		Assert.All(byFrame.Skip(200).Take(2500), f => Assert.Equal(((sbyte)0, (sbyte)0), f));
	}
}

// Measures real timer pacing: runs one class at a time (collection "RealTime") and, in tools/test.ps1, in its own phase
// with nothing else running (Category=RealTime).
//
// What is checked strictly: a wait never returns before its deadline, the high-resolution timer stays in use, and a
// failed arming switches to the fallback for good. How late a wait may return depends on the machine at that moment:
// the fallback sleeps in whole timer ticks, and Windows 11 may ignore a process's request for 1 ms ticks (a process
// whose windows are hidden or minimized, or one it counts as background) and then sleeps in 15.6 ms ticks; a busy
// machine also wakes threads late. So the lateness bound is set from the timer granularity measured right then, and
// one noisy measurement is repeated.
[Trait("Category", "RealTime")]
[Collection("RealTime")]
public class PreciseWaitTests
{
	// Waits measured per attempt (10.67 ms apart).
	private const int Samples = 40;

	// The median lateness allowed when the machine sleeps in ~1 ms ticks (measured: 0.00 ms high-resolution, ~0.0-0.5 ms
	// fallback on an idle machine).
	private const double PreciseBoundMs = 2;

	[Fact]
	public void HighResolutionTimer_IsAvailable_AndNeverWakesEarly()
	{
		using IPreciseWait wait = PreciseWait.Create();
		Assert.True(wait.HighResolution);
		AssertPaces(wait);
		Assert.True(wait.HighResolution);
	}

	[Fact]
	public void Fallback_NeverWakesEarly()
	{
		using IPreciseWait wait = PreciseWait.CreateFallback();
		AssertPaces(wait);
		wait.Idle();
	}

	[Fact]
	public void HighResolution_WhenArmingFails_WaitsWithTheFallback_NeverEarly()
	{
		int armed = 0;
		using IPreciseWait wait = PreciseWait.CreateHighResolution(_ =>
		{
			Interlocked.Increment(ref armed);
			return false;
		})!;
		Assert.True(wait.HighResolution);

		Stopwatch clock = Stopwatch.StartNew();
		int waited = AssertPaces(wait);

		// Every deadline (10.67 ms apart) was really waited for (falling through would take ~0 ms), and the timer was
		// tried only once before switching for good.
		Assert.True(clock.Elapsed.TotalMilliseconds >= waited * BluetoothReports.HapticsInterval.TotalMilliseconds - 1);
		Assert.False(wait.HighResolution);
		Assert.Equal(1, armed);
		wait.Idle();
	}

	[Fact]
	public void Wait_ReturnsEarly_WhenWoken()
	{
		using IPreciseWait wait = PreciseWait.Create();
		using AutoResetEvent wake = new(true);
		Stopwatch clock = Stopwatch.StartNew();
		Assert.False(wait.WaitUntil(Stopwatch.GetTimestamp() + Stopwatch.Frequency, false, wake));
		Assert.True(clock.ElapsedMilliseconds < 500);
	}

	[Fact]
	public void LatenessBound_FollowsTheMeasuredTimerGranularity()
	{
		Assert.Equal(PreciseBoundMs, LatenessBound(1.1));
		Assert.Equal(PreciseBoundMs, LatenessBound(2));
		Assert.Equal(PreciseBoundMs + 13.6, LatenessBound(15.6), 6);
	}

	// Paces Samples waits like the haptics stream; returns how many deadlines were waited for. Never early, on every
	// attempt; the median lateness within LatenessBound, on one of two attempts.
	private static int AssertPaces(IPreciseWait wait)
	{
		string failure = "";
		int waited = 0;
		for (int attempt = 0; attempt < 2; attempt++)
		{
			List<double> late = Pace(wait);
			waited += late.Count;
			Assert.All(late, l => Assert.True(l >= 0, $"woke {-l:F3} ms early"));
			double granularity = SleepGranularityMs();
			double median = Median(late);
			double bound = LatenessBound(granularity);
			if (median < bound)
			{
				return waited;
			}
			failure += $"attempt {attempt + 1}: median {median:F3} ms late (bound {bound:F2} ms, sleep granularity {granularity:F2} ms, p90 {late[late.Count * 9 / 10]:F3} ms); ";
		}
		Assert.Fail(failure);
		return waited;
	}

	private static List<double> Pace(IPreciseWait wait)
	{
		long interval = (long)(BluetoothReports.HapticsInterval.TotalSeconds * Stopwatch.Frequency);
		long next = Stopwatch.GetTimestamp();
		List<double> late = [];
		for (int i = 0; i < Samples; i++)
		{
			next += interval;
			Assert.True(wait.WaitUntil(next, true));
			late.Add((Stopwatch.GetTimestamp() - next) * 1000.0 / Stopwatch.Frequency);
		}
		late.Sort();
		return late;
	}

	private static double Median(List<double> sorted) => sorted[sorted.Count / 2];

	// The median lateness allowed: PreciseBoundMs while a 1 ms sleep takes up to 2 ms (the 1 ms timer resolution the
	// fallback asks for is granted and the machine wakes threads on time); otherwise as much more as the sleeps take
	// longer (a coarse timer or a loaded machine delays any wait by about that much).
	internal static double LatenessBound(double sleepGranularityMs) => PreciseBoundMs + Math.Max(0, sleepGranularityMs - 2);

	// How long a 1 ms sleep takes right now (median of 15) in a process that asks for 1 ms timer resolution, as the
	// fallback does: ~1-2 ms when it is granted, ~15.6 ms when Windows ignores the request, longer on a loaded machine.
	private static double SleepGranularityMs()
	{
		if (OperatingSystem.IsWindows())
		{
			_ = timeBeginPeriod(1);
		}
		try
		{
			List<double> sleeps = [];
			for (int i = 0; i < 15; i++)
			{
				long start = Stopwatch.GetTimestamp();
				Thread.Sleep(1);
				sleeps.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
			}
			sleeps.Sort();
			return Median(sleeps);
		}
		finally
		{
			if (OperatingSystem.IsWindows())
			{
				_ = timeEndPeriod(1);
			}
		}
	}

	[System.Runtime.InteropServices.DllImport("winmm.dll")]
	private static extern uint timeBeginPeriod(uint period);

	[System.Runtime.InteropServices.DllImport("winmm.dll")]
	private static extern uint timeEndPeriod(uint period);
}
