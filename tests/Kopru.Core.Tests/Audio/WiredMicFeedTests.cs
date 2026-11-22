// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;
using Xunit;

namespace Kopru.Core.Tests;

// The controller's USB microphone capture: interleaved float blocks -> mono 48 kHz 16-bit for the mic pipeline.
public class WiredMicFeedTests
{
	private static (WiredMicFeed feed, List<short[]> pushed) Feed()
	{
		List<short[]> pushed = [];
		return (new WiredMicFeed(span => pushed.Add(span.ToArray())), pushed);
	}

	[Fact]
	public void Stereo48k_IsAveragedToMono16Bit()
	{
		(var feed, var pushed) = Feed();

		feed.OnAudio([0.5f, -0.5f, 0.25f, 0.75f], 2, 48000);

		short[] mono = Assert.Single(pushed);
		Assert.Equal(0, mono[0]);
		Assert.InRange(mono[1], 16383, 16385);
	}

	[Fact]
	public void Overshoot_IsClampedTo16Bit()
	{
		(var feed, var pushed) = Feed();

		feed.OnAudio([1f, 1f, -1f, -1f, 3f, 2f, -2f, -3f], 2, 48000);

		Assert.Equal([short.MaxValue, short.MinValue, short.MaxValue, short.MinValue], Assert.Single(pushed));
	}

	[Fact]
	public void Mono48k_PassesThrough()
	{
		(var feed, var pushed) = Feed();

		feed.OnAudio([0.5f, -0.25f, 0f], 1, 48000);

		short[] mono = Assert.Single(pushed);
		Assert.Equal(3, mono.Length);
		Assert.InRange(mono[0], 16383, 16384);
		Assert.InRange(mono[1], -8193, -8191);
		Assert.Equal(0, mono[2]);
	}

	[Fact]
	public void LowerInputRate_IsResampledTo48k()
	{
		(var feed, var pushed) = Feed();

		feed.OnAudio(Enumerable.Repeat(0.5f, 24).ToArray(), 1, 24000);

		Assert.Single(pushed);
		Assert.InRange(pushed[0].Length, 2 * 24 - 2, 2 * 24 + 2);
	}

	[Fact]
	public void EmptyInput_AndNoChannels_PushNothing()
	{
		(var feed, var pushed) = Feed();

		feed.OnAudio([], 2, 48000);
		feed.OnAudio([0.5f, 0.5f], 0, 48000);

		Assert.Empty(pushed);
	}
}
