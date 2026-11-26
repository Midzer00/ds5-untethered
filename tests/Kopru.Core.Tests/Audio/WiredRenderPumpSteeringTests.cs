// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Kopru.Core.Audio;
using Xunit;

namespace Kopru.Core.Tests;

// Fix 8 (crackle while vibrating in wired mode): the pump against a simulated shared-mode engine that reads a whole
// 10 ms period (480 frames) at a time while the haptics thread writes a 512-frame block every 10.67 ms. What the
// "device" plays is recorded, so a step in the haptics waveform shows up as a large sample-to-sample jump.
public sealed class WiredRenderPumpSteeringTests
{
	private const double BlockSeconds = 512 / 48000.0;

	private sealed class EngineStream : IRenderStream, IDisposable
	{
		private readonly IntPtr buffer = Marshal.AllocHGlobal(1 << 16);
		private readonly Queue<float[]> queue = new();
		private double startedAt;
		private long ticksDone;
		public double Now;
		public bool Running;
		public readonly List<float[]> Played = [];

		private void Advance()
		{
			if (!Running)
			{
				return;
			}
			// The engine reads one period at Start and one more every 10 ms; a short queue is padded with silence.
			long due = (long)Math.Floor((Now - startedAt) / 0.010 + 1e-9) + 1;
			for (; ticksDone < due; ticksDone++)
			{
				for (int i = 0; i < 480; i++)
				{
					Played.Add(queue.Count > 0 ? queue.Dequeue() : new float[4]);
				}
			}
		}

		public int GetCurrentPadding(out uint frames)
		{
			Advance();
			frames = (uint)queue.Count;
			return 0;
		}

		public int GetBuffer(uint frames, out IntPtr data)
		{
			data = buffer;
			return 0;
		}

		public int ReleaseBuffer(uint frames)
		{
			float[] all = new float[frames * 4];
			Marshal.Copy(buffer, all, 0, all.Length);
			for (int f = 0; f < frames; f++)
			{
				queue.Enqueue(all[(f * 4)..(f * 4 + 4)]);
			}
			return 0;
		}

		public int Start()
		{
			Running = true;
			startedAt = Now;
			ticksDone = 0;
			Advance();
			return 0;
		}

		public int Stop()
		{
			Running = false;
			return 0;
		}

		public int Reset()
		{
			queue.Clear();
			return 0;
		}

		public double? PlayedSeconds() => Played.Count / 48000.0;

		public void Dispose() => Marshal.FreeHGlobal(buffer);
	}

	// The test rumble's light motor: 160 Hz at 101/127 (peak 0.795, as in the owner's log), 32 samples per block.
	private static sbyte[] Tone(int block) =>
		[.. Enumerable.Range(0, 32).Select(i => (sbyte)Math.Round(101 * Math.Sin(2 * Math.PI * 160 * (block * 32 + i) / 3000.0)))];

	private static (WiredRenderPump Pump, EngineStream Stream, List<string> Lines) Make(WiredAudioTuning tuning)
	{
		EngineStream stream = new();
		List<string> lines = [];
		WiredRenderPump pump = new(stream, 4, WiredSampleFormat.Float32, () => "shared mode", lines.Add, tuning, () => stream.Now);
		return (pump, stream, lines);
	}

	// Largest jump between two played haptics samples (channel 3) after the start.
	private static float WorstStep(List<float[]> played, int from)
	{
		float worst = 0;
		for (int i = Math.Max(1, from); i < played.Count; i++)
		{
			worst = Math.Max(worst, Math.Abs(played[i][2] - played[i - 1][2]));
		}
		return worst;
	}

	private static void Run(WiredRenderPump pump, EngineStream stream, int blocks, Func<int, double>? extraDelay = null)
	{
		double at = 0;
		for (int b = 0; b < blocks; b++)
		{
			at += BlockSeconds + (extraDelay?.Invoke(b) ?? 0);
			stream.Now = at;
			sbyte[] tone = Tone(b);
			pump.WriteHaptics(tone, tone);
		}
	}

	[Fact]
	public void AgainstA10MsEngine_MostBlocksAreNudged_AndLegacySteeringPutsStepsIntoTheHaptics()
	{
		(WiredRenderPump pump, EngineStream stream, _) = Make(new WiredAudioTuning { Steering = WiredSteering.Legacy });
		using (stream)
		{
			Run(pump, stream, 300);
			// The queue read jumps by 480 frames, so far more blocks fall outside the 96-frame deadband than inside.
			Assert.True(pump.Nudged > 100, $"nudged {pump.Nudged} of 300");
			Assert.Equal(0, pump.Gaps);
			// The natural step of this tone is ~0.017 per frame; dropped frames make it several times larger.
			Assert.True(WorstStep(stream.Played, 2000) > 0.04f, $"worst step {WorstStep(stream.Played, 2000)}");
		}
	}

