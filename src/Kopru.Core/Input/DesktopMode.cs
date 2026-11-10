// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Input;

// Desktop mode tuning. Speeds are multipliers (1.0 = default); PointerAcceleration 0-100 % bends the right stick's
// response (0 = linear, 100 = cubic); AutoOffInGame turns desktop mode off when a known game comes to the front.
public sealed record DesktopModeSettings
{
	public static DesktopModeSettings Default { get; } = new();

	public double PointerSpeed { get; init; } = 1.0;

	public double PointerAcceleration { get; init; } = 50;

	public double TouchpadSpeed { get; init; } = 1.0;

	public double ScrollSpeed { get; init; } = 1.0;

	public bool AutoOffInGame { get; init; } = true;
}

// Desktop mode: while Active, the controller drives the Windows desktop through an IInputSink and the 63-byte input
// body is replaced by a neutral one (sticks centred, triggers released, no buttons, no touches), so games don't react.
//
// Mapping:
//   touchpad      one finger moves the pointer (with acceleration); tap = left click; two-finger tap = right click;
//                 two fingers moving up/down = scroll; touchpad click = left click (left half) / right click (right half)
//   right stick   pointer (speed and acceleration curve)          left stick   scroll (vertical and horizontal)
//   Cross         left mouse button (held = drag)                 Circle       right mouse button
//   Square        Enter                                           Triangle     on-screen keyboard (Win+Ctrl+O)
//   Options       Windows key (Start)                             Create       media play/pause
//   D-pad         arrow keys                                      L3 Esc,      R3 middle mouse button
//   R1 / L1       Alt+Tab / Alt+Shift+Tab (Alt stays down SwitcherHold after the last press, Cross picks at once)
//   L2 / R2       volume down / up
//   PS and Mute do nothing (PS combos are handled before, by ShortcutProcessor).
//
// Apply runs on the input thread only; Active and Settings may be set from any thread. Turning Active off releases every
// key and button still held on the next Apply.
public sealed class DesktopMode
{
	public const int MinBodySize = 40;
	public const int Buttons0 = 7, Buttons1 = 8, Buttons2 = 9, Touch1 = 32, Touch2 = 36;

	// Right stick at full deflection with PointerSpeed 1.0, pixels per second.
	public const double PointerPixelsPerSecond = 1400;
	public const double StickDeadzone = 0.15;

	// Left stick at full deflection: wheel notches per second.
	public const double ScrollNotchesPerSecond = 14;
	public const int WheelDelta = 120;

	// Touchpad counts (1920 x 1080 pad) to pixels at TouchpadSpeed 1.0, before acceleration.
	public const double TouchGain = 0.6;
	// Two-finger scroll: wheel units per touchpad count of finger movement.
	public const double TouchScrollPerCount = 1.5;

	public static readonly TimeSpan TapMaxLength = TimeSpan.FromMilliseconds(250);
	public const int TapMaxTravel = 60;
	public static readonly TimeSpan SwitcherHold = TimeSpan.FromMilliseconds(700);

	private static readonly (PadButtons Button, ushort Key)[] HeldKeys =
	[
		(PadButtons.DpadUp, VirtualKeys.Up), (PadButtons.DpadDown, VirtualKeys.Down), (PadButtons.DpadLeft, VirtualKeys.Left),
		(PadButtons.DpadRight, VirtualKeys.Right), (PadButtons.Square, VirtualKeys.Enter), (PadButtons.L3, VirtualKeys.Escape)
	];

	private static readonly (PadButtons Button, ushort Key)[] TapKeys =
	[
		(PadButtons.Options, VirtualKeys.LeftWindows), (PadButtons.Create, VirtualKeys.MediaPlayPause),
		(PadButtons.L2, VirtualKeys.VolumeDown), (PadButtons.R2, VirtualKeys.VolumeUp)
	];

	private static readonly (PadButtons Button, MouseButton Mouse)[] MouseButtons =
	[
		(PadButtons.Cross, MouseButton.Left), (PadButtons.Circle, MouseButton.Right), (PadButtons.R3, MouseButton.Middle)
	];

	private readonly TimeProvider time;
	private volatile bool active;
	private volatile DesktopModeSettings settings = DesktopModeSettings.Default;
	private volatile IInputSink sink;

	private bool engaged, fresh;
	private long lastTimestamp;
	private bool hasTimestamp;
	private double clock;
	private PadButtons previous;
	private PadButtons crossSwallowed;

