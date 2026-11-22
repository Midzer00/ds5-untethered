// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;
using Xunit;

namespace Kopru.Core.Tests;

// The wired render queue is steered a few frames per block back to the target instead of underrunning once or
// dropping whole blocks.
public class WiredAudioSteeringTests
{
	[Fact]
	public void Adjust_IsZero_InsideTheDeadband()
	{
		Assert.Equal(0, WiredAudioSteering.Adjust(WiredAudioSteering.TargetFrames));
		Assert.Equal(0, WiredAudioSteering.Adjust(WiredAudioSteering.TargetFrames + WiredAudioSteering.DeadbandFrames));
		Assert.Equal(0, WiredAudioSteering.Adjust(WiredAudioSteering.TargetFrames - WiredAudioSteering.DeadbandFrames));
	}

	[Fact]
	public void Adjust_ShrinksAboveTheTarget_AndStretchesBelow_AtLeastOneFrame()
	{
		// Just outside the deadband the nudge is at least one frame, with the sign toward the target.
		Assert.Equal(-3, WiredAudioSteering.Adjust(WiredAudioSteering.TargetFrames + WiredAudioSteering.DeadbandFrames + 1));
		Assert.Equal(3, WiredAudioSteering.Adjust(WiredAudioSteering.TargetFrames - WiredAudioSteering.DeadbandFrames - 1));
		Assert.True(WiredAudioSteering.Adjust(WiredAudioSteering.TargetFrames + 200) < 0);
		Assert.True(WiredAudioSteering.Adjust(WiredAudioSteering.TargetFrames - 200) > 0);
	}

	[Fact]
	public void Adjust_IsClamped_ToMaxFramesPerBlock()
	{
		Assert.Equal(-WiredAudioSteering.MaxFramesPerBlock, WiredAudioSteering.Adjust(WiredAudioSteering.TargetFrames + 10000));
		Assert.Equal(WiredAudioSteering.MaxFramesPerBlock, WiredAudioSteering.Adjust(0));
	}

	[Fact]
	public void Apply_Drop_TakesFramesOffTheEnd()
	{
		float[] block = new float[24];
		for (int i = 0; i < block.Length; i++)
		{
			block[i] = i;
		}
		Assert.Equal(4, WiredAudioSteering.Apply(block, 6, 2, -2));
		Assert.Equal(new float[] { 0, 1, 2, 3, 4, 5, 6, 7 }, block[..8].ToArray());
	}

	[Fact]
	public void Apply_Repeat_CopiesTheLastFrame_OnEveryChannel()
	{
		float[] block = new float[20];
		new float[] { 1, 2, 3, 4, 5, 6 }.CopyTo(block, 0);
		Assert.Equal(5, WiredAudioSteering.Apply(block, 3, 2, 2));
		Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6, 5, 6, 5, 6 }, block[..10].ToArray());
	}

	[Fact]
	public void Apply_Zero_LeavesTheBlockUnchanged()
	{
		float[] block = { 1, 2, 3, 4, 5, 6 };
		Assert.Equal(3, WiredAudioSteering.Apply(block, 3, 2, 0));
		Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6 }, block);
	}
}
