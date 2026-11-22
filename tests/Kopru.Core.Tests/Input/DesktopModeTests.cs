// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.Core.Tests;

// Records what desktop mode / gyro mouse send.
internal sealed class RecordingSink : IInputSink
{
	public List<string> Events { get; } = [];
	public int MoveX, MoveY, Wheel, HWheel;

	public void MoveMouse(int dx, int dy)
	{
		MoveX += dx;
		MoveY += dy;
		Events.Add("move");
	}

	public void MouseButton(MouseButton button, bool down) => Events.Add($"{button} {(down ? "down" : "up")}");

	void IInputSink.Wheel(int delta, bool horizontal)
	{
		if (horizontal)
			HWheel += delta;
		else
			Wheel += delta;
		Events.Add(horizontal ? "hwheel" : "wheel");
	}

	public void Key(ushort virtualKey, bool down) => Events.Add($"key {virtualKey:X2} {(down ? "down" : "up")}");

	public List<string> Without(string kind) => Events.Where(e => e != kind).ToList();

	public void Clear()
	{
		Events.Clear();
		MoveX = MoveY = Wheel = HWheel = 0;
	}
}

public class DesktopModeTests
{
	private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(4);

	private static byte[] Body(byte b0 = 0x08, byte b1 = 0, byte b2 = 0)
	{
		var b = new byte[63];
		b[0] = b[1] = b[2] = b[3] = 128;
		b[7] = b0;
		b[8] = b1;
		b[9] = b2;
		b[32] = 0x80;
		b[36] = 0x80;
		return b;
	}

	private static void Finger(byte[] body, int slot, byte id, int x, int y)
	{
		int at = slot == 0 ? 32 : 36;
		body[at] = id;
		body[at + 1] = (byte)x;
		body[at + 2] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
		body[at + 3] = (byte)(y >> 4);
	}

	private static (DesktopMode Mode, RecordingSink Sink) Active(TouchpadRightClick rightClick = TouchpadRightClick.Off)
	{
		var sink = new RecordingSink();
		var mode = new DesktopMode(sink) { Active = true, Settings = DesktopModeSettings.Default with { RightClick = rightClick } };
		mode.Apply(Body(), Frame);
		return (mode, sink);
	}

	[Fact]
	public void Off_PassesTheBodyUntouched()
	{
		var sink = new RecordingSink();
		var mode = new DesktopMode(sink);
		byte[] body = Body(0x28, 0x01);
		body[2] = 255;
		byte[] copy = (byte[])body.Clone();
		Assert.False(mode.Apply(body, Frame));
		Assert.Equal(copy, body);
		Assert.Empty(sink.Events);
	}

	[Fact]
	public void Active_SendsANeutralPadReport()
	{
		(DesktopMode mode, _) = Active();
		byte[] body = Body(0x28 | 0x80, 0xFF, 0x07);
		body[0] = 0; body[1] = 255; body[2] = 30; body[3] = 220; body[4] = 200; body[5] = 90;
		Finger(body, 0, 3, 500, 400);
		Assert.True(mode.Apply(body, Frame));
		Assert.Equal(new byte[] { 128, 128, 128, 128, 0, 0 }, body[..6]);
		Assert.Equal(0x08, body[7]);
		Assert.Equal(0, body[8]);
		Assert.Equal(0, body[9]);
		Assert.False(TouchPoint.Parse(body.AsSpan(32)).Active);
		Assert.False(TouchPoint.Parse(body.AsSpan(36)).Active);
	}

	[Fact]
	public void Buttons_MapToMouseAndKeys()
	{
		(DesktopMode mode, RecordingSink sink) = Active();
		mode.Apply(Body(0x08 | 0x20), Frame); // Cross down
		mode.Apply(Body(), Frame);
		Assert.Equal(["Left down", "Left up"], sink.Events);
		sink.Clear();
		mode.Apply(Body(0x08 | 0x40), Frame); // Circle
		mode.Apply(Body(), Frame);
		Assert.Equal(["Right down", "Right up"], sink.Events);
		sink.Clear();
		mode.Apply(Body(0x00), Frame); // D-pad up held
		mode.Apply(Body(0x00), Frame);
		mode.Apply(Body(), Frame);
		Assert.Equal(["key 26 down", "key 26 up"], sink.Events);
		sink.Clear();
		mode.Apply(Body(0x08 | 0x10), Frame); // Square = Enter
		mode.Apply(Body(), Frame);
		Assert.Equal(["key 0D down", "key 0D up"], sink.Events);
		sink.Clear();
		mode.Apply(Body(b1: 0x08), Frame); // R2 = volume up
		mode.Apply(Body(b1: 0x04), Frame); // L2 = volume down
		mode.Apply(Body(b1: 0x10), Frame); // Create = play/pause
		mode.Apply(Body(b1: 0x20), Frame); // Options = Windows key
		mode.Apply(Body(), Frame);
		Assert.Equal(["key AF down", "key AF up", "key AE down", "key AE up", "key B3 down", "key B3 up", "key 5B down", "key 5B up"], sink.Events);
		sink.Clear();
		mode.Apply(Body(0x08 | 0x80), Frame); // Triangle = Win+Ctrl+O
		Assert.Equal(["key 5B down", "key 11 down", "key 4F down", "key 4F up", "key 11 up", "key 5B up"], sink.Events);
	}