	// Held keys / mouse buttons, released when leaving.
	private readonly List<ushort> keysDown = [];
	private readonly List<MouseButton> mouseDown = [];

	// Pointer and scroll remainders (sub-pixel / sub-unit).
	private double remX, remY, wheelRem, hwheelRem;

	// Window switcher: Alt held until SwitcherHold after the last L1/R1 press.
	private bool altDown;
	private double switcherSince;

	// Touchpad.
	private bool touching, touchClicked, hasLast, hasLastPair;
	private int lastX, lastY, lastId, maxFingers;
	private double lastPairY, touchStart, travel;
	private bool padClickDown;
	private MouseButton padClickButton;

	public DesktopMode(IInputSink? sink = null, TimeProvider? time = null)
	{
		this.sink = sink ?? NullInputSink.Instance;
		this.time = time ?? TimeProvider.System;
	}

	public bool Active
	{
		get => active;
		set
		{
			if (value && !active)
			{
				fresh = true;
			}
			active = value;
		}
	}

	public DesktopModeSettings Settings
	{
		get => settings;
		set => settings = value ?? DesktopModeSettings.Default;
	}

	public IInputSink Sink
	{
		get => sink;
		set => sink = value ?? NullInputSink.Instance;
	}

	// Right stick (-1..1 per axis, Y down) to pointer velocity in pixels per second.
	public static (double X, double Y) PointerVelocity(double x, double y, DesktopModeSettings settings)
	{
		double n = StickAmount(x, y, out double r);
		if (n <= 0)
		{
			return (0, 0);
		}
		double exponent = 1 + 2 * Math.Clamp(settings.PointerAcceleration, 0, 100) / 100;
		double speed = PointerPixelsPerSecond * Math.Max(0, settings.PointerSpeed) * Math.Pow(n, exponent);
		return (x / r * speed, y / r * speed);
	}

	// Touchpad movement (counts per report) to pixels: a fast swipe goes further than a slow one.
	public static double TouchPixels(double delta, double magnitude, DesktopModeSettings settings) =>
		delta * TouchGain * Math.Max(0, settings.TouchpadSpeed) * (1 + Math.Min(magnitude / 30, 2) * 0.5);

	// Applies one input body with the time since the last one measured here.
	public bool Apply(Span<byte> body)
	{
		long now = time.GetTimestamp();
		TimeSpan elapsed = hasTimestamp ? time.GetElapsedTime(lastTimestamp, now) : TimeSpan.Zero;
		lastTimestamp = now;
		hasTimestamp = true;
		return Apply(body, elapsed);
	}

	// True when desktop mode handled the body (it is neutral now); false when it passed untouched.
	public bool Apply(Span<byte> body, TimeSpan elapsed)
	{
		if (body.Length < MinBodySize)
		{
			return false;
		}
		IInputSink output = sink;
		if (!active)
		{
			if (engaged)
			{
				ReleaseAll(output);
			}
			return false;
		}
		DesktopModeSettings s = settings;
		double dt = Math.Clamp(elapsed.TotalSeconds, 0, 0.05);
		clock += dt;
		PadButtons buttons = InputSnapshot.DecodeButtons(body[Buttons0], body[Buttons1], body[Buttons2]);
		if (!engaged || fresh)
		{
			// Buttons already down when desktop mode starts do nothing until released.
			if (engaged)
			{
				ReleaseAll(output);
			}
			engaged = true;
			fresh = false;
			previous = buttons;
			crossSwallowed = PadButtons.None;
			ResetTouch(body);
		}
		Buttons(output, buttons);
		Sticks(output, body, dt, s);
		Touch(output, body, buttons, s);
		previous = buttons;
		Neutralize(body);
		return true;
	}

	// A body games see while desktop mode is on: sticks centred, triggers out, no buttons, no fingers.
	public static void Neutralize(Span<byte> body)
	{
		body[0] = body[1] = body[2] = body[3] = 128;
		body[4] = body[5] = 0;
		body[Buttons0] = 0x08;
		body[Buttons1] = 0;
		body[Buttons2] = 0;
		if (body.Length >= MinBodySize)
		{
			body[Touch1] |= 0x80;
			body[Touch2] |= 0x80;
		}
	}

	private bool Pressed(PadButtons now, PadButtons b) => (now & b) != 0 && (previous & b) == 0;

