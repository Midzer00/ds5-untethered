// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;

namespace Kopru.Core.Tests;

// The microphone's jitter buffer and loss concealment against synthetic Bluetooth timing: bursty arrival, stalls, lost
// reports and the controller's clock running fast or slow against the PC's.
public class MicJitterTests
{
	private const int Packet = MicPackets.FrameSamples;

	// A 48 kHz sine sample (amplitude 0.4) at a running sample index.
	private static short Sine(long i, double hz = 200) => (short)Math.Round(0.4 * short.MaxValue * Math.Sin(2 * Math.PI * hz * i / 48000));

	private sealed record Run(long Underruns, long UnderrunsLate, int Target, (int Min, int Max)? LateLevels, long Dropped, long Skipped, double Trim, int Buffered);

	// Plays `seconds` of 10 ms packets into the buffer while the host reads 1 ms (48 samples) per ms of its own clock.
	// drift: the controller's clock against the PC's (+0.003: 0.3 % fast). delay(k): how late packet k arrives (ms);
	// packets stay in order (one held back holds back the ones after it, as on a Bluetooth link). "Late": the last third.
	private static Run Simulate(int seconds, double drift, Func<int, Random, double> delay, MicJitterBuffer? buffer = null, int seed = 1)
	{
		buffer ??= new MicJitterBuffer();
		Random random = new(seed);
		short[] packet = new short[Packet];
		short[] read = new short[48];
		long written = 0;
		int next = 0;
		double nextArrival = delay(0, random);
		double lastArrival = 0;
		long lateFrom = seconds * 1000L * 2 / 3;
		long underrunsBefore = 0;
		for (long ms = 0; ms < seconds * 1000L; ms++)
		{
			while (nextArrival <= ms)
			{
				for (int i = 0; i < Packet; i++)
				{
					packet[i] = Sine(written++);
				}
				buffer.Write(packet);
				lastArrival = nextArrival;
				next++;
				nextArrival = Math.Max(lastArrival, next * 10 / (1 + drift) + delay(next, random));
			}
			if (ms == lateFrom)
			{
				underrunsBefore = buffer.Underruns;
				buffer.TakeLevels();
			}
			buffer.Read(read);
		}
		return new Run(buffer.Underruns, buffer.Underruns - underrunsBefore, buffer.Target, buffer.TakeLevels(), buffer.Dropped,
			buffer.Skipped, buffer.Trim, buffer.Buffered);
	}

	[Fact]
	public void SteadyPackets_NeverUnderrun_AndKeepTheMinimumDepth()
	{
		Run run = Simulate(30, 0, (_, _) => 0);
		Assert.Equal(0, run.Underruns);
		Assert.Equal(MicJitterBuffer.DefaultMinTarget, run.Target);
		Assert.Equal(0, run.Dropped);
		// Latency stays near the target: 40 ms plus at most one packet.
		Assert.InRange(run.LateLevels!.Value.Max, 0, MicJitterBuffer.DefaultMinTarget + Packet);
	}

	[Fact]
	public void BurstyArrival_WithinTheDepth_NeverUnderruns()
	{
		// Every packet 0-30 ms late (Bluetooth bursts): the 40 ms depth covers it.
		Run run = Simulate(30, 0, (_, r) => r.NextDouble() * 30);
		Assert.Equal(0, run.Underruns);
		Assert.Equal(MicJitterBuffer.DefaultMinTarget, run.Target);
	}

	[Fact]
	public void RepeatedStalls_GrowTheDepth_UntilTheyNoLongerUnderrun()
	{
		// 2 % of packets held back 70 ms (a radio retry burst): the first ones underrun, then the depth covers them.
		Run run = Simulate(90, 0, (k, r) => r.NextDouble() < 0.02 ? 70 : r.NextDouble() * 15);
		Assert.InRange(run.Underruns, 1, 6);
		Assert.Equal(0, run.UnderrunsLate);
		Assert.InRange(run.Target, MicJitterBuffer.DefaultMinTarget + MicJitterBuffer.TargetStep, MicJitterBuffer.DefaultMaxTarget);
	}

	[Fact]
	public void TheDepthComesBackDown_AfterAQuietStretch()
	{
		MicJitterBuffer buffer = new();
		// One long stall early on (packet 50 held back 120 ms), then a clean link for long enough to shrink back.
		Run run = Simulate(150, 0, (k, _) => k == 50 ? 120 : 0, buffer);
		Assert.Equal(1, run.Underruns);
		Assert.Equal(MicJitterBuffer.DefaultMinTarget, run.Target);
	}