	[Fact]
	public void WindowSwitcher_HoldsAlt_AndCrossPicks()
	{
		(DesktopMode mode, RecordingSink sink) = Active();
		mode.Apply(Body(b1: 0x02), Frame); // R1
		Assert.Equal(["key 12 down", "key 09 down", "key 09 up"], sink.Events);
		mode.Apply(Body(), Frame);
		mode.Apply(Body(b1: 0x01), Frame); // L1: Shift+Tab, Alt stays down
		Assert.Equal(["key 12 down", "key 09 down", "key 09 up", "key 10 down", "key 09 down", "key 09 up", "key 10 up"], sink.Events);
		sink.Clear();
		mode.Apply(Body(), Frame);
		mode.Apply(Body(0x08 | 0x20), Frame); // Cross picks: Alt up, no click
		mode.Apply(Body(), Frame);
		Assert.Equal(["key 12 up"], sink.Events);

		sink.Clear();
		mode.Apply(Body(b1: 0x02), Frame);
		mode.Apply(Body(), Frame);
		for (int i = 0; i < 200; i++)
			mode.Apply(Body(), Frame);
		Assert.Equal("key 12 up", sink.Events[^1]);
		Assert.Single(sink.Events, "key 12 up");
	}

	[Fact]
	public void TurningOff_ReleasesHeldButtons()
	{
		(DesktopMode mode, RecordingSink sink) = Active();
		mode.Apply(Body(0x08 | 0x20), Frame);
		mode.Apply(Body(0x02), Frame); // Cross released? no: D-pad right now, Cross released
		sink.Clear();
		mode.Apply(Body(0x02 | 0x20), Frame); // Cross + right held
		mode.Active = false;
		byte[] body = Body(0x02 | 0x20);
		Assert.False(mode.Apply(body, Frame));
		Assert.Contains("Left up", sink.Events);
		Assert.Contains("key 27 up", sink.Events);
		Assert.Equal(0x22, body[7]);
	}

	[Fact]
	public void ButtonsHeldWhenTurnedOn_DoNothing()
	{
		var sink = new RecordingSink();
		var mode = new DesktopMode(sink) { Active = true };
		mode.Apply(Body(0x08 | 0x20, 0x20), Frame);
		mode.Apply(Body(), Frame);
		Assert.Empty(sink.Events);
	}

	[Fact]
	public void RightStick_MovesThePointer_WithACurve()
	{
		var s = DesktopModeSettings.Default;
		Assert.Equal((0.0, 0.0), DesktopMode.PointerVelocity(0.1, 0, s));
		(double full, _) = DesktopMode.PointerVelocity(1, 0, s);
		Assert.Equal(DesktopMode.PointerPixelsPerSecond, full, 3);
		(double half, _) = DesktopMode.PointerVelocity(0.575, 0, s);
		Assert.True(half < full / 3, $"half deflection {half}");
		(double linearHalf, _) = DesktopMode.PointerVelocity(0.575, 0, s with { PointerAcceleration = 0 });
		Assert.Equal(full / 2, linearHalf, 1);
		(double fast, _) = DesktopMode.PointerVelocity(1, 0, s with { PointerSpeed = 2 });
		Assert.Equal(2 * full, fast, 3);
		(_, double up) = DesktopMode.PointerVelocity(0, -1, s);
		Assert.True(up < 0);

		(DesktopMode mode, RecordingSink sink) = Active();
		for (int i = 0; i < 250; i++)
		{
			byte[] body = Body();
			body[2] = 255;
			mode.Apply(body, Frame);
		}
		// One second at full deflection.
		Assert.InRange(sink.MoveX, DesktopMode.PointerPixelsPerSecond - 2, DesktopMode.PointerPixelsPerSecond + 2);
		Assert.Equal(0, sink.MoveY);
	}

