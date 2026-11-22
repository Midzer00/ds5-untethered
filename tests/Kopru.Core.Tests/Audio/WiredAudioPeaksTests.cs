// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;
using Xunit;

namespace Kopru.Core.Tests;

// The wired sink's peak meter: haptics (channels 3-4) and speaker (channels 1-2) peaks over the written audio;
// a full window reports, an idle stream reports after a second, and a reported window starts over.
public class WiredAudioPeaksTests
{
	private static float[] Block(int frames, int channels, params (int Channel, float Value)[] samples)
	{
		float[] block = new float[frames * channels];
		foreach ((int channel, float value) in samples)
		{
			for (int f = 0; f < frames; f++)
			{
				block[f * channels + channel] = value;
			}
		}
		return block;
	}

	[Fact]
	public void Peaks_OfSpeakerAndHapticsChannels_AreSeparate_ByAbsoluteValue_ChannelsBeyondFourIgnored()
	{
		WiredAudioPeaks peaks = new();
		// A huge value on channel 6 must not leak into either peak.
		peaks.Add(Block(100, 6, (0, 0.25f), (3, -0.8f), (5, 5f)), 100, 6);
		peaks.Add(Block(WiredAudioPeaks.MinIdleFrames - 100, 6), WiredAudioPeaks.MinIdleFrames - 100, 6);

		Assert.True(peaks.TryTake(true, out float haptics, out float speaker, out int frames));
		Assert.Equal(0.8f, haptics, 6);
		Assert.Equal(0.25f, speaker, 6);
		Assert.Equal(WiredAudioPeaks.MinIdleFrames, frames);
	}

	[Fact]
	public void TryTake_NeedsTheWindow_UnlessIdle_ThenAMinIdleAmount()
	{
		WiredAudioPeaks peaks = new();
		peaks.Add(Block(WiredAudioPeaks.MinIdleFrames - 1, 4, (2, 0.7f)), WiredAudioPeaks.MinIdleFrames - 1, 4);
		Assert.False(peaks.TryTake(true, out _, out _, out _));
		Assert.False(peaks.TryTake(false, out _, out _, out _));

		// One more frame is a second of audio: an idle stream reports it.
		peaks.Add(Block(1, 4), 1, 4);
		Assert.True(peaks.TryTake(true, out float haptics, out _, out int frames));
		Assert.Equal(0.7f, haptics, 6);
		Assert.Equal(WiredAudioPeaks.MinIdleFrames, frames);

		// Not idle, the whole window is needed; reaching it reports and resets.
		peaks.Add(Block(WiredAudioPeaks.WindowFrames - 1, 4, (0, 0.4f)), WiredAudioPeaks.WindowFrames - 1, 4);
		Assert.False(peaks.TryTake(false, out _, out _, out _));
		peaks.Add(Block(1, 4), 1, 4);
		Assert.True(peaks.TryTake(false, out _, out float speaker, out frames));
		Assert.Equal(0.4f, speaker, 6);
		Assert.Equal(WiredAudioPeaks.WindowFrames, frames);
		Assert.False(peaks.TryTake(true, out _, out _, out _));
	}
}