	[Theory]
	[InlineData(0.001)]
	[InlineData(-0.001)]
	[InlineData(0.0001)]
	[InlineData(-0.0001)]
	public void ClockDrift_IsTrimmed_WithoutUnderrunsOrDrops(double drift)
	{
		// 0.1 % is ten times what real crystals drift; without the trim it would gain or lose 60 ms a minute.
		Run run = Simulate(120, drift, (_, r) => r.NextDouble() * 10);
		Assert.Equal(0, run.Underruns);
		Assert.Equal(0, run.Dropped);
		Assert.Equal(MicJitterBuffer.DefaultMinTarget, run.Target);
		(int min, int max) = run.LateLevels!.Value;
		int reach = MicJitterBuffer.TrimDeadband + MicJitterBuffer.TrimFullScale;
		Assert.InRange(min, run.Target - reach - 2 * Packet, run.Target + reach);
		Assert.InRange(max, run.Target - reach, run.Target + reach + 2 * Packet);
		Assert.InRange(Math.Abs(run.Trim), 0, MicJitterBuffer.MaxTrim);
		if (Math.Abs(drift) >= 0.001)
		{
			Assert.Equal(Math.Sign(drift), Math.Sign(run.Trim));
		}
	}

	[Fact]
	public void FarAboveTheTarget_TheExcessIsSkipped_AtOnce()
	{
		MicJitterBuffer buffer = new();
		short[] read = new short[48];
		buffer.Write(new short[MicJitterBuffer.DefaultMinTarget]);
		buffer.Read(read);
		// A backlog arrives at once (a stalled reader caught up): 200 ms more.
		buffer.Write(new short[48 * 200]);
		buffer.Read(read);
		Assert.True(buffer.Skipped > 0);
		Assert.InRange(buffer.Buffered, MicJitterBuffer.DefaultMinTarget - 96, MicJitterBuffer.DefaultMinTarget);
	}

	[Fact]
	public void ACatchUpSkip_CrossfadesIntoTheKeptAudio()
	{
		MicJitterBuffer buffer = new();
		short[] read = new short[48];
		buffer.Write(Enumerable.Repeat((short)1000, MicJitterBuffer.DefaultMinTarget).ToArray());
		buffer.Read(read);
		buffer.Read(read);
		// A backlog: what would be skipped is 1000, the kept newest part 9000.
		int backlog = 48 * 200;
		buffer.Write(Enumerable.Repeat((short)1000, backlog - MicJitterBuffer.DefaultMinTarget).ToArray());
		buffer.Write(Enumerable.Repeat((short)9000, MicJitterBuffer.DefaultMinTarget).ToArray());
		short[] after = new short[MicJitterBuffer.CrossfadeSamples + 48];
		buffer.Read(after);
		Assert.True(buffer.Skipped > 0);
		Assert.InRange(after[0], 1000, 1100);
		Assert.InRange(after[MicJitterBuffer.CrossfadeSamples / 2], 4900, 5100);
		for (int i = 1; i < MicJitterBuffer.CrossfadeSamples; i++)
		{
			Assert.True(after[i] >= after[i - 1]);
		}
		Assert.Equal(9000, after[MicJitterBuffer.CrossfadeSamples + 10]);
	}

	[Fact]
	public void MicOffAndOn_IsNoUnderrun_NorLoss()
	{
		PlcDecoder decoder = new();
		using MicPipeline pipeline = new(decoder, new MicJitterBuffer(minTarget: Packet));
		byte[] pcm = new byte[48 * 2 * 2];
		pipeline.PushReport(MicPackets.Build([0xD4], counter: 10));
		pipeline.PushReport(MicPackets.Build([0xD4], counter: 11));
		pipeline.Fill(pcm, 2);

		// Off (no reads, no reports), then on again with the controller's counter somewhere else.
		pipeline.Restart();
		Assert.Equal(0, pipeline.Buffer.Buffered);
		pipeline.Fill(pcm, 2);
		pipeline.PushReport(MicPackets.Build([0xD4], counter: 14));
		pipeline.Fill(pcm, 2);
		Assert.Equal(0, pipeline.Buffer.Underruns);
		Assert.Equal(0, pipeline.LostPackets);
		Assert.Equal(0, decoder.Concealed);
		pipeline.PushReport(MicPackets.Build([0xD4], counter: 16));
		Assert.Equal(1, pipeline.LostPackets);
	}

	[Fact]
	public void AnUnderrun_ContinuesThePitch_FadesOut_ThenSilence()
	{
		MicJitterBuffer buffer = new(minTarget: 48 * 40);
		short[] sound = Enumerable.Range(0, 48 * 40).Select(i => Sine(i)).ToArray();
		buffer.Write(sound);
		short[] heard = new short[48 * 40];
		Assert.Equal(heard.Length, buffer.Read(heard));

		short[] after = new short[MicJitterBuffer.ConcealSamples + 48];
		Assert.Equal(0, buffer.Read(after));
		Assert.Equal(1, buffer.Underruns);
		Assert.Equal(MicJitterBuffer.ConcealSamples, buffer.Concealed);
		for (int i = 0; i < MicJitterBuffer.ConcealSamples; i++)
		{
			// The 200 Hz tone goes on where it stopped, fading linearly.
			double expected = Sine(sound.Length + i) * (MicJitterBuffer.ConcealSamples - i) / (double)MicJitterBuffer.ConcealSamples;
			Assert.InRange(after[i], expected - 200, expected + 200);
		}
		Assert.All(after[MicJitterBuffer.ConcealSamples..], s => Assert.Equal(0, s));
		short[] more = new short[48];
		buffer.Read(more);
		Assert.All(more, s => Assert.Equal(0, s));
	}

