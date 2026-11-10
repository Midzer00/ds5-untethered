// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;
using Kopru.Core.Protocol;

namespace Kopru.Core.Tests;

// A clock moved by hand (timestamps in ticks of 1 µs).
internal sealed class StepClock : TimeProvider
{
	private long ticks = 1_000_000;

	public override long TimestampFrequency => 1_000_000;

	public override long GetTimestamp() => Interlocked.Read(ref ticks);

	public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, (long)(by.TotalSeconds * TimestampFrequency));
}

public class ShortcutProcessorTests
{
	private const int Hat = ShortcutProcessor.Buttons0, Ps = ShortcutProcessor.Buttons2;

	private readonly StepClock clock = new();
	private readonly ShortcutProcessor shortcuts;
	private readonly List<ShortcutAction> fired = [];

	public ShortcutProcessorTests()
	{
		shortcuts = new ShortcutProcessor(clock);
		shortcuts.Triggered += fired.Add;
	}

	// A report body: D-pad hat (8 = none), square, triangle, PS. Cross set so passthrough of other buttons is visible.
	private static byte[] Body(int hat = 8, bool square = false, bool triangle = false, bool ps = false)
	{
		var b = new byte[InputState.BodySize];
		b[Hat] = (byte)(hat | 0x20 | (square ? 0x10 : 0) | (triangle ? 0x80 : 0));
		b[Ps] = (byte)(ps ? 0x01 : 0);
		b[0] = 77;
		return b;
	}

	// Runs one report 4 ms after the last one; returns what the game gets.
	private byte[] Step(byte[] body, double ms = 4)
	{
		clock.Advance(TimeSpan.FromMilliseconds(ms));
		byte[] copy = body.ToArray();
		shortcuts.Apply(copy);
		return copy;
	}

	private static bool PsOf(byte[] b) => (b[Ps] & 0x01) != 0;

	private static int HatOf(byte[] b) => b[Hat] & 0x0F;

	[Fact]
	public void PlainPsTap_ReachesTheGame_OnRelease_AsAShortTap()
	{
		Assert.False(PsOf(Step(Body(ps: true))));
		Assert.False(PsOf(Step(Body(ps: true), 50)));
		Assert.True(PsOf(Step(Body())));
		Assert.True(PsOf(Step(Body(), 50)));
		Assert.False(PsOf(Step(Body(), 60)));
		Assert.False(PsOf(Step(Body())));
		Assert.Empty(fired);
	}

	[Fact]
	public void LongPlainPsHold_IsStillPassedOnAsATap()
	{
		Step(Body(ps: true));
		Assert.False(PsOf(Step(Body(ps: true), 2000)));
		Assert.True(PsOf(Step(Body())));
		Assert.Empty(fired);
	}

	[Theory]
	[InlineData(6, ShortcutAction.PreviousProfile)]
	[InlineData(2, ShortcutAction.NextProfile)]
	[InlineData(0, ShortcutAction.HapticsUp)]
	[InlineData(4, ShortcutAction.HapticsDown)]
	public void PsPlusDpad_FiresOnce_AndHidesPsAndTheDpad(int hat, ShortcutAction expected)
	{
		Step(Body(ps: true));
		byte[] game = Step(Body(hat: hat, ps: true));
		Assert.Equal([expected], fired);
		Assert.False(PsOf(game));
		Assert.Equal(8, HatOf(game));
		Assert.Equal(0x20, game[Hat] & 0x20);
		Assert.Equal(77, game[0]);

		// Held: no repeat, still hidden.
		Assert.Equal(8, HatOf(Step(Body(hat: hat, ps: true), 300)));
		Assert.Single(fired);

		// PS let go first: no PS tap after a combo, the D-pad stays hidden until released.
		byte[] afterPs = Step(Body(hat: hat));
		Assert.False(PsOf(afterPs));
		Assert.Equal(8, HatOf(afterPs));
		Step(Body());
		Assert.False(PsOf(Step(Body())));
		// A later D-pad press without PS reaches the game.
		Assert.Equal(hat, HatOf(Step(Body(hat: hat))));
		Assert.Single(fired);
	}