	private bool Released(PadButtons now, PadButtons b) => (now & b) == 0 && (previous & b) != 0;

	private void Buttons(IInputSink output, PadButtons now)
	{
		// Window switcher first: Cross while it is open picks the window instead of clicking.
		bool forward = Pressed(now, PadButtons.R1), back = Pressed(now, PadButtons.L1);
		if (forward || back)
		{
			if (!altDown)
			{
				output.Key(VirtualKeys.Alt, true);
				altDown = true;
			}
			if (back)
			{
				output.Key(VirtualKeys.Shift, true);
			}
			output.Key(VirtualKeys.Tab, true);
			output.Key(VirtualKeys.Tab, false);
			if (back)
			{
				output.Key(VirtualKeys.Shift, false);
			}
			switcherSince = clock;
		}
		if (altDown)
		{
			if (Pressed(now, PadButtons.Cross))
			{
				crossSwallowed = PadButtons.Cross;
				CloseSwitcher(output);
			}
			else if ((now & (PadButtons.L1 | PadButtons.R1)) == 0 && clock - switcherSince >= SwitcherHold.TotalSeconds)
			{
				CloseSwitcher(output);
			}
			else if ((now & (PadButtons.L1 | PadButtons.R1)) != 0)
			{
				switcherSince = clock;
			}
		}
		if ((now & crossSwallowed) == 0)
		{
			crossSwallowed = PadButtons.None;
		}

		foreach ((PadButtons b, MouseButton m) in MouseButtons)
		{
			if ((b & crossSwallowed) != 0)
			{
				continue;
			}
			if (Pressed(now, b))
			{
				output.MouseButton(m, true);
				mouseDown.Add(m);
			}
			else if (Released(now, b) && mouseDown.Remove(m))
			{
				output.MouseButton(m, false);
			}
		}
		foreach ((PadButtons b, ushort key) in HeldKeys)
		{
			if (Pressed(now, b))
			{
				output.Key(key, true);
				keysDown.Add(key);
			}
			else if (Released(now, b) && keysDown.Remove(key))
			{
				output.Key(key, false);
			}
		}
		foreach ((PadButtons b, ushort key) in TapKeys)
		{
			if (Pressed(now, b))
			{
				output.Key(key, true);
				output.Key(key, false);
			}
		}
		if (Pressed(now, PadButtons.Triangle))
		{
			// Win+Ctrl+O switches the on-screen keyboard on and off.
			output.Key(VirtualKeys.LeftWindows, true);
			output.Key(VirtualKeys.Control, true);
			output.Key(VirtualKeys.O, true);
			output.Key(VirtualKeys.O, false);
			output.Key(VirtualKeys.Control, false);
			output.Key(VirtualKeys.LeftWindows, false);
		}
	}

	private void CloseSwitcher(IInputSink output)
	{
		if (altDown)
		{
			output.Key(VirtualKeys.Alt, false);
			altDown = false;
		}
	}

	private void Sticks(IInputSink output, ReadOnlySpan<byte> body, double dt, DesktopModeSettings s)
	{
		(double vx, double vy) = PointerVelocity(Axis(body[2]), Axis(body[3]), s);
		remX += vx * dt;
		remY += vy * dt;
		int px = (int)Math.Truncate(remX), py = (int)Math.Truncate(remY);
		remX -= px;
		remY -= py;
		if (px != 0 || py != 0)
		{
			output.MoveMouse(px, py);
		}
		double lx = Axis(body[0]), ly = Axis(body[1]);
		double n = StickAmount(lx, ly, out double r);
		if (n <= 0)
		{
			wheelRem = hwheelRem = 0;
			return;
		}
		double rate = ScrollNotchesPerSecond * WheelDelta * Math.Max(0, s.ScrollSpeed) * n * n;
		// Stick up (Y < 0) scrolls up (positive wheel).
		wheelRem += -ly / r * rate * dt;
		hwheelRem += lx / r * rate * dt;
		int w = (int)Math.Truncate(wheelRem), h = (int)Math.Truncate(hwheelRem);
		wheelRem -= w;
		hwheelRem -= h;
		if (w != 0)
		{
			output.Wheel(w);
		}
		if (h != 0)
		{
			output.Wheel(h, horizontal: true);
		}
	}