	[Fact]
	public void AnUnderrunInSilence_ConcealsNothing()
	{
		MicJitterBuffer buffer = new(minTarget: 480);
		buffer.Write(new short[480]);
		short[] read = new short[960];
		Assert.Equal(480, buffer.Read(read));
		Assert.Equal(1, buffer.Underruns);
		Assert.Equal(0, buffer.Concealed);
		Assert.All(read, s => Assert.Equal(0, s));
	}

	[Fact]
	public void PlaybackStarts_WithAFadeIn()
	{
		MicJitterBuffer buffer = new(minTarget: 480);
		buffer.Write(Enumerable.Repeat((short)10000, 480).ToArray());
		short[] read = new short[MicJitterBuffer.FadeInSamples + 10];
		buffer.Read(read);
		Assert.Equal(0, read[0]);
		Assert.InRange(read[MicJitterBuffer.FadeInSamples / 2], 4900, 5100);
		Assert.Equal(10000, read[MicJitterBuffer.FadeInSamples]);
		for (int i = 1; i < MicJitterBuffer.FadeInSamples; i++)
		{
			Assert.True(read[i] >= read[i - 1]);
		}
	}

	private sealed class PlcDecoder : IMicDecoder
	{
		public int Concealed;

		public int Decode(ReadOnlySpan<byte> packet, Span<short> pcm)
		{
			pcm[..Packet].Fill(1000);
			return Packet;
		}

		public int Conceal(Span<short> pcm)
		{
			Concealed++;
			pcm[..Packet].Fill(500);
			return Packet;
		}

		public void Dispose()
		{
		}
	}

	[Fact]
	public void LostReports_AreConcealedByTheDecoder_FromTheCounter()
	{
		PlcDecoder decoder = new();
		using MicPipeline pipeline = new(decoder, new MicJitterBuffer(minTarget: 0, capacity: 48 * 1000));
		void Push(byte counter) => pipeline.PushReport(MicPackets.Build([0xD4], counter: counter));

		Push(250);
		Push(251);
		Push(253); // one lost
		Assert.Equal((1, 1L, 1L), (decoder.Concealed, pipeline.LostPackets, pipeline.ConcealedPackets));
		Assert.Equal(4 * Packet, pipeline.Buffer.Buffered);

		Push(255);
		Push(1); // 255 -> 0 lost across the wrap
		Assert.Equal((3, 3L), (decoder.Concealed, pipeline.LostPackets));

		Push(1); // a repeat: nothing lost
		Push(9); // 7 lost: longer than MaxConcealedPackets, a restart, not filled
		Assert.Equal((3, 10L, 3L), (decoder.Concealed, pipeline.LostPackets, pipeline.ConcealedPackets));
		Assert.Equal(7, pipeline.Packets);
	}

	[Fact]
	public void LossAndJitter_EndToEnd_KeepTheStreamContinuous()
	{
		// 1 % of reports lost (concealed by the decoder), the rest 0-25 ms late: the virtual mic never runs dry after start.
		PlcDecoder decoder = new();
		using MicPipeline pipeline = new(decoder);
		Random random = new(3);
		byte[] pcm = new byte[48 * 2 * 2];
		int nextPacket = 0;
		double nextArrival = 0;
		for (long ms = 0; ms < 60_000; ms++)
		{
			while (nextArrival <= ms)
			{
				if (random.NextDouble() >= 0.01)
				{
					pipeline.PushReport(MicPackets.Build([0xD4], counter: (byte)nextPacket));
				}
				nextPacket++;
				nextArrival = Math.Max(nextArrival, nextPacket * 10 + random.NextDouble() * 25);
			}
			pipeline.Fill(pcm, 2);
		}
		Assert.InRange(pipeline.LostPackets, 30, 100);
		Assert.Equal(pipeline.LostPackets, pipeline.ConcealedPackets);
		Assert.Equal(0, pipeline.Buffer.Underruns);
		string summary = pipeline.TakeSummary();
		Assert.Contains($"{pipeline.Packets} packets ({pipeline.LostPackets} lost, {pipeline.ConcealedPackets} concealed, 0 bad), 0 underruns (0 ms concealed), 0 ms dropped, buffer ", summary);
		Assert.Contains("/40/", summary);
		Assert.False(pipeline.HasNews);
	}
}