	[Fact]
	public void LeftStick_Scrolls()
	{
		(DesktopMode mode, RecordingSink sink) = Active();
		for (int i = 0; i < 250; i++)
		{
			byte[] body = Body();
			body[1] = 0; // up
			mode.Apply(body, Frame);
		}
		Assert.InRange(sink.Wheel, DesktopMode.ScrollNotchesPerSecond * 120 - 2, DesktopMode.ScrollNotchesPerSecond * 120 + 2);
		Assert.Equal(0, sink.HWheel);
		Assert.Equal(0, sink.MoveX);
		sink.Clear();
		byte[] right = Body();
		right[0] = 255;
		mode.Apply(right, Frame);
		mode.Apply(right, Frame);
		Assert.True(sink.HWheel > 0);
	}

	[Fact]
	public void Touchpad_OneFingerMoves_FastSwipesGoFurther()
	{
		(DesktopMode mode, RecordingSink sink) = Active();
		for (int i = 0; i <= 10; i++)
		{
			byte[] body = Body();
			Finger(body, 0, 5, 500 + i * 8, 500);
			mode.Apply(body, Frame);
		}
		int slow = sink.MoveX;
		Assert.Equal((int)Math.Truncate(10 * DesktopMode.TouchPixels(8, 8, DesktopModeSettings.Default)), slow);
		mode.Apply(Body(), Frame);
		Assert.DoesNotContain("Left down", sink.Events); // moved too far for a tap
		sink.Clear();
		for (int i = 0; i <= 1; i++)
		{
			byte[] body = Body();
			Finger(body, 0, 6, 500 + i * 50, 500);
			mode.Apply(body, Frame);
		}
		Assert.True(sink.MoveX > slow, $"fast {sink.MoveX} slow {slow}");
		Assert.True(DesktopMode.TouchPixels(50, 50, DesktopModeSettings.Default) / 50 > DesktopMode.TouchPixels(5, 5, DesktopModeSettings.Default) / 5);
	}

	[Fact]
	public void Touchpad_TapClicks_TwoFingerTapRightClicks()
	{
		(DesktopMode mode, RecordingSink sink) = Active(TouchpadRightClick.TwoFingers);
		byte[] down = Body();
		Finger(down, 0, 1, 900, 500);
		mode.Apply(down, Frame);
		mode.Apply(down, Frame);
		mode.Apply(Body(), Frame);
		Assert.Equal(["Left down", "Left up"], sink.Events);
		sink.Clear();

		byte[] two = Body();
		Finger(two, 0, 2, 800, 500);
		Finger(two, 1, 3, 1100, 500);
		mode.Apply(two, Frame);
		mode.Apply(two, Frame);
		mode.Apply(Body(), Frame);
		Assert.Equal(["Right down", "Right up"], sink.Events);
		sink.Clear();

		// Held too long: no tap.
		for (int i = 0; i < 100; i++)
			mode.Apply(down, Frame);
		mode.Apply(Body(), Frame);
		Assert.Empty(sink.Events);
	}

	[Fact]
	public void Touchpad_TwoFingersMovingScroll()
	{
		(DesktopMode mode, RecordingSink sink) = Active();
		for (int i = 0; i < 20; i++)
		{
			byte[] two = Body();
			Finger(two, 0, 2, 800, 300 + i * 10);
			Finger(two, 1, 3, 1100, 300 + i * 10);
			mode.Apply(two, Frame);
		}
		mode.Apply(Body(), Frame);
		// Fingers moved down 190 counts: scrolls up (natural), no pointer motion, no click.
		Assert.Equal((int)(190 * DesktopMode.TouchScrollPerCount), sink.Wheel);
		Assert.Equal(0, sink.MoveX);
		Assert.Equal(0, sink.MoveY);
		Assert.DoesNotContain("Right down", sink.Events);
	}

	[Fact]
	public void TouchpadClick_LeftOrRightHalf()
	{
		(DesktopMode mode, RecordingSink sink) = Active(TouchpadRightClick.RightSide);
		byte[] left = Body(b2: 0x02);
		Finger(left, 0, 1, 300, 500);
		mode.Apply(left, Frame);
		byte[] lift = Body();
		Finger(lift, 0, 1, 300, 500);
		mode.Apply(lift, Frame);
		mode.Apply(Body(), Frame);
		Assert.Equal(["Left down", "Left up"], sink.Events);
		sink.Clear();
		byte[] right = Body(b2: 0x02);
		Finger(right, 0, 4, 1500, 500);
		mode.Apply(right, Frame);
		mode.Apply(Body(), Frame);
		Assert.Equal(["Right down", "Right up"], sink.Events);
	}

