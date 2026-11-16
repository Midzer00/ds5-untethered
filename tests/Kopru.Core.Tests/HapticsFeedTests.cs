// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;
using Xunit;

namespace Kopru.Core.Tests;

// A clock the test moves by hand (Stopwatch units).
internal sealed class SimulatedClock : TimeProvider
{
	private long now = Stopwatch.Frequency * 1000;

	public override long TimestampFrequency => Stopwatch.Frequency;

	public override long GetTimestamp() => Interlocked.Read(ref now);

	public void Set(long timestamp) => Interlocked.Exchange(ref now, timestamp);
}

// Simulated time for the haptics stream: every wait moves the clock to its deadline (nothing really waits) and runs
// OnTick, which plays the game (sends an audio packet, flips a setting) at that moment of simulated time.
internal sealed class SimulatedWait(SimulatedClock time, long start) : IPreciseWait
{
	private long offset = long.MinValue;

	public Action<long>? OnTick;

	public bool HighResolution => true;

	public bool WaitUntil(long timestamp, bool spinFinal, WaitHandle? wake = null)
	{
		// The stream's deadlines are on the real Stopwatch; the simulated clock follows them from `start`.
		if (offset == long.MinValue)
		{
			offset = start - timestamp;
		}
		long simulated = timestamp + offset;
		time.Set(simulated);
		OnTick?.Invoke(simulated - start);
		return true;
	}

	public void Idle()
	{
	}

	public void Dispose()
	{
	}
}

// The default mode: while the game keeps sending audio packets to the virtual pad (silent or not) the haptics stream keeps
// running as it always did; it stops only after the tail once the packets stop. PauseHapticsInSilence (opt-in, Low Power)
// switches to stopping on silence, and can be switched while the stream runs.
public class HapticsFeedTests
{
	private static readonly IsoPacket[] Packets = Enumerable.Range(0, 10).Select(i => new IsoPacket(i * 384, 384, 0, 0)).ToArray();

	private static readonly byte[] Silence = HapticsConverterTests.Audio(480);

	private static readonly byte[] Tone = HapticsConverterTests.Audio(480, ch3: HapticsConverterTests.Tone(100, 0.5), ch4: HapticsConverterTests.Tone(100, 0.5));

	private static void WaitUntil(Func<bool> condition, int ms = 5000) => Assert.True(SpinWait.SpinUntil(condition, ms), "timed out");