	[Fact]
	public void RepeatedDpadPresses_WhilePsHeld_EachFire()
	{
		Step(Body(ps: true));
		Step(Body(hat: 2, ps: true));
		Step(Body(ps: true));
		Step(Body(hat: 2, ps: true));
		Step(Body(ps: true));
		Step(Body(hat: 6, ps: true));
		Assert.Equal([ShortcutAction.NextProfile, ShortcutAction.NextProfile, ShortcutAction.PreviousProfile], fired);
	}

	[Fact]
	public void DpadAlreadyHeld_WhenPsGoesDown_DoesNotFire()
	{
		Step(Body(hat: 2));
		Step(Body(hat: 2, ps: true));
		Step(Body(hat: 2, ps: true));
		Assert.Empty(fired);
		// The D-pad still reaches the game; PS comes as a tap on release.
		Assert.Equal(2, HatOf(Step(Body(hat: 2, ps: true))));
		Assert.True(PsOf(Step(Body(hat: 2))));
	}

	[Fact]
	public void PsPlusSquare_TogglesSupportedMode_AndHidesSquare()
	{
		Step(Body(ps: true));
		byte[] game = Step(Body(square: true, ps: true));
		Assert.Equal([ShortcutAction.ToggleSupportedMode], fired);
		Assert.Equal(0, game[Hat] & 0x10);
		Assert.Equal(0, Step(Body(square: true))[Hat] & 0x10);
		Step(Body());
		Assert.Equal(0x10, Step(Body(square: true))[Hat] & 0x10);
	}

	[Fact]
	public void PsPlusTriangle_PowersOffOnlyAfterTheHold()
	{
		Step(Body(ps: true));
		Assert.Equal(0, Step(Body(triangle: true, ps: true))[Hat] & 0x80);
		Step(Body(triangle: true, ps: true), 500);
		Step(Body(triangle: true, ps: true), 400);
		Assert.Empty(fired);
		Step(Body(triangle: true, ps: true), 120);
		Assert.Equal([ShortcutAction.PowerOff], fired);
		Step(Body(triangle: true, ps: true), 2000);
		Assert.Single(fired);
	}

	[Fact]
	public void ShortTriangle_DoesNotPowerOff_AndIsHidden()
	{
		Step(Body(ps: true));
		Assert.Equal(0, Step(Body(triangle: true, ps: true))[Hat] & 0x80);
		Step(Body(triangle: true, ps: true), 500);
		Step(Body(ps: true));
		Step(Body(ps: true), 1000);
		Assert.Empty(fired);
		// The combo started: no PS tap.
		Assert.False(PsOf(Step(Body())));
	}

	[Fact]
	public void LettingGoOfPs_StopsThePowerOffHold()
	{
		Step(Body(ps: true));
		Step(Body(triangle: true, ps: true));
		Step(Body(triangle: true), 200);
		Step(Body(triangle: true), 2000);
		Assert.Empty(fired);
	}

	[Fact]
	public void Disabled_PassesEverythingThrough()
	{
		shortcuts.Enabled = false;
		Assert.True(PsOf(Step(Body(ps: true))));
		byte[] game = Step(Body(hat: 2, ps: true));
		Assert.True(PsOf(game));
		Assert.Equal(2, HatOf(game));
		Assert.Empty(fired);
	}

	[Fact]
	public void OtherButtons_PassUnchanged_DuringACombo()
	{
		Step(Body(ps: true));
		byte[] body = Body(hat: 2, ps: true);
		body[8] = 0x03; // L1, R1
		body[4] = 200;
		byte[] game = Step(body);
		Assert.Equal(0x03, game[8]);
		Assert.Equal(200, game[4]);
		Assert.Equal(0x20, game[Hat] & 0x20);
	}

	[Fact]
	public void PlayerLedPatterns_CountUp_AndWrap()
	{
		Assert.Equal(0x04, Profiles.ProfileFeedback.PlayerLeds(1));
		Assert.Equal(0x0A, Profiles.ProfileFeedback.PlayerLeds(2));
		Assert.Equal(0x15, Profiles.ProfileFeedback.PlayerLeds(3));
		Assert.Equal(0x1B, Profiles.ProfileFeedback.PlayerLeds(4));
		Assert.Equal(0x1F, Profiles.ProfileFeedback.PlayerLeds(5));
		Assert.Equal(0x04, Profiles.ProfileFeedback.PlayerLeds(6));
	}
}