	// One touchpad report: a finger (slot 0) at x, y, the click bit when pressed.
	private static byte[] Pad(int x, int y, bool pressed, byte id = 1)
	{
		byte[] body = Body(b2: (byte)(pressed ? 0x02 : 0));
		Finger(body, 0, id, x, y);
		return body;
	}

	private static List<string> Clicks(RecordingSink sink) => sink.Events.Where(e => e != "move").ToList();

	// A resting finger's position jitters by a few counts on every report: a slow tap (200 ms, 50 reports) is still a tap.
	// The tap's travel used to be the path length, which the jitter alone pushed past TapMaxTravel.
	[Fact]
	public void Tap_RestingFingerJitter_StillClicks()
	{
		(DesktopMode mode, RecordingSink sink) = Active();
		for (int i = 0; i < 50; i++)
			mode.Apply(Pad(900 + (i % 2 == 0 ? 4 : -4), 500 + (i % 3 == 0 ? 3 : -3), false), Frame);
		mode.Apply(Body(), Frame);
		Assert.Equal(["Left down", "Left up"], Clicks(sink));

		// Moved away for real (more than TapMaxTravel from where it came down): no tap.
		sink.Clear();
		for (int i = 0; i < 10; i++)
			mode.Apply(Pad(900 + i * 10, 500, false, id: 2), Frame);
		mode.Apply(Body(), Frame);
		Assert.Empty(Clicks(sink));
	}

	// The finger rolls as it presses the pad: the pointer stays where it was aimed from the press to the release, so the
	// click is a click and not a small drag (which loses it on links, icons and text). Exactly one click, no tap on top.
	[Fact]
	public void TouchpadClick_FingerRoll_PointerStaysStill_OneClick()
	{
		(DesktopMode mode, RecordingSink sink) = Active();
		mode.Apply(Pad(600, 500, false), Frame);
		mode.Apply(Pad(605, 500, false), Frame);
		int x = 605;
		for (int i = 0; i < 6; i++)
		{
			x += 12;
			mode.Apply(Pad(x, 500 + i * 5, true), Frame);
		}
		x += 12;
		mode.Apply(Pad(x, 530, false), Frame);
		mode.Apply(Body(), Frame);
		int down = sink.Events.IndexOf("Left down"), up = sink.Events.IndexOf("Left up");
		Assert.True(down >= 0 && up > down);
		Assert.DoesNotContain("move", sink.Events.Skip(down).Take(up - down));
		Assert.Equal(["Left down", "Left up"], Clicks(sink));
	}

	// Held and moved past ClickDragTravel: the pointer follows (drag-select, moving a window), from there on.
	[Fact]
	public void TouchpadClick_HeldAndMovedFar_Drags()
	{
		(DesktopMode mode, RecordingSink sink) = Active();
		mode.Apply(Pad(400, 500, false), Frame);
		for (int i = 1; i <= 20; i++)
			mode.Apply(Pad(400 + i * 20, 500, true), Frame);
		mode.Apply(Pad(800, 500, false), Frame);
		mode.Apply(Body(), Frame);
		int down = sink.Events.IndexOf("Left down"), up = sink.Events.IndexOf("Left up");
		Assert.Contains("move", sink.Events.Skip(down).Take(up - down));
		Assert.True(sink.MoveX > 0);
		// Only the movement past the threshold: less than the whole 400 counts.
		Assert.True(sink.MoveX < (int)DesktopMode.TouchPixels(400, 20, DesktopModeSettings.Default), $"{sink.MoveX}");
		Assert.Equal(["Left down", "Left up"], Clicks(sink));
	}

	// The shortest press the controller reports (down in one report, up in the next) is one click; so is a press whose
	// finger is not reported yet in its first report.
	[Fact]
	public void TouchpadClick_ShortestPress_AndPressBeforeTheFinger_OneClickEach()
	{
		(DesktopMode mode, RecordingSink sink) = Active();
		mode.Apply(Pad(500, 500, false), Frame);
		mode.Apply(Pad(500, 500, true), Frame);
		mode.Apply(Pad(500, 500, false), Frame);
		mode.Apply(Body(), Frame);
		Assert.Equal(["Left down", "Left up"], Clicks(sink));

		sink.Clear();
		mode.Apply(Body(b2: 0x02), Frame);
		mode.Apply(Pad(500, 500, true, id: 2), Frame);
		mode.Apply(Pad(500, 500, false, id: 2), Frame);
		mode.Apply(Body(), Frame);
		Assert.Equal(["Left down", "Left up"], Clicks(sink));
	}