	private static VirtualDualSense Pad() => new(DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
		[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
		new Dictionary<int, string> { [1] = "Sony Interactive Entertainment", [2] = "DualSense Wireless Controller" },
		new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] }));

	private static SubmitCommand Audio(uint seq, byte[] data) =>
		new(seq, 0, UsbIpCodes.DirOut, VirtualDualSense.AudioOutEndpoint, 0, data.Length, 0, Packets.Length, 1, new byte[8], data, Packets);

	private static readonly long Interval = (long)(BluetoothReports.HapticsInterval.TotalSeconds * Stopwatch.Frequency);

	[Fact]
	public void SilentGamePackets_KeepOneStreamRunning_For10Seconds_ThenItStopsAfterTheTail()
	{
		FakeLink link = new();
		SimulatedClock time = new();
		SimulatedWait wait = new(time, time.GetTimestamp());
		using BluetoothSession session = new(link) { Time = time, PreciseWaitFactory = () => wait };
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		long tenSeconds = Stopwatch.Frequency * 10;
		long lastPacket = 0, packets = 0;
		bool stoppedWhileFed = false;
		uint seq = 1;
		// One silent 10 ms packet per report (the game's audio clock and the stream's run side by side) for 10 s.
		wait.OnTick = elapsed =>
		{
			stoppedWhileFed |= !session.IsHapticsStreaming;
			if (elapsed <= tenSeconds)
			{
				pad.Submit(Audio(seq++, Silence), _ => { });
				lastPacket = elapsed;
				packets++;
			}
		};

		pad.Submit(Audio(0, Silence), _ => { });
		WaitUntil(() => session.HapticsStreams == 1 && !session.IsHapticsStreaming, 15000);

		Assert.False(stoppedWhileFed, "the stream stopped while the game was still sending");
		Assert.Equal(1, session.HapticsStreams);
		Assert.True(packets >= tenSeconds / Interval, $"{packets} packets");
		// After the last packet: the feed window (~100 ms), then the ~1 s tail.
		long after = session.HapticsReports - packets;
		long window = (long)(BluetoothSession.FeedWindow.TotalSeconds * Stopwatch.Frequency) / Interval;
		Assert.InRange(after, BluetoothSession.SilentReportsBeforeStop, BluetoothSession.SilentReportsBeforeStop + window + 3);
		Assert.True(lastPacket >= tenSeconds - Interval);
	}

	[Fact]
	public void SilentGamePacket_StartsTheStream_WithNoIdleCushionTrimming()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);

		pad.Submit(Audio(0, Silence), _ => { });

		Assert.True(session.IsHapticsStreaming);
		WaitUntil(() => session.HapticsReports > 3);
		Assert.Equal(1, session.HapticsStreams);
		Assert.Equal(0, session.Haptics.Audible);
	}

	[Fact]
	public void LiveSwitch_PauseOn_StopsWhileTheGameStillSendsSilence_PauseOff_StartsItAgain_OneThread()
	{
		FakeLink link = new();
		SimulatedClock time = new();
		SimulatedWait wait = new(time, time.GetTimestamp());
		using BluetoothSession session = new(link) { Time = time, PreciseWaitFactory = () => wait };
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		long second = Stopwatch.Frequency;
		uint seq = 1;
		ConcurrentQueue<string> events = new();
		bool pausedOnce = false;
		wait.OnTick = elapsed =>
		{
			pad.Submit(Audio(seq++, Silence), _ => { });
			if (!pausedOnce && elapsed >= second)
			{
				// One second in: the user turns the pause on while the game keeps sending silence.
				pausedOnce = true;
				session.PauseHapticsInSilence = true;
				events.Enqueue("on");
			}
		};

		pad.Submit(Audio(0, Silence), _ => { });
		WaitUntil(() => pausedOnce && !session.IsHapticsStreaming);
		long reportsAtStop = session.HapticsReports;
		Assert.Equal(1, session.HapticsStreams);
		// Stopped about 1 s after the switch (the feed window, then the tail): nothing stuck running.
		Assert.InRange(reportsAtStop, second / Interval + BluetoothSession.SilentReportsBeforeStop - 2, second / Interval + BluetoothSession.SilentReportsBeforeStop + 15);

		// Paused: the game's silent packets (now sent from here, the stream being stopped) do not start it again.
		for (int i = 0; i < 20; i++)
		{
			pad.Submit(Audio(seq++, Silence), _ => { });
		}
		Assert.False(session.IsHapticsStreaming);
		Assert.Equal(1, session.HapticsStreams);

		// Pause off while the game still sends: the stream starts at once, once (no doubled stream).
		wait.OnTick = null;
		pad.Submit(Audio(seq++, Silence), _ => { });
		session.PauseHapticsInSilence = false;
		WaitUntil(() => session.HapticsStreams == 2);
		session.PauseHapticsInSilence = false;
		WaitUntil(() => !session.IsHapticsStreaming);
		Assert.Equal(2, session.HapticsStreams);
		Assert.Equal(1, session.HapticsThreadsStarted);
	}

	[Fact]
	public void DefaultMode_SoundAfterSilence_PlaysWithoutAFreshStart()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { GameHapticsLevel = 1.0 };
		uint seq = 0;

		Stopwatch clock = Stopwatch.StartNew();
		while (clock.ElapsedMilliseconds < 400)
		{
			pad.Submit(Audio(seq++, Silence), _ => { });
			Thread.Sleep(10);
		}
		pad.Submit(Audio(seq++, Tone), _ => { });
		WaitUntil(() => link.Writes.Any(w => w[0] == BluetoothReports.HapticsId && w.AsSpan(13, 64).ToArray().Any(b => b != 0)));

		Assert.Equal(1, session.HapticsStreams);
	}
}

public class PauseHapticsSettingTests
{
	[Fact]
	public void Setting_IsOffByDefault_LowPowerTurnsItOn_WithItsReason()
	{
		ControllerSettings plain = new();
		Assert.False(plain.PauseHapticsInSilence);
		Assert.Null(plain.PauseHapticsReason);
		Assert.Null(LowPower.Apply(plain, false).PauseHapticsReason);
		Assert.Equal("low power", LowPower.Apply(plain, true).PauseHapticsReason);
		Assert.Equal("setting", (plain with { PauseHapticsInSilence = true }).PauseHapticsReason);
		Assert.Equal("setting", LowPower.Apply(plain with { PauseHapticsInSilence = true }, true).PauseHapticsReason);
		Assert.False(new BluetoothSession(new FakeLink()).PauseHapticsInSilence);
	}

	[Fact]
	public void Host_LogsEachChange_OnceWithItsReason()
	{
		ConcurrentQueue<string> lines = new();
		void Watch(string line)
		{
			if (line.StartsWith("haptics: pause in silence", StringComparison.Ordinal))
			{
				lines.Enqueue(line);
			}
		}
		Diagnostics.AppLog.Written += Watch;
		try
		{
			BridgeHost host = new(new QueueControllers(), new FakeIdentity(null), new FakeAttacher(new ConcurrentQueue<string>()));
			host.SetPauseHapticsInSilence(null);
			host.SetPauseHapticsInSilence("setting");
			host.SetPauseHapticsInSilence("setting");
			Assert.True(host.PauseHapticsInSilence);
			host.SetPauseHapticsInSilence(null);
			host.SetPauseHapticsInSilence("low power");
			host.SetPauseHapticsInSilence(null);
			Assert.False(host.PauseHapticsInSilence);
		}
		finally
		{
			Diagnostics.AppLog.Written -= Watch;
		}

		// Other tests may log too: ours appear in order.
		string[] mine = [.. lines];
		int at = Array.IndexOf(mine, "haptics: pause in silence on (setting)");
		Assert.True(at >= 0, string.Join(" | ", mine));
		Assert.Equal(["haptics: pause in silence on (setting)", "haptics: pause in silence off (setting)",
			"haptics: pause in silence on (low power)", "haptics: pause in silence off (low power)"], mine.Skip(at).Take(4));
	}
}
