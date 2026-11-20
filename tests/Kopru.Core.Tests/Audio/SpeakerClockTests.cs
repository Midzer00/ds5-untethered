// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Kopru.Core.Audio;
using Xunit;

namespace Kopru.Core.Tests;

// The speaker buffer between the game's bursty USB audio and the report timer, simulated in time: the source delivers
// 10 ms chunks late by a random 0-12 ms (now and then a 20 ms hold, then the held chunks at once), the report timer runs
// 0.5 % fast or slow and now and then stalls (then catches up, as the session's loop does).
public class SpeakerClockTests
{
	private const double ChunkMs = 10;
	private const int ChunkFrames = 480;
	private const double ReportMs = 32.0 / 3;

	private sealed class Simulation(double sinkDrift, int seed)
	{
		private readonly Random random = new(seed);

		public FakeOpusEncoder Encoder { get; } = new();

		public SpeakerAudioSender Sender { get; private set; } = null!;

		public int Frames { get; private set; }

		// Plays sounds (ms of sound, then ms of nothing pushed at all) through one sender; the sound is a full-scale-ish
		// sine, followed by the bridge's 200 ms tail of silence.
		public void Run(IEnumerable<(double SoundMs, double GapMs)> sounds, double amplitude = 12000, bool headset = false)
		{
			Sender = new SpeakerAudioSender(Encoder) { Headset = headset };
			List<(double At, byte[] Chunk)> chunks = [];
			double t = 0;
			long sample = 0;
			foreach ((double soundMs, double gapMs) in sounds)
			{
				int soundChunks = (int)(soundMs / ChunkMs);
				int tailChunks = 20;
				for (int c = 0; c < soundChunks + tailChunks; c++)
				{
					bool sound = c < soundChunks;
					byte[] chunk = new byte[ChunkFrames * 8];
					for (int i = 0; i < ChunkFrames; i++, sample++)
					{
						short v = sound ? (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * 440 * sample / 48000.0)) : (short)0;
						BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(i * 8), v);
						BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(i * 8 + 2), v);
					}
					chunks.Add((t, chunk));
					t += ChunkMs;
				}
				t += gapMs;
			}
			// Delivery: late by 0-12 ms; every ~1.5 s three chunks are held 20 ms more. Never out of order.
			double[] due = new double[chunks.Count];
			double last = 0;
			for (int i = 0; i < chunks.Count; i++)
			{
				double late = random.NextDouble() * 12 + (i % 150 < 3 ? 20 : 0);
				last = Math.Max(last, chunks[i].At + late);
				due[i] = last;
			}
			double end = t + 500;
			double reportMs = ReportMs * (1 + sinkDrift);
			byte[] opus = new byte[SpeakerReports.OpusFrameBytes];
			int next = 0;
			long report = 0;
			double stallUntil = 0;
			while (true)
			{
				double nominal = report * reportMs;
				if (report % 220 == 100)
				{
					stallUntil = nominal + 15; // the report thread is late, then catches up
				}
				double reportAt = Math.Max(nominal, stallUntil);
				if (next < chunks.Count && due[next] <= reportAt)
				{
					Sender.Push(chunks[next].Chunk);
					next++;
					continue;
				}
				if (reportAt > end)
				{
					break;
				}
				Frames += Sender.TakeFrame(opus) ? 1 : 0;
				report++;
			}
		}
	}

	[Theory]
	[InlineData(0.005, 1)]
	[InlineData(-0.005, 2)]
	[InlineData(0.0, 3)]
	[InlineData(0.005, 10)]
	[InlineData(-0.005, 11)]
	public void SixtySecondsOfSound_NoUnderrunsNoDrops(double sinkDrift, int seed)
	{
		Simulation sim = new(sinkDrift, seed);
		sim.Run([(60000, 0)]);
		Assert.Equal(0, sim.Sender.Underruns);
		Assert.Equal(0, sim.Sender.DroppedSamples);
		// The level control took up the clock difference.
		Assert.InRange(sim.Sender.Trim, -0.01, 0.01);
		Assert.True(sim.Frames > 5500);
	}

	[Theory]
	[InlineData(0.005, 4)]
	[InlineData(-0.005, 5)]
	[InlineData(0.005, 7)]
	[InlineData(-0.005, 8)]
	[InlineData(0.0, 9)]
	public void ManyShortSounds_NoUnderrunsNoDrops(double sinkDrift, int seed)
	{
		Random lengths = new(seed);
		List<(double, double)> sounds = [];
		for (int i = 0; i < 80; i++)
		{
			sounds.Add((100 + lengths.Next(0, 70) * 10, 50 + lengths.Next(0, 150) * 10));
		}
		Simulation sim = new(sinkDrift, seed);
		sim.Run(sounds);
		Assert.Equal(0, sim.Sender.Underruns);
		Assert.Equal(0, sim.Sender.DroppedSamples);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FullScaleInput_NeverWrapsOrExceedsFullScale(bool headset)
	{
		Simulation sim = new(0.005, 6);
		sim.Run([(3000, 0)], amplitude: 32767, headset: headset);
		int peak = 0;
		foreach (short[] f in sim.Encoder.Frames)
		{
			for (int i = 2; i < f.Length; i++)
			{
				peak = Math.Max(peak, Math.Abs((int)f[i]));
				// A wrap-around would jump by close to 65536; a 440 Hz sine moves at most ~2000 per sample here.
				Assert.InRange(Math.Abs(f[i] - f[i - 2]), 0, 3000);
			}
		}
		Assert.InRange(peak, 32000, 32767);
	}

	[Fact]
	public void ForcedUnderrun_FadesOutAndBackIn()
	{
		FakeOpusEncoder encoder = new();
		using SpeakerAudioSender sender = new(encoder);
		byte[] opus = new byte[SpeakerReports.OpusFrameBytes];
		byte[] Dc(int frames) => Enumerable.Range(0, frames).SelectMany(_ => new byte[] { 0x10, 0x27, 0x10, 0x27, 0, 0, 0, 0 }).ToArray(); // 10 000

		sender.Push(Dc(2800));
		List<short> output = [];
		// The game keeps sending, far too little: the buffer runs dry.
		do
		{
			sender.Push(Dc(16));
			Assert.True(sender.TakeFrame(opus));
			output.AddRange(encoder.Frames[^1].Where((_, i) => i % 2 == 0));
		}
		while (output[^1] != 0);
		Assert.Equal(0, sender.Underruns); // counted once more audio arrives
		AssertNoStep(output.Skip(1).ToList()); // (the resampler's very first sample starts from silence)
		Assert.Equal(0, output[^1]);
		Assert.Equal(10000, output[1]);

		// The sound resumes: it builds its cushion again and fades in.
		sender.Push(Dc(2800));
		Assert.Equal(1, sender.Underruns);
		Assert.True(sender.TakeFrame(opus));
		short[] resumed = encoder.Frames[^1].Where((_, i) => i % 2 == 0).ToArray();
		Assert.InRange(resumed[0], (short)0, (short)100);
		AssertNoStep(resumed);
		Assert.Equal(10000, resumed[SpeakerAudioSender.FadeSamples + 10]);
	}

	[Fact]
	public void BufferRunsDryAtAFrameBoundary_FadesFromTheLastSample()
	{
		FakeOpusEncoder encoder = new();
		using SpeakerAudioSender sender = new(encoder);
		byte[] opus = new byte[SpeakerReports.OpusFrameBytes];
		byte[] Dc(int frames) => Enumerable.Range(0, frames).SelectMany(_ => new byte[] { 0x10, 0x27, 0x10, 0x27, 0, 0, 0, 0 }).ToArray();
		sender.Push(Dc(2800));
		// Take frames while sending nothing new until exactly nothing is left would need luck; instead drain to an exact
		// multiple: keep pushing single samples until the buffer holds a whole number of frames.
		while (sender.Buffered % SpeakerAudioSender.FrameSamples != 0)
		{
			sender.Push(Dc(1));
		}
		while (sender.Buffered > 0)
		{
			sender.Push([]);
			Assert.True(sender.TakeFrame(opus));
		}
		Assert.Equal(10000, encoder.Frames[^1][^1]);
		sender.Push([]); // still "sending"
		Assert.True(sender.TakeFrame(opus));
		short[] fade = encoder.Frames[^1].Where((_, i) => i % 2 == 0).ToArray();
		sender.Push(Dc(16));
		Assert.Equal(1, sender.Underruns);
		Assert.InRange(fade[0], (short)9800, (short)10000);
		AssertNoStep(fade);
		Assert.All(fade.Skip(SpeakerAudioSender.FadeSamples), v => Assert.Equal(0, v));
	}

	// No jump larger than a 128-sample fade of full DC could make.
	private static void AssertNoStep(IReadOnlyList<short> samples)
	{
		for (int i = 1; i < samples.Count; i++)
		{
			Assert.InRange(Math.Abs(samples[i] - samples[i - 1]), 0, 10000 / SpeakerAudioSender.FadeSamples + 2);
		}
	}
}
