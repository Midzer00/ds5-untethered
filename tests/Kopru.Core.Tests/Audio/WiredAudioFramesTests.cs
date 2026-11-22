// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Kopru.Core.Audio;
using Xunit;

namespace Kopru.Core.Tests;

// The wired (USB) audio mix: haptics on channels 3-4 (16x upsampled, ramped), the game's speaker on channels 1-2.
public class WiredAudioFramesTests
{
	private static byte[] FourChannel(int frames, short left, short right)
	{
		byte[] audio = new byte[frames * 8];
		for (int i = 0; i < frames; i++)
		{
			BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(i * 8), left);
			BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(i * 8 + 2), right);
		}
		return audio;
	}

	[Fact]
	public void HapticsBlock_RampsToEachSample_OnChannels3And4_AndStartsWhereTheLastBlockEnded()
	{
		WiredAudioFrames frames = new();
		float[] output = new float[WiredAudioFrames.Upsample * 4];

		Assert.Equal(WiredAudioFrames.Upsample, frames.Build([(sbyte)127], [(sbyte)-127], output, 4));

		for (int k = 0; k < WiredAudioFrames.Upsample; k++)
		{
			float t = (k + 1) / (float)WiredAudioFrames.Upsample;
			Assert.Equal(t, output[k * 4 + 2], 4);
			Assert.Equal(-t, output[k * 4 + 3], 4);
			// No speaker audio: channels 1-2 stay silent.
			Assert.Equal(0, output[k * 4]);
			Assert.Equal(0, output[k * 4 + 1]);
		}

		Array.Clear(output);
		frames.Build([(sbyte)0], [(sbyte)0], output, 4);
		for (int k = 0; k < WiredAudioFrames.Upsample; k++)
		{
			Assert.Equal(1f - (k + 1) / (float)WiredAudioFrames.Upsample, output[k * 4 + 2], 4);
			Assert.Equal(-1f + (k + 1) / (float)WiredAudioFrames.Upsample, output[k * 4 + 3], 4);
		}
	}

	[Fact]
	public void Speaker_WaitsForTheCushion_PlaysInOrder_AndWaitsAgainAfterRunningDry()
	{
		WiredAudioFrames frames = new();
		float[] output = new float[WiredAudioFrames.Upsample * 4];

		frames.PushSpeaker(FourChannel(WiredAudioFrames.SpeakerStartFrames - 2, 1000, 2000));
		Assert.Equal(WiredAudioFrames.SpeakerStartFrames - 2, frames.SpeakerBuffered);
		frames.Build([(sbyte)0], [(sbyte)0], output, 4);
		Assert.All(output, v => Assert.Equal(0, v));

		frames.PushSpeaker(FourChannel(100, 1000, 2000));
		frames.Build([(sbyte)0], [(sbyte)0], output, 4);
		Assert.Equal(1000 / 32768f, output[0]);
		Assert.Equal(2000 / 32768f, output[1]);
		// One block (16 frames) was played.
		Assert.Equal(WiredAudioFrames.SpeakerStartFrames + 98 - WiredAudioFrames.Upsample, frames.SpeakerBuffered);

		// Pushing the rest and building enough blocks drains the buffer in order, then one underrun is counted.
		Array.Clear(output);
		while (frames.SpeakerBuffered > 0)
		{
			frames.Build([(sbyte)0], [(sbyte)0], output, 4);
		}
		frames.Build([(sbyte)0], [(sbyte)0], output, 4);
		Assert.Equal(1, frames.SpeakerUnderruns);
		Assert.Equal(0, output[0]);
		Assert.Equal(0, output[1]);

		// Below the cushion again: still silent even though frames arrived.
		frames.PushSpeaker(FourChannel(10, 3000, 4000));
		frames.Build([(sbyte)0], [(sbyte)0], output, 4);
		Assert.Equal(0, output[0]);
		Assert.Equal(0, output[1]);
	}

	[Fact]
	public void Speaker_DropsTheOldest_WhenMoreThan200MsAreBuffered()
	{
		WiredAudioFrames frames = new();
		frames.PushSpeaker(FourChannel(WiredAudioFrames.SpeakerMaxFrames, 1, 1));
		Assert.Equal(0, frames.DroppedSpeakerFrames);

		frames.PushSpeaker(FourChannel(10, 2, 2));
		Assert.Equal(WiredAudioFrames.SpeakerMaxFrames, frames.SpeakerBuffered);
		Assert.Equal(10, frames.DroppedSpeakerFrames);
	}

	// Speaker audio alone plays on channels 1-2 only: without haptics pushed, channels 3-4 stay at zero.
	[Fact]
	public void SpeakerAlone_LeavesChannels3And4Silent()
	{
		WiredAudioFrames frames = new();
		float[] output = new float[WiredAudioFrames.Upsample * 4];
		frames.PushSpeaker(FourChannel(WiredAudioFrames.SpeakerStartFrames, 9000, -9000));

		frames.Build([(sbyte)0], [(sbyte)0], output, 4);

		Assert.Equal(9000 / 32768f, output[0]);
		Assert.Equal(-9000 / 32768f, output[1]);
		for (int k = 0; k < WiredAudioFrames.Upsample; k++)
		{
			Assert.Equal(0, output[k * 4 + 2]);
			Assert.Equal(0, output[k * 4 + 3]);
		}
	}

	[Fact]
	public void Build_SixChannels_LeavesChannels5And6Silent()
	{
		WiredAudioFrames frames = new();
		float[] output = new float[WiredAudioFrames.Upsample * 6];
		Array.Fill(output, 9);

		frames.Build([(sbyte)127], [(sbyte)127], output, 6);

		for (int k = 0; k < WiredAudioFrames.Upsample; k++)
		{
			Assert.Equal(0, output[k * 6 + 4]);
			Assert.Equal(0, output[k * 6 + 5]);
			Assert.NotEqual(0, output[k * 6 + 2]);
		}
	}

	// A tail left below the start cushion (the game went quiet mid-buffer) is not speaker audio: the stream may
	// stop, and after SpeakerFlushFrames of output without a push the tail is dropped, never played late.
	[Fact]
	public void Speaker_AStaleTail_BelowTheCushion_IsNotSpeakerAudio_AndIsFlushed()
	{
		WiredAudioFrames frames = new();
		float[] output = new float[WiredAudioFrames.Upsample * 4];

		frames.PushSpeaker(FourChannel(WiredAudioFrames.SpeakerStartFrames + 10, 5000, 6000));
		Assert.True(frames.HasSpeakerAudio);
		while (frames.SpeakerBuffered > 0)
		{
			frames.Build([(sbyte)0], [(sbyte)0], output, 4);
		}
		Assert.Equal(1, frames.SpeakerUnderruns);
		Assert.False(frames.HasSpeakerAudio);

		frames.PushSpeaker(FourChannel(10, 7000, 8000));
		Assert.False(frames.HasSpeakerAudio);
		long before = frames.DroppedSpeakerFrames;
		for (int i = 0; i <= WiredAudioFrames.SpeakerFlushFrames / WiredAudioFrames.Upsample; i++)
		{
			frames.Build([(sbyte)0], [(sbyte)0], output, 4);
		}
		Assert.Equal(0, frames.SpeakerBuffered);
		Assert.Equal(before + 10, frames.DroppedSpeakerFrames);
		Assert.All(output, v => Assert.Equal(0, v));

		// The next sound starts fresh: the flushed tail never played.
		frames.PushSpeaker(FourChannel(WiredAudioFrames.SpeakerStartFrames, 1000, 1000));
		frames.Build([(sbyte)0], [(sbyte)0], output, 4);
		Assert.Equal(1000 / 32768f, output[0]);
	}

	[Fact]
	public void Reset_ClearsSpeakerAudio_AndTheHapticsRamp()
	{
		WiredAudioFrames frames = new();
		float[] output = new float[WiredAudioFrames.Upsample * 4];
		frames.PushSpeaker(FourChannel(WiredAudioFrames.SpeakerStartFrames, 9000, 9000));
		frames.Build([(sbyte)127], [(sbyte)127], output, 4);
		Assert.True(frames.HasSpeakerAudio);

		frames.Reset();

		Assert.Equal(0, frames.SpeakerBuffered);
		Assert.False(frames.HasSpeakerAudio);
		Array.Clear(output);
		frames.Build([(sbyte)127], [(sbyte)0], output, 4);
		Assert.Equal(1 / (float)WiredAudioFrames.Upsample, output[2], 4);
	}

	[Fact]
	public void Build_RefusesTooFewChannels_OrAShortOutput()
	{
		WiredAudioFrames frames = new();
		Assert.Throws<ArgumentException>(() => frames.Build([(sbyte)0], [(sbyte)0], new float[WiredAudioFrames.Upsample * 3], 3));
		Assert.Throws<ArgumentException>(() => frames.Build([(sbyte)0], [(sbyte)0], new float[WiredAudioFrames.Upsample * 4 - 1], 4));
	}
}