	// Two fingers (slots 0 and 1) at x1 and x2, the click bit when pressed. Apply neutralizes the body in place: a new
	// one per report.
	private static byte[] Two(int x1, int x2, bool pressed)
	{
		byte[] two = Body(b2: (byte)(pressed ? 0x02 : 0));
		Finger(two, 0, 2, x1, 500);
		Finger(two, 1, 3, x2, 500);
		return two;
	}

	// One press with two fingers on the pad, then both lift.
	private static void TwoFingerPress(DesktopMode mode, int x1, int x2)
	{
		mode.Apply(Two(x1, x2, false), Frame);
		mode.Apply(Two(x1, x2, true), Frame);
		mode.Apply(Two(x1, x2, false), Frame);
		mode.Apply(Body(), Frame);
	}

	// One press with one finger at x, then it lifts.
	private static void OneFingerPress(DesktopMode mode, int x, byte id = 1)
	{
		mode.Apply(Pad(x, 500, false, id), Frame);
		mode.Apply(Pad(x, 500, true, id), Frame);
		mode.Apply(Pad(x, 500, false, id), Frame);
		mode.Apply(Body(), Frame);
	}

	// Two Fingers: two fingers on the pad when it is pressed are a right click, also on the left half; one finger on the
	// right half is a left click.
	[Fact]
	public void TouchpadClick_TwoFingers_RightClick()
	{
		(DesktopMode mode, RecordingSink sink) = Active(TouchpadRightClick.TwoFingers);
		TwoFingerPress(mode, 300, 600);
		Assert.Equal(["Right down", "Right up"], Clicks(sink));
		sink.Clear();
		OneFingerPress(mode, 1500);
		Assert.Equal(["Left down", "Left up"], Clicks(sink));
	}

	// Right Side: two fingers on the left half are a left click (only the side counts).
	[Fact]
	public void TouchpadClick_RightSide_TwoFingersOnTheLeftHalf_LeftClick()
	{
		(DesktopMode mode, RecordingSink sink) = Active(TouchpadRightClick.RightSide);
		TwoFingerPress(mode, 300, 600);
		Assert.Equal(["Left down", "Left up"], Clicks(sink));
		sink.Clear();
		OneFingerPress(mode, 1500);
		Assert.Equal(["Right down", "Right up"], Clicks(sink));
	}

	// Off (the default): every press is a left click, on either half, with one finger or two.
	[Fact]
	public void TouchpadClick_RightClickOff_EveryPressLeftClicks()
	{
		Assert.Equal(TouchpadRightClick.Off, DesktopModeSettings.Default.RightClick);
		(DesktopMode mode, RecordingSink sink) = Active();
		OneFingerPress(mode, 300);
		OneFingerPress(mode, 1500, id: 2);
		OneFingerPress(mode, TouchPoint.Width - 1, id: 3);
		TwoFingerPress(mode, 300, 600);
		TwoFingerPress(mode, 1200, 1700);
		Assert.Equal(Enumerable.Repeat(new[] { "Left down", "Left up" }, 5).SelectMany(e => e), Clicks(sink));
	}

	// The choice applies at once while mouse mode is on: the next press follows it.
	[Fact]
	public void TouchpadClick_RightClickChoice_AppliesLive()
	{
		(DesktopMode mode, RecordingSink sink) = Active();
		OneFingerPress(mode, 1500);
		mode.Settings = mode.Settings with { RightClick = TouchpadRightClick.RightSide };
		OneFingerPress(mode, 1500, id: 2);
		mode.Settings = mode.Settings with { RightClick = TouchpadRightClick.TwoFingers };
		OneFingerPress(mode, 1500, id: 3);
		TwoFingerPress(mode, 300, 600);
		mode.Settings = mode.Settings with { RightClick = TouchpadRightClick.Off };
		TwoFingerPress(mode, 300, 600);
		Assert.Equal(["Left down", "Left up", "Right down", "Right up", "Left down", "Left up", "Right down", "Right up", "Left down", "Left up"],
			Clicks(sink));
	}

	// A click held while the choice changes comes up as the button it went down as.
	[Fact]
	public void TouchpadClick_ChoiceChangedWhileHeld_ReleasesTheSameButton()
	{
		(DesktopMode mode, RecordingSink sink) = Active(TouchpadRightClick.RightSide);
		mode.Apply(Pad(1500, 500, true), Frame);
		mode.Settings = mode.Settings with { RightClick = TouchpadRightClick.Off };
		mode.Apply(Pad(1500, 500, false), Frame);
		mode.Apply(Body(), Frame);
		Assert.Equal(["Right down", "Right up"], Clicks(sink));
	}