	private void ResetTouch(ReadOnlySpan<byte> body)
	{
		// A finger already on the pad when desktop mode starts doesn't make a tap.
		TouchPoint f1 = TouchPoint.Parse(body[Touch1..]), f2 = TouchPoint.Parse(body[Touch2..]);
		touching = f1.Active || f2.Active;
		touchClicked = touching;
		hasLast = hasLastPair = false;
		padClickDown = false;
		travel = 0;
	}

	private void Touch(IInputSink output, ReadOnlySpan<byte> body, PadButtons buttons, DesktopModeSettings s)
	{
		TouchPoint f1 = TouchPoint.Parse(body[Touch1..]), f2 = TouchPoint.Parse(body[Touch2..]);
		int fingers = (f1.Active ? 1 : 0) + (f2.Active ? 1 : 0);
		bool click = (buttons & PadButtons.Touchpad) != 0;
		if (click && (previous & PadButtons.Touchpad) == 0)
		{
			TouchPoint at = f1.Active ? f1 : f2;
			padClickButton = at.Active && at.X >= TouchPoint.Width / 2 ? MouseButton.Right : MouseButton.Left;
			output.MouseButton(padClickButton, true);
			mouseDown.Add(padClickButton);
			padClickDown = true;
			touchClicked = true;
		}
		else if (!click && padClickDown)
		{
			padClickDown = false;
			if (mouseDown.Remove(padClickButton))
			{
				output.MouseButton(padClickButton, false);
			}
		}

		if (fingers == 0)
		{
			if (touching)
			{
				touching = false;
				if (!touchClicked && clock - touchStart <= TapMaxLength.TotalSeconds && travel <= TapMaxTravel)
				{
					MouseButton tap = maxFingers >= 2 ? MouseButton.Right : MouseButton.Left;
					output.MouseButton(tap, true);
					output.MouseButton(tap, false);
				}
			}
			hasLast = hasLastPair = false;
			return;
		}
		if (!touching)
		{
			touching = true;
			touchClicked = click;
			touchStart = clock;
			travel = 0;
			maxFingers = 0;
			hasLast = hasLastPair = false;
		}
		maxFingers = Math.Max(maxFingers, fingers);
		if (fingers == 1)
		{
			TouchPoint p = f1.Active ? f1 : f2;
			if (hasLast && p.Id == lastId)
			{
				int dx = p.X - lastX, dy = p.Y - lastY;
				double magnitude = Math.Sqrt(dx * dx + dy * dy);
				travel += magnitude;
				remX += TouchPixels(dx, magnitude, s);
				remY += TouchPixels(dy, magnitude, s);
				int px = (int)Math.Truncate(remX), py = (int)Math.Truncate(remY);
				remX -= px;
				remY -= py;
				if (px != 0 || py != 0)
				{
					output.MoveMouse(px, py);
				}
			}
			lastX = p.X;
			lastY = p.Y;
			lastId = p.Id;
			hasLast = true;
			hasLastPair = false;
		}
		else
		{
			double y = (f1.Y + f2.Y) / 2.0;
			if (hasLastPair)
			{
				double dy = y - lastPairY;
				travel += Math.Abs(dy);
				// Natural scrolling: fingers moving down show what is above (scroll up).
				wheelRem += dy * TouchScrollPerCount * Math.Max(0, s.ScrollSpeed);
				int w = (int)Math.Truncate(wheelRem);
				wheelRem -= w;
				if (w != 0)
				{
					output.Wheel(w);
				}
			}
			lastPairY = y;
			hasLastPair = true;
			hasLast = false;
		}
	}

	private void ReleaseAll(IInputSink output)
	{
		foreach (ushort key in keysDown)
		{
			output.Key(key, false);
		}
		keysDown.Clear();
		foreach (MouseButton m in mouseDown)
		{
			output.MouseButton(m, false);
		}
		mouseDown.Clear();
		CloseSwitcher(output);
		padClickDown = false;
		engaged = false;
		remX = remY = wheelRem = hwheelRem = 0;
		touching = hasLast = hasLastPair = false;
	}

	// 128 (the resting value) is exactly 0.
	private static double Axis(byte raw) => Math.Clamp((raw - 128) / 127.0, -1, 1);

	// 0..1 past the deadzone.
	private static double StickAmount(double x, double y, out double r)
	{
		r = Math.Sqrt(x * x + y * y);
		return r <= StickDeadzone ? 0 : Math.Min((r - StickDeadzone) / (1 - StickDeadzone), 1);
	}
}
