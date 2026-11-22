// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Protocol;
using Xunit;

namespace Kopru.Core.Tests;

internal sealed class FakeOpusEncoder : IOpusEncoder
{
	public List<short[]> Frames { get; } = [];

	public int Encode(ReadOnlySpan<short> pcm, Span<byte> output)
	{
		Frames.Add(pcm.ToArray());
		output.Fill(0xAB);
		return output.Length;
	}

	public void Dispose()
	{
	}
}

public class SpeakerAudioTests
{
	private static byte[] FourChannel(int frames, Func<int, (short L, short R, short H)> sample)
	{
		byte[] audio = new byte[frames * 8];
		for (int i = 0; i < frames; i++)
		{
			(short l, short r, short h) = sample(i);
			BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(i * 8), l);
			BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(i * 8 + 2), r);
			BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(i * 8 + 4), h);
			BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(i * 8 + 6), h);
		}
		return audio;
	}

	[Fact]
	public void SpeakerReportKeepsTheHapticsLayoutAndAddsOpus()
	{
		sbyte[] left = Enumerable.Range(0, 32).Select(i => (sbyte)i).ToArray();
		sbyte[] right = Enumerable.Range(0, 32).Select(i => (sbyte)-i).ToArray();
		byte[] opus = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
		byte[] report = new byte[547];
		SpeakerReports.Build(left, right, opus, headset: false, sequence: 5, counter: 9, bufferDepth: 0x20, report);
		byte[] haptics = new byte[142];
		BluetoothReports.BuildHaptics(left, right, 5, 9, 0x20, haptics);

		Assert.Equal(0x36, report[0]);
		Assert.Equal(haptics.AsSpan(1, 76).ToArray(), report.AsSpan(1, 76).ToArray());
		Assert.Equal(0x93, report[77]);
		Assert.Equal(200, report[78]);
		Assert.Equal(opus, report.AsSpan(79, 200).ToArray());
		Assert.Equal(Crc.Compute(Crc.OutputSeed, report.AsSpan(0, 394)), BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(394)));
		Assert.All(report.AsSpan(398).ToArray(), b => Assert.Equal(0, b));

		SpeakerReports.Build(left, right, opus, headset: true, 5, 9, 0x20, report);
		Assert.Equal(0x96, report[77]);
	}

	[Fact]
	public void SpeakerReportRejectsWrongSizes()
	{
		sbyte[] side = new sbyte[32];
		Assert.Throws<ArgumentException>(() => SpeakerReports.Build(side, side, new byte[199], false, 0, 0, 0x20, new byte[398]));
		Assert.Throws<ArgumentException>(() => SpeakerReports.Build(side, side, new byte[200], false, 0, 0, 0x20, new byte[397]));
	}

	[Fact]
	public void RouteSetsSpeakerOrHeadphones()
	{
		byte[] block = new byte[OutputBlock.Size];
		AudioOutput.SetRoute(block, headset: false, volume: 1);
		Assert.Equal(0xB0, block[0] & 0xB0);
		Assert.Equal(0x80, block[1] & 0x80);
		Assert.Equal(0x30, block[AudioOutput.AudioControl]);
		Assert.Equal(2, block[AudioOutput.AudioControl2]);
		Assert.Equal(0x64, block[AudioOutput.SpeakerVolume]);
		Assert.Equal(0, block[AudioOutput.HeadphoneVolume]);

		AudioOutput.SetRoute(block, headset: true, volume: 1);
		Assert.Equal(0, block[AudioOutput.AudioControl]);
		Assert.Equal(0x7F, block[AudioOutput.HeadphoneVolume]);
		Assert.Equal(0, block[AudioOutput.SpeakerVolume]);

		AudioOutput.SetRoute(block, headset: false, volume: 0);
		Assert.Equal(0, block[AudioOutput.SpeakerVolume]);

		AudioOutput.Clear(block);
		Assert.All(block, b => Assert.Equal(0, b));
	}

	[Fact]
	public void HeadphoneBitIsReadFromStatusByteOne()
	{
		byte[] body = new byte[63];
		Assert.False(AudioOutput.HeadphonesPlugged(body));
		body[53] = 0x02; // microphone only
		Assert.False(AudioOutput.HeadphonesPlugged(body));
		body[53] = 0x03;
		Assert.True(AudioOutput.HeadphonesPlugged(body));
	}

	[Fact]
	public void ResamplerTurns512SamplesInto480()
	{
		StereoResampler resampler = new(48000, 45000);
		int outputs = 0;
		for (int i = 0; i < 512 * 100; i++)
		{
			resampler.Process(1000, -1000, (_, _) => outputs++);
		}
		Assert.InRange(outputs, 480 * 100 - 1, 480 * 100 + 1);
	}

	[Fact]
	public void ResamplerKeepsLevelAndFrequency()
	{
		StereoResampler resampler = new(48000, 45000);
		List<short> output = [];
		for (int i = 0; i < 48000; i++)
		{
			short v = (short)Math.Round(10000 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0));
			resampler.Process(v, v, (l, _) => output.Add(l));
		}
		// 1 kHz at 48 kHz in -> the same 1 000 cycles in one second of 45 kHz output.
		int crossings = 0;
		for (int i = 1; i < output.Count; i++)
		{
			if (output[i - 1] < 0 && output[i] >= 0)
			{
				crossings++;
			}
		}
		Assert.InRange(crossings, 998, 1001);
		Assert.InRange(output.Max(), (short)9900, (short)10000);
	}

	[Fact]
	public void SenderMixesToMonoForTheSpeakerAndKeepsStereoForHeadphones()
	{
		FakeOpusEncoder encoder = new();
		using SpeakerAudioSender sender = new(encoder);
		byte[] opus = new byte[200];
		Assert.False(sender.TakeFrame(opus));

		sender.Push(FourChannel(2800, _ => (2000, 0, 500))); // past the prefill
		Assert.True(sender.TakeFrame(opus));
		Assert.All(opus, b => Assert.Equal(0xAB, b));
		short[] frame = encoder.Frames[^1];
		Assert.Equal(960, frame.Length);
		Assert.Equal(1000, frame[100]);
		Assert.Equal(1000, frame[101]);

		sender.Headset = true;
		sender.Push(FourChannel(2048, _ => (2000, -2000, 500)));
		while (sender.Buffered > 480)
		{
			sender.TakeFrame(opus);
		}
		sender.TakeFrame(opus);
		frame = encoder.Frames[^1];
		Assert.Equal(2000, frame[0]);
		Assert.Equal(-2000, frame[1]);
	}

	[Fact]
	public void SenderAcceptsAnyChunkingAndDropsOldAudio()
	{
		using SpeakerAudioSender sender = new(new FakeOpusEncoder());
		byte[] audio = FourChannel(512, _ => (100, 100, 0));
		for (int i = 0; i < audio.Length; i += 3)
		{
			sender.Push(audio.AsSpan(i, Math.Min(3, audio.Length - i)));
		}
		Assert.InRange(sender.Buffered, 479, 481);
		sender.Push(FourChannel(48000, _ => (1, 1, 0)));
		Assert.Equal(SpeakerAudioSender.MaxBufferedSamples, sender.Buffered);
	}

	[Fact]
	public void SenderWaitsForThePrefill_WhileTheGameKeepsSending()
	{
		using SpeakerAudioSender sender = new(new FakeOpusEncoder());
		byte[] opus = new byte[200];
		for (int i = 0; i < 5; i++)
		{
			sender.Push(FourChannel(480, _ => (3000, 3000, 0))); // 450 samples a time: 2250 is still under the prefill
			Assert.False(sender.TakeFrame(opus));
		}
		sender.Push(FourChannel(480, _ => (3000, 3000, 0))); // 2700 samples
		Assert.True(sender.TakeFrame(opus));
		Assert.Equal(0, sender.Underruns);
	}

	[Fact]
	public void SenderPlaysAShortSoundOnceTheGameStopsSending()
	{
		FakeOpusEncoder encoder = new();
		using SpeakerAudioSender sender = new(encoder);
		byte[] opus = new byte[200];
		sender.Push(FourChannel(300, _ => (3000, 3000, 0))); // ~281 samples, never reaches the prefill
		for (int i = 0; i < SpeakerAudioSender.StoppedAfterTakes; i++)
		{
			Assert.False(sender.TakeFrame(opus)); // a late USB burst is waited for
		}
		Assert.True(sender.TakeFrame(opus)); // nothing new arrived for a while: the sound goes out, faded and padded at its end
		Assert.Equal(3000, encoder.Frames[^1][100]);
		Assert.Equal(0, encoder.Frames[^1][^1]);
		Assert.False(sender.TakeFrame(opus));
		Assert.Equal(0, sender.Underruns); // the end of a sound is not an underrun
	}

	// The game's audio in 10 ms USB chunks (480 frames -> 450 samples), the reports every 10.67 ms at any phase: with the
	// prefill no frame runs dry mid-sound. Before, a frame went out (padded with silence) whenever anything was buffered,
	// several gaps a second (the crackle).
	[Theory]
	[InlineData(0.0)]
	[InlineData(2.5)]
	[InlineData(5.0)]
	[InlineData(7.5)]
	[InlineData(9.9)]
	public void SenderDoesNotRunDryMidSound_With10MsChunks(double phaseMs)
	{
		FakeOpusEncoder encoder = new();
		using SpeakerAudioSender sender = new(encoder);
		byte[] opus = new byte[200];
		byte[] chunk = FourChannel(480, _ => (3000, 3000, 0));
		const double ReportMs = 32.0 / 3;
		double nextChunk = 0, nextReport = phaseMs;
		int frames = 0;
		while (nextChunk < 2000)
		{
			if (nextChunk <= nextReport)
			{
				sender.Push(chunk);
				nextChunk += 10;
			}
			else
			{
				frames += sender.TakeFrame(opus) ? 1 : 0;
				nextReport += ReportMs;
			}
		}
		Assert.Equal(0, sender.Underruns);
		Assert.True(frames > 175);
		// No sample of silence inside the sound: every frame sent while the game was sending is full (the resampler's very
		// first sample starts from its silent state).
		Assert.All(encoder.Frames, f => Assert.DoesNotContain((short)0, f.Skip(2)));
	}

	[Fact]
	public void SenderCountsUnderrunsAndDroppedSamples()
	{
		using SpeakerAudioSender sender = new(new FakeOpusEncoder());
		byte[] opus = new byte[200];
		sender.Push(FourChannel(2800, _ => (3000, 3000, 0)));
		Assert.True(sender.TakeFrame(opus));
		// The game still sends, but too little: the buffer runs dry.
		for (int i = 0; i < 9 && sender.Underruns == 0; i++)
		{
			sender.Push(FourChannel(48, _ => (3000, 3000, 0)));
			sender.TakeFrame(opus);
		}
		Assert.Equal(1, sender.Underruns); // counted when audio arrived after the dry frame
		Assert.Equal(0, sender.DroppedSamples);
		sender.Push(FourChannel(12000, _ => (1, 1, 0))); // ~11 000 samples into a buffer of MaxBufferedSamples
		Assert.Equal(SpeakerAudioSender.MaxBufferedSamples, sender.Buffered);
		Assert.InRange(sender.DroppedSamples, 1000L, 2000L);
	}

	[Fact]
	public void SessionInterleavesSpeakerWithHaptics()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		FakeOpusEncoder encoder = new();
		session.Speaker = new SpeakerAudioSender(encoder) { Volume = 1 };
		byte[] route = link.Writes.Last();
		Assert.Equal(0x31, route[0]);
		Assert.Equal(0x30, route[3 + AudioOutput.AudioControl]);
		Assert.Equal(0x64, route[3 + AudioOutput.SpeakerVolume]);

		for (int i = 0; i < 64; i++)
		{
			session.Haptics.Push(50, 50);
		}
		session.PushSpeakerAudio(FourChannel(2800, _ => (3000, 3000, 0))); // ~2625 samples (past the prefill): six Opus frames
		SpinWait.SpinUntil(() => link.Writes.Count(w => w[0] == 0x32) >= 2, 3000);

		List<byte[]> stream = link.Writes.Where(w => w[0] is 0x32 or 0x36).ToList();
		Assert.True(stream.Count >= 7);
		// First the six frames of speech (with haptics inside), then haptics-only reports; the counter runs on.
		for (int i = 0; i < 6; i++)
		{
			Assert.Equal(0x36, stream[i][0]);
		}
		Assert.Equal(0x32, stream[6][0]);
		Assert.True((sbyte)stream[0][13] > 0); // the mixer's haptics are inside the speaker report
		Assert.Equal(stream[0][10] + 1, stream[1][10]);
		Assert.Equal(stream[5][10] + 1, stream[6][10]);
		Assert.All(stream, w => Assert.Equal(547, w.Length));
		Assert.Equal(Crc.Compute(Crc.OutputSeed, stream[0].AsSpan(0, 394)), BinaryPrimitives.ReadUInt32LittleEndian(stream[0].AsSpan(394)));
		Assert.All(stream[6].AsSpan(142).ToArray(), b => Assert.Equal(0, b));
		Assert.Equal(6, session.SpeakerReports);
	}

	[Fact]
	public void SessionFollowsTheHeadphoneJack()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		session.Speaker = new SpeakerAudioSender(new FakeOpusEncoder()) { Volume = 1 };
		session.Start();
		byte[] input = FakeLink.Input(0, 1);
		input[2 + 53] = 0x01;
		BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(74), Crc.Compute(Crc.InputSeed, input.AsSpan(0, 74)));
		link.Feed(input);
		SpinWait.SpinUntil(() => session.Speaker.Headset, 3000);
		SpinWait.SpinUntil(() => link.Writes.Last()[3 + AudioOutput.HeadphoneVolume] == 0x7F, 3000);
		Assert.True(session.Speaker.Headset);
		Assert.Equal(0, link.Writes.Last()[3 + AudioOutput.AudioControl]);
	}

	[Fact]
	public void SpeakerIsOffByDefault()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		Assert.Null(session.Speaker);
		session.PushSpeakerAudio(FourChannel(512, _ => (3000, 3000, 0)));
		Assert.Empty(link.Writes);
	}
}