	// Off: two quick presses anywhere are a double click, the second landing somewhere else (the other half, or with two
	// fingers): two left clicks, the pointer still from the first press to the last release, also when the finger rolls
	// as the click comes up.
	[Fact]
	public void TouchpadClick_RightClickOff_TwoQuickPressesAnywhere_DoubleClick()
	{
		foreach (bool twoFingers in new[] { false, true })
		{
			(DesktopMode mode, RecordingSink sink) = Active();
			mode.Apply(Pad(300, 500, false), Frame);
			mode.Apply(Pad(300, 500, true), Frame);
			mode.Apply(Pad(306, 503, false), Frame);
			mode.Apply(Pad(318, 509, false), Frame);
			mode.Apply(Body(), Frame);
			if (twoFingers)
			{
				TwoFingerPress(mode, 1200, 1700);
			}
			else
			{
				mode.Apply(Pad(1700, 800, false, id: 2), Frame);
				mode.Apply(Pad(1700, 800, true, id: 2), Frame);
				mode.Apply(Pad(1690, 806, false, id: 2), Frame);
				mode.Apply(Body(), Frame);
			}
			Assert.Equal(["Left down", "Left up", "Left down", "Left up"], Clicks(sink));
			int first = sink.Events.IndexOf("Left down"), last = sink.Events.LastIndexOf("Left up");
			Assert.DoesNotContain("move", sink.Events.Skip(first).Take(last - first + 1));
			Assert.Equal(0, sink.Wheel);
		}
	}

	// A two-finger tap right clicks only with Two Fingers; with Off and Right Side it is a left click, like every tap
	// (taps don't go by side: a tap on the right half is a left click too).
	[Theory]
	[InlineData(TouchpadRightClick.Off, "Left")]
	[InlineData(TouchpadRightClick.RightSide, "Left")]
	[InlineData(TouchpadRightClick.TwoFingers, "Right")]
	public void Tap_TwoFingers_RightClicksOnlyWithTwoFingers(TouchpadRightClick choice, string twoFingerButton)
	{
		(DesktopMode mode, RecordingSink sink) = Active(choice);
		mode.Apply(Two(1200, 1700, false), Frame);
		mode.Apply(Two(1200, 1700, false), Frame);
		mode.Apply(Body(), Frame);
		Assert.Equal([$"{twoFingerButton} down", $"{twoFingerButton} up"], Clicks(sink));
		sink.Clear();
		mode.Apply(Pad(1700, 500, false), Frame);
		mode.Apply(Pad(1700, 500, false), Frame);
		mode.Apply(Body(), Frame);
		Assert.Equal(["Left down", "Left up"], Clicks(sink));
	}

	// After the click comes up the pointer stays still only for ReleaseHold: a finger left on the pad moves it after.
	[Fact]
	public void TouchpadClick_AfterReleaseHold_TheFingerMovesThePointer()
	{
		(DesktopMode mode, RecordingSink sink) = Active();
		mode.Apply(Pad(600, 500, false), Frame);
		mode.Apply(Pad(600, 500, true), Frame);
		mode.Apply(Pad(600, 500, false), Frame);
		for (int i = 0; i < 6; i++)
			mode.Apply(Pad(600, 500, false), TimeSpan.FromMilliseconds(50));
		Assert.DoesNotContain("move", sink.Events);
		for (int i = 1; i <= 5; i++)
			mode.Apply(Pad(600 + i * 8, 500, false), Frame);
		Assert.True(sink.MoveX > 0, $"{sink.MoveX}");
		mode.Apply(Body(), Frame);
		Assert.Equal(["Left down", "Left up"], Clicks(sink));
	}

	// Two quick presses on the left half: two left clicks with the pointer still in between (Windows' double click),
	// even when the finger rolls a little on each press.
	[Fact]
	public void TouchpadClick_TwoQuickPresses_DoubleClick()
	{
		(DesktopMode mode, RecordingSink sink) = Active();
		mode.Apply(Pad(700, 500, false), Frame);
		mode.Apply(Pad(700, 500, true), Frame);
		mode.Apply(Pad(708, 504, true), Frame);
		mode.Apply(Pad(708, 504, false), Frame);
		mode.Apply(Pad(708, 504, true), Frame);
		mode.Apply(Pad(700, 500, true), Frame);
		mode.Apply(Pad(700, 500, false), Frame);
		mode.Apply(Body(), Frame);
		Assert.Equal(["Left down", "Left up", "Left down", "Left up"], sink.Events.Where(e => e != "move").ToList());
		int first = sink.Events.IndexOf("Left down"), last = sink.Events.LastIndexOf("Left up");
		Assert.DoesNotContain("move", sink.Events.Skip(first).Take(last - first));
	}

