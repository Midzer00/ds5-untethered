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

	private static (DesktopMode Mode, RecordingSink Sink) Active()
	{
		var sink = new RecordingSink();
		var mode = new DesktopMode(sink) { Active = true };
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
		(DesktopMode mode, RecordingSink sink) = Active();
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
		(DesktopMode mode, RecordingSink sink) = Active();
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

	[Fact]
	public void Texts_Exist_InBothLanguages()
	{
		foreach (Language l in new[] { Language.Turkish, Language.English })
		{
			Assert.NotEmpty(Text.DesktopModeSection(l));
			Assert.Equal(9, Text.DesktopMapping(l).Count);
			Assert.NotEqual(Text.GyroTargetName(GyroTarget.Mouse, l), Text.GyroTargetName(GyroTarget.RightStick, l));
		}
		Assert.Equal("Fare Modu Açık", Text.DesktopOnTitle(Language.Turkish));
		Assert.Equal("Mouse Mode Off", Text.DesktopOffTitle(Language.English));
	}
}
