// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Kopru.Core.Audio;
using Xunit;

namespace Kopru.Core.Tests;

// The wired render stream's feeding (WiredRenderPump) against a fake audio client: Start once after the first
// buffer, the device clock read while the stream runs (a Reset zeroes it), and the stall check that gives up on a
// stream whose device clock never moves (found on hardware: a log said "device clock 0.00 s").
public sealed class WiredRenderPumpTests
{
	private sealed class FakeStream : IRenderStream, IDisposable
	{
		private readonly IntPtr buffer = Marshal.AllocHGlobal(1 << 16);
		public readonly List<string> Calls = [];
		public bool Consumes = true;
		public bool ClockMoves = true;
		public bool HasClock = true;
		public uint Queued;
		public bool Running;
		public double Played;
		public float[] LastBlock = [];

		public int GetCurrentPadding(out uint frames)
		{
			if (Running && Consumes)
			{
				// One haptics block's worth (512 frames, 10.67 ms) played per tick.
				uint played = Math.Min(Queued, 512);
				Queued -= played;
				if (ClockMoves)
				{
					Played += played / 48000.0;
				}
			}
			frames = Queued;
			return 0;
		}

		public int GetBuffer(uint frames, out IntPtr data)
		{
			Calls.Add("GetBuffer");
			data = buffer;
			return 0;
		}

		public int ReleaseBuffer(uint frames)
		{
			Calls.Add("ReleaseBuffer " + frames);
			Queued += frames;
			LastBlock = new float[frames * 4];
			Marshal.Copy(buffer, LastBlock, 0, LastBlock.Length);
			return 0;
		}

		public int Start()
		{
			Calls.Add("Start");
			Running = true;
			return 0;
		}

		public int Stop()
		{
			Calls.Add("Stop");
			Running = false;
			return 0;
		}

		public int Reset()
		{
			Calls.Add("Reset");
			Queued = 0;
			Played = 0;
			return 0;
		}

		public double? PlayedSeconds()
		{
			Calls.Add("Clock");
			return HasClock ? Played : null;
		}

		public void Dispose() => Marshal.FreeHGlobal(buffer);
	}

	// The calls without the clock reads; a haptics block (512 frames, nudged by a few for steering) shows as "block".
	private static List<string> Order(FakeStream stream) =>
		[.. stream.Calls.Where(c => c != "Clock").Select(c => c.StartsWith("ReleaseBuffer 5", StringComparison.Ordinal) ? "ReleaseBuffer block" : c)];

	private static readonly sbyte[] Tone = [.. Enumerable.Range(0, 32).Select(i => (sbyte)(i % 2 == 0 ? 70 : -70))];

	private static (WiredRenderPump Pump, List<string> Lines) Pump(FakeStream stream)
	{
		List<string> lines = [];
		return (new WiredRenderPump(stream, 4, WiredSampleFormat.Float32, () => "shared mode", lines.Add), lines);
	}

	[Fact]
	public void Start_IsCalledOnce_AfterTheCushionAndTheFirstBlockAreQueued()
	{
		using FakeStream stream = new();
		(WiredRenderPump pump, _) = Pump(stream);

		pump.WriteHaptics(Tone, Tone);
		Assert.Equal(["GetBuffer", "ReleaseBuffer 960", "GetBuffer", "ReleaseBuffer block", "Start"], Order(stream));
		// The block reached the buffer: channels 3-4 carry the haptics ramp, 1-2 stay silent.
		Assert.Contains(stream.LastBlock.Where((_, i) => i % 4 == 2), v => Math.Abs(v) > 0.5f);
		Assert.All(stream.LastBlock.Where((_, i) => i % 4 < 2), v => Assert.Equal(0f, v));

		for (int i = 0; i < 50; i++)
		{
			pump.WriteHaptics(Tone, Tone);
		}
		Assert.Single(stream.Calls, c => c == "Start");
		// Playing: the device clock moved and the queue stays near the steering target, nothing dropped.
		Assert.True(stream.Played > 0.4);
		Assert.Equal(0, pump.DroppedBlocks);
	}

	[Fact]
	public void Idle_ReadsTheClockBeforeTheReset_AndTheNextRunStartsAgainBehindACushion()
	{
		using FakeStream stream = new();
		(WiredRenderPump pump, List<string> lines) = Pump(stream);
		for (int i = 0; i < 100; i++)
		{
			pump.WriteHaptics(Tone, Tone);
		}

		stream.Calls.Clear();
		pump.Idle();
		Assert.Equal(["Clock", "Stop", "Reset"], stream.Calls);
		string line = Assert.Single(lines);
		Assert.Contains("device played 1.0", line, StringComparison.Ordinal);
		Assert.EndsWith("(shared mode)", line, StringComparison.Ordinal);

		stream.Calls.Clear();
		pump.WriteHaptics(Tone, Tone);
		Assert.Equal(["GetBuffer", "ReleaseBuffer 960", "GetBuffer", "ReleaseBuffer block", "Start"], Order(stream));
	}

	[Fact]
	public void AStreamWhoseClockNeverMoves_IsGivenUpAfterASecond()
	{
		using FakeStream stream = new() { ClockMoves = false };
		(WiredRenderPump pump, _) = Pump(stream);
		int blocks = 0;
		WiredAudioStalledException e = Assert.Throws<WiredAudioStalledException>(() =>
		{
			for (; blocks < 200; blocks++)
			{
				pump.WriteHaptics(Tone, Tone);
			}
		});
		// About one second of blocks (48000 / 512 ~ 94), not before.
		Assert.InRange(blocks, 90, 96);
		Assert.StartsWith("device clock still 0 after 1.0 s written", e.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void AStreamThatConsumesNothing_IsGivenUpToo_ThoughItsBlocksAreDropped()
	{
		using FakeStream stream = new() { Consumes = false };
		(WiredRenderPump pump, _) = Pump(stream);
		int blocks = 0;
		Assert.Throws<WiredAudioStalledException>(() =>
		{
			for (; blocks < 200; blocks++)
			{
				pump.WriteHaptics(Tone, Tone);
			}
		});
		Assert.InRange(blocks, 90, 96);
		Assert.True(pump.DroppedBlocks > 80);
	}

	[Fact]
	public void WithoutADeviceClock_OrWithAMovingOne_TheStallCheckNeverFires()
	{
		using FakeStream noClock = new() { HasClock = false, ClockMoves = false };
		(WiredRenderPump a, _) = Pump(noClock);
		using FakeStream moving = new();
		(WiredRenderPump b, _) = Pump(moving);
		for (int i = 0; i < 300; i++)
		{
			a.WriteHaptics(Tone, Tone);
			b.WriteHaptics(Tone, Tone);
		}
		Assert.Single(noClock.Calls, c => c == "Start");
		Assert.Single(moving.Calls, c => c == "Start");
	}

	[Fact]
	public void TheStallCheck_CountsEachRunFromItsOwnStart()
	{
		using FakeStream stream = new();
		(WiredRenderPump pump, _) = Pump(stream);
		for (int i = 0; i < 120; i++)
		{
			pump.WriteHaptics(Tone, Tone);
		}
		pump.Idle();
		// The next run's device stops moving: it is caught a second into that run, not at once.
		stream.ClockMoves = false;
		int blocks = 0;
		Assert.Throws<WiredAudioStalledException>(() =>
		{
			for (; blocks < 200; blocks++)
			{
				pump.WriteHaptics(Tone, Tone);
			}
		});
		Assert.InRange(blocks, 90, 96);
	}
}