	[Fact]
	public void SmoothSteering_KeepsTheQueueNearTheTarget_WithoutStepsInTheHaptics()
	{
		(WiredRenderPump pump, EngineStream stream, _) = Make(WiredAudioTuning.Default);
		using (stream)
		{
			Run(pump, stream, 300);
			Assert.True(pump.Nudged > 100);
			Assert.Equal(0, pump.Gaps);
			Assert.Equal(0, pump.DroppedBlocks);
			Assert.True(WorstStep(stream.Played, 2000) < 0.02f, $"worst step {WorstStep(stream.Played, 2000)}");
			stream.GetCurrentPadding(out uint queued);
			Assert.InRange(queued, 300u, 2000u);
		}
	}

	// A constant level (a held rumble) so a jump after the gap is easy to see.
	private static void RunLevelWithOneLateBlock(WiredRenderPump pump, EngineStream stream)
	{
		sbyte[] level = [.. Enumerable.Repeat((sbyte)100, 32)];
		double at = 0;
		for (int b = 0; b < 120; b++)
		{
			at += BlockSeconds + (b == 60 ? 0.045 : 0);
			stream.Now = at;
			pump.WriteHaptics(level, level);
		}
	}

	// The first played haptics value after the longest run of silence that follows sound.
	private static float FirstAfterGap(List<float[]> played)
	{
		int i = played.FindIndex(2000, f => f[2] == 0);
		Assert.True(i > 0, "no gap played");
		while (played[i][2] == 0)
		{
			i++;
		}
		return played[i][2];
	}

	[Fact]
	public void AGap_IsCounted_AndTheHapticsFadeBackIn_InsteadOfJumpingBack()
	{
		(WiredRenderPump pump, EngineStream stream, List<string> lines) = Make(WiredAudioTuning.Default);
		using (stream)
		{
			RunLevelWithOneLateBlock(pump, stream);
			Assert.Equal(1, pump.Gaps);
			// 2 ms fade: the first sample back is 1/96 of the level, not the level itself.
			Assert.InRange(FirstAfterGap(stream.Played), 0f, 0.02f);
			pump.Idle();
			string line = Assert.Single(lines);
			Assert.Contains("1 gaps,", line, StringComparison.Ordinal);
			Assert.Contains("1 late (worst 55.7 ms)", line, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void WithoutTheGapFade_TheHapticsJumpBackAfterAGap_AsBeforeFix8()
	{
		(WiredRenderPump pump, EngineStream stream, _) = Make(new WiredAudioTuning { GapFadeFrames = 0 });
		using (stream)
		{
			RunLevelWithOneLateBlock(pump, stream);
			Assert.Equal(1, pump.Gaps);
			Assert.InRange(FirstAfterGap(stream.Played), 0.75f, 0.8f);
		}
	}

	[Fact]
	public void ALargerBuffer_QueuesALargerCushion_AndSteersToIt()
	{
		WiredAudioTuning tuning = WiredAudioTuning.Parse(name => name == WiredAudioTuning.BufferVariable ? "40" : null);
		(WiredRenderPump pump, EngineStream stream, _) = Make(tuning);
		using (stream)
		{
			// A 30 ms late block: a gap with the 20 ms target, none with 40 ms.
			Run(pump, stream, 200, b => b == 100 ? 0.030 : 0);
			Assert.Equal(0, pump.Gaps);
			// The late block took ~1440 frames; steering wins them back at 4 frames a block at most.
			stream.GetCurrentPadding(out uint queued);
			Assert.InRange(queued, 900u, 3000u);
		}
		(WiredRenderPump small, EngineStream smallStream, _) = Make(WiredAudioTuning.Default);
		using (smallStream)
		{
			Run(small, smallStream, 200, b => b == 100 ? 0.030 : 0);
			Assert.Equal(1, small.Gaps);
		}
	}

	[Fact]
	public void TheEventLog_WritesOneLinePerSecond_WithTheCounts_OnlyWhenOn()
	{
		(WiredRenderPump quiet, EngineStream quietStream, List<string> quietLines) = Make(WiredAudioTuning.Default);
		using (quietStream)
		{
			Run(quiet, quietStream, 300);
			Assert.DoesNotContain(quietLines, l => l.Contains("events", StringComparison.Ordinal));
		}
		(WiredRenderPump pump, EngineStream stream, List<string> lines) = Make(new WiredAudioTuning { EventLog = true });
		using (stream)
		{
			Run(pump, stream, 300, b => b == 150 ? 0.045 : 0);
			pump.Idle();
			List<string> events = [.. lines.Where(l => l.StartsWith("wired: usb audio events", StringComparison.Ordinal))];
			// 300 blocks are 3.2 s (+45 ms): three full seconds and the rest at idle.
			Assert.Equal(4, events.Count);
			Assert.Contains(events, l => l.Contains(" 1 gaps,", StringComparison.Ordinal) && l.Contains("1 late (worst 55.7 ms between blocks)", StringComparison.Ordinal));
			Assert.All(events, l => Assert.Matches(@"^wired: usb audio events \d\.\d s: \d+ blocks, \d+ gaps, \d+ low, \d+ nudged \(\+\d+/-\d+ frames\), \d+ dropped, \d+ late \(worst \d+\.\d ms between blocks\), queue (\d+\.\.\d+|-) frames$", l));
		}
	}
}