	// The hint's press part follows the Right Click choice.
	[Fact]
	public void TouchpadHint_FollowsTheRightClickChoice()
	{
		Assert.Contains("; dokunmak ya da basmak sol tık.", Text.DesktopTouchpadHint(TouchpadRightClick.Off, Language.Turkish));
		Assert.Contains("; tap or press to left click.", Text.DesktopTouchpadHint(TouchpadRightClick.Off, Language.English));
		Assert.DoesNotContain("sağ tık", Text.DesktopTouchpadHint(TouchpadRightClick.Off, Language.Turkish));
		Assert.DoesNotContain("right click", Text.DesktopTouchpadHint(TouchpadRightClick.Off, Language.English));
		Assert.Contains("dokunmak sol tık; sol yarıya basmak sol tık, sağ yarıya basmak sağ tık", Text.DesktopTouchpadHint(TouchpadRightClick.RightSide, Language.Turkish));
		Assert.Contains("tap to left click; press the left half to left click, the right half to right click", Text.DesktopTouchpadHint(TouchpadRightClick.RightSide, Language.English));
		Assert.Contains("iki parmakla sağ tık", Text.DesktopTouchpadHint(TouchpadRightClick.TwoFingers, Language.Turkish));
		Assert.Contains("tap or press with one finger to left click, with two to right click", Text.DesktopTouchpadHint(TouchpadRightClick.TwoFingers, Language.English));
		Assert.Equal(Text.DesktopTouchpadHint(TouchpadRightClick.Off, Language.English), Text.DesktopTouchpadHint(l: Language.English));
	}

	[Fact]
	public void ToggleCombo_IsMaskedFromTheGame_AndFires()
	{
		var shortcuts = new ShortcutProcessor();
		var fired = new List<ShortcutAction>();
		shortcuts.Triggered += fired.Add;
		var desktop = new DesktopMode(new RecordingSink());
		shortcuts.Triggered += a =>
		{
			if (a == ShortcutAction.ToggleDesktopMode)
				desktop.Active = !desktop.Active;
		};
		byte[] ps = Body(b2: 0x01);
		shortcuts.Apply(ps);
		byte[] combo = Body(b1: 0x20, b2: 0x01);
		shortcuts.Apply(combo);
		Assert.Equal([ShortcutAction.ToggleDesktopMode], fired);
		Assert.Equal(0, combo[8] & 0x20);
		Assert.Equal(0, combo[9] & 0x01);
		Assert.True(desktop.Active);
		// Options still held after PS is let go: still hidden, and desktop mode doesn't press the Windows key.
		byte[] after = Body(b1: 0x20);
		shortcuts.Apply(after);
		Assert.Equal(0, after[8] & 0x20);
		var sink = (RecordingSink)desktop.Sink;
		desktop.Apply(after, Frame);
		Assert.Empty(sink.Events);
	}

	[Fact]
	public void GyroMouse_MovesThePointer_AndLeavesTheStick()
	{
		var gyro = new GyroSettings { Mode = GyroMode.Always, Target = GyroTarget.Mouse, Deadzone = 0 };
		var transform = new InputTransform(InputTuning.Default, null, gyro);
		var sink = new RecordingSink();
		var mouse = new GyroMouse(sink);
		for (int i = 0; i < 250; i++)
		{
			byte[] body = Body();
			// Yaw left 60 deg/s (positive): pointer moves left.
			BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(GyroAim.GyroY), (short)(60 * GyroAim.CountsPerDegPerSec));
			transform.Apply(body);
			Assert.Equal(128, body[2]);
			Assert.Equal(128, body[3]);
			mouse.Apply(transform.Gyro, body, Frame);
		}
		Assert.InRange(sink.MoveX, -60 * GyroMouse.PixelsPerDegree - 2, -60 * GyroMouse.PixelsPerDegree + 2);
		Assert.Equal(0, sink.MoveY);

		// Right stick target: the stick moves, the mouse doesn't.
		var stick = new InputTransform(InputTuning.Default, null, gyro with { Target = GyroTarget.RightStick });
		sink.Clear();
		byte[] b = Body();
		BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(GyroAim.GyroY), (short)(60 * GyroAim.CountsPerDegPerSec));
		stick.Apply(b);
		mouse.Apply(stick.Gyro, b, Frame);
		Assert.True(b[2] < 128);
		Assert.Equal(0, sink.MoveX);
	}

	[Fact]
	public void GyroMouse_FollowsTheActivationButton()
	{
		var gyro = new GyroSettings { Mode = GyroMode.WhileR1, Target = GyroTarget.Mouse, Deadzone = 0 };
		var transform = new InputTransform(InputTuning.Default, null, gyro);
		var sink = new RecordingSink();
		var mouse = new GyroMouse(sink);
		byte[] body = Body();
		BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(GyroAim.GyroX), 2000);
		for (int i = 0; i < 20; i++)
		{
			byte[] copy = (byte[])body.Clone();
			transform.Apply(copy);
			mouse.Apply(transform.Gyro, copy, Frame);
		}
		Assert.Equal(0, sink.MoveY);
		body[8] = 0x02;
		for (int i = 0; i < 20; i++)
		{
			byte[] copy = (byte[])body.Clone();
			transform.Apply(copy);
			mouse.Apply(transform.Gyro, copy, Frame);
		}
		Assert.True(sink.MoveY < 0, $"pitch up moves the pointer up ({sink.MoveY})");
	}

	[Fact]
	public void Settings_RoundTrip_AndOldGyroDefaultsToTheStick()
	{
		var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
		var desktop = new DesktopModeSettings { PointerSpeed = 1.7, PointerAcceleration = 20, TouchpadSpeed = 0.8, ScrollSpeed = 2.5, AutoOffInGame = false };
		Assert.Equal(desktop, JsonSerializer.Deserialize<DesktopModeSettings>(JsonSerializer.Serialize(desktop, options), options));
		var gyro = new GyroSettings { Mode = GyroMode.Always, Target = GyroTarget.Mouse };
		Assert.Equal(gyro, JsonSerializer.Deserialize<GyroSettings>(JsonSerializer.Serialize(gyro, options), options));
		Assert.Equal(GyroTarget.RightStick, JsonSerializer.Deserialize<GyroSettings>("{\"Mode\":\"Always\"}", options)!.Target);
		Assert.Equal(DesktopModeSettings.Default, JsonSerializer.Deserialize<DesktopModeSettings>("{}", options));
	}

	// Right Click is saved by name; missing (older files) or unknown: Off.
	[Fact]
	public void RightClick_RoundTrips_AndDefaultsToOff()
	{
		foreach (TouchpadRightClick choice in Enum.GetValues<TouchpadRightClick>())
		{
			var desktop = DesktopModeSettings.Default with { RightClick = choice, PointerSpeed = 1.4 };
			string json = JsonSerializer.Serialize(desktop);
			Assert.Contains($"\"RightClick\":\"{choice}\"", json);
			Assert.Equal(desktop, JsonSerializer.Deserialize<DesktopModeSettings>(json));
		}
		Assert.Equal(TouchpadRightClick.Off, JsonSerializer.Deserialize<DesktopModeSettings>("{\"PointerSpeed\":2}")!.RightClick);
		Assert.Equal(TouchpadRightClick.Off, JsonSerializer.Deserialize<DesktopModeSettings>("{\"RightClick\":\"Middle\"}")!.RightClick);
		Assert.Equal(TouchpadRightClick.TwoFingers, JsonSerializer.Deserialize<DesktopModeSettings>("{\"RightClick\":\"twofingers\"}")!.RightClick);
	}

	[Fact]
	public void Texts_Exist_InBothLanguages()
	{
		foreach (Language l in new[] { Language.Turkish, Language.English })
		{
			Assert.NotEmpty(Text.DesktopModeSection(l));
			Assert.Equal(DesktopButtonMap.Actions.Count, DesktopButtonMap.Actions.Select(a => Text.DesktopActionName(a, l)).Distinct().Count());
			Assert.NotEqual(Text.DesktopStickLeft(l), Text.DesktopStickRight(l));
			Assert.Equal(3, Enum.GetValues<TouchpadRightClick>().Select(m => Text.DesktopRightClickOption(m, l)).Distinct().Count());
			Assert.NotEmpty(Text.DesktopRightClick(l));
			Assert.NotEqual(Text.GyroTargetName(GyroTarget.Mouse, l), Text.GyroTargetName(GyroTarget.RightStick, l));
		}
		Assert.Equal("Fare Modu Açık", Text.DesktopOnTitle(Language.Turkish));
		Assert.Equal("Mouse Mode Off", Text.DesktopOffTitle(Language.English));
	}
}
