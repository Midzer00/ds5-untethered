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

	// Which stick moves the pointer (the other scrolls); missing in older files: the right stick.
	[System.Text.Json.Serialization.JsonConverter(typeof(LenientEnumConverter<PointerStick>))]
	public PointerStick PointerStick { get; init; } = PointerStick.Right;

	// What each button does (missing in older files, or a button or action not known: that button's default).
	public DesktopButtonMap Buttons
	{
		get => _buttons;
		init => _buttons = value ?? DesktopButtonMap.Default;
	}

	private readonly DesktopButtonMap _buttons = DesktopButtonMap.Default;
}

// Desktop mode: while Active, the controller drives the Windows desktop through an IInputSink and the 63-byte input
// body is replaced by a neutral one (sticks centred, triggers released, no buttons, no touches), so games don't react.
//
// Mapping (the buttons are Settings.Buttons; the defaults below):
//   touchpad      one finger moves the pointer (with acceleration); tap = left click; two-finger tap = right click;
//                 two fingers moving up/down = scroll; touchpad click = left click (left half) / right click (right half)
//   right stick   pointer (speed and acceleration curve)          left stick   scroll (vertical and horizontal)
//                 (Settings.PointerStick = Left swaps the two)
//   Cross         left mouse button (held = drag)                 Circle       right mouse button
//   Square        Enter                                           Triangle     on-screen keyboard (Win+Ctrl+O)
//   Options       Windows key (Start)                             Create       media play/pause
//   D-pad         arrow keys                                      L3 Esc,      R3 middle mouse button
//   R1 / L1       Alt+Tab / Alt+Shift+Tab (Alt stays down SwitcherHold after the last press; the button on Left Click
//                 picks at once instead of clicking)
//   L2 / R2       volume down / up
//   PS and Mute do nothing (PS combos are handled before, by ShortcutProcessor); touchpad, PS and Mute are not remappable.
// A held action is released with the action it was pressed with; when the mapping changes under a held button, what it
// holds is let go at once and the button does nothing more until it is released.
//
// Apply runs on the input thread only; Active and Settings may be set from any thread. Turning Active off releases every
// key and button still held on the next Apply.
public sealed class DesktopMode
{
	public const int MinBodySize = 40;
	public const int Buttons0 = 7, Buttons1 = 8, Buttons2 = 9, Touch1 = 32, Touch2 = 36;

	// Pointer stick at full deflection with PointerSpeed 1.0, pixels per second.
	public const double PointerPixelsPerSecond = 1400;
	public const double StickDeadzone = 0.15;

	// Scroll stick at full deflection: wheel notches per second.
	public const double ScrollNotchesPerSecond = 14;
	public const int WheelDelta = 120;

	// Touchpad counts (1920 x 1080 pad) to pixels at TouchpadSpeed 1.0, before acceleration.
	public const double TouchGain = 0.6;
	// Two-finger scroll: wheel units per touchpad count of finger movement.
	public const double TouchScrollPerCount = 1.5;

	public static readonly TimeSpan TapMaxLength = TimeSpan.FromMilliseconds(250);
	public const int TapMaxTravel = 60;
	public static readonly TimeSpan SwitcherHold = TimeSpan.FromMilliseconds(700);

	private readonly TimeProvider time;
	private volatile bool active;
	private volatile DesktopModeSettings settings = DesktopModeSettings.Default;
	private volatile IInputSink sink;

	private bool engaged, fresh;
	private long lastTimestamp;
	private bool hasTimestamp;
	private double clock;
	private PadButtons previous;
	// Buttons that do nothing until released (picked a window, or remapped while held).
	private PadButtons swallowed;

	// The action each remappable button was pressed with (None: not pressed, or swallowed).
	private readonly MouseAction[] pressed = new MouseAction[DesktopButtonMap.Bits.Length];

	// How many presses hold each key / mouse button down (two buttons on one action); released when leaving.
	private readonly int[] keyCount = new int[256];
	private readonly int[] mouseCount = new int[3];

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

	// Pointer stick (-1..1 per axis, Y down) to pointer velocity in pixels per second.
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
			swallowed = PadButtons.None;
			ResetTouch(body);
		}
		Buttons(output, buttons, s.Buttons);
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

	private void Buttons(IInputSink output, PadButtons now, DesktopButtonMap map)
	{
		PadButtons[] bits = DesktopButtonMap.Bits;
		// Releases first (with the action the button was pressed with); a held button whose action changed lets go now.
		PadButtons switcherHeld = PadButtons.None;
		for (int i = 0; i < bits.Length; i++)
		{
			PadButtons b = bits[i];
			if ((now & b) == 0)
			{
				swallowed &= ~b;
				if (pressed[i] != MouseAction.None)
				{
					Release(output, pressed[i]);
					pressed[i] = MouseAction.None;
				}
			}
			else if (pressed[i] != MouseAction.None && map.At(i) != pressed[i])
			{
				Release(output, pressed[i]);
				pressed[i] = MouseAction.None;
				swallowed |= b;
			}
			if (pressed[i] is MouseAction.NextWindow or MouseAction.PreviousWindow)
			{
				switcherHeld |= b;
			}
		}

		// Window switcher next: the button on Left Click while it is open picks the window instead of clicking.
		for (int i = 0; i < bits.Length; i++)
		{
			MouseAction action = map.At(i);
			if (action is MouseAction.NextWindow or MouseAction.PreviousWindow && IsNewPress(now, bits[i]))
			{
				Switch(output, back: action == MouseAction.PreviousWindow);
				pressed[i] = action;
				switcherHeld |= bits[i];
			}
		}
		if (altDown)
		{
			int pick = -1;
			for (int i = 0; i < bits.Length && pick < 0; i++)
			{
				if (map.At(i) == MouseAction.LeftClick && IsNewPress(now, bits[i]))
				{
					pick = i;
				}
			}
			if (pick >= 0)
			{
				swallowed |= bits[pick];
				CloseSwitcher(output);
			}
			else if (switcherHeld == PadButtons.None && clock - switcherSince >= SwitcherHold.TotalSeconds)
			{
				CloseSwitcher(output);
			}
			else if (switcherHeld != PadButtons.None)
			{
				switcherSince = clock;
			}
		}

		for (int i = 0; i < bits.Length; i++)
		{
			MouseAction action = map.At(i);
			if (action is not (MouseAction.None or MouseAction.NextWindow or MouseAction.PreviousWindow) && IsNewPress(now, bits[i]))
			{
				Press(output, action);
				pressed[i] = action;
			}
		}
	}

	// Went down in this body and is not being ignored.
	private bool IsNewPress(PadButtons now, PadButtons b) => Pressed(now, b) && (swallowed & b) == 0;

	private void Switch(IInputSink output, bool back)
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

	// The mouse button of a click action, or null.
	private static MouseButton? MouseOf(MouseAction action) => action switch
	{
		MouseAction.LeftClick => MouseButton.Left,
		MouseAction.RightClick => MouseButton.Right,
		MouseAction.MiddleClick => MouseButton.Middle,
		_ => null
	};

	// The key an action holds down while its button is held, or 0.
	private static ushort HeldKeyOf(MouseAction action) => action switch
	{
		MouseAction.Enter => VirtualKeys.Enter,
		MouseAction.Escape => VirtualKeys.Escape,
		MouseAction.Tab => VirtualKeys.Tab,
		MouseAction.Space => VirtualKeys.Space,
		MouseAction.Backspace => VirtualKeys.Backspace,
		MouseAction.Delete => VirtualKeys.Delete,
		MouseAction.ArrowUp => VirtualKeys.Up,
		MouseAction.ArrowDown => VirtualKeys.Down,
		MouseAction.ArrowLeft => VirtualKeys.Left,
		MouseAction.ArrowRight => VirtualKeys.Right,
		_ => 0
	};

	// A one-press action: the key tapped and up to two modifiers held around it (Key 0: not a one-press action).
	private static (ushort Mod1, ushort Mod2, ushort Key) TapOf(MouseAction action) => action switch
	{
		MouseAction.PageUp => (0, 0, VirtualKeys.PageUp),
		MouseAction.PageDown => (0, 0, VirtualKeys.PageDown),
		MouseAction.WindowsKey => (0, 0, VirtualKeys.LeftWindows),
		// Win+Ctrl+O switches the on-screen keyboard on and off.
		MouseAction.OnScreenKeyboard => (VirtualKeys.LeftWindows, VirtualKeys.Control, VirtualKeys.O),
		MouseAction.Back => (VirtualKeys.Alt, 0, VirtualKeys.Left),
		MouseAction.Forward => (VirtualKeys.Alt, 0, VirtualKeys.Right),
		MouseAction.Copy => (VirtualKeys.Control, 0, VirtualKeys.C),
		MouseAction.Paste => (VirtualKeys.Control, 0, VirtualKeys.V),
		MouseAction.VolumeUp => (0, 0, VirtualKeys.VolumeUp),
		MouseAction.VolumeDown => (0, 0, VirtualKeys.VolumeDown),
		MouseAction.MuteSound => (0, 0, VirtualKeys.VolumeMute),
		MouseAction.PlayPause => (0, 0, VirtualKeys.MediaPlayPause),
		MouseAction.NextTrack => (0, 0, VirtualKeys.MediaNextTrack),
		MouseAction.PreviousTrack => (0, 0, VirtualKeys.MediaPreviousTrack),
		_ => (0, 0, 0)
	};

	private void Press(IInputSink output, MouseAction action)
	{
		if (MouseOf(action) is MouseButton m)
		{
			PressMouse(output, m);
			return;
		}
		ushort held = HeldKeyOf(action);
		if (held != 0)
		{
			if (keyCount[held]++ == 0)
			{
				output.Key(held, true);
			}
			return;
		}
		(ushort mod1, ushort mod2, ushort key) = TapOf(action);
		if (key == 0)
		{
			return;
		}
		if (mod1 == VirtualKeys.Alt)
		{
			// Alt+arrow would close the switcher's Alt halfway; close it first.
			CloseSwitcher(output);
		}
		if (mod1 != 0)
		{
			output.Key(mod1, true);
		}
		if (mod2 != 0)
		{
			output.Key(mod2, true);
		}
		output.Key(key, true);
		output.Key(key, false);
		if (mod2 != 0)
		{
			output.Key(mod2, false);
		}
		if (mod1 != 0)
		{
			output.Key(mod1, false);
		}
	}

	private void Release(IInputSink output, MouseAction action)
	{
		if (MouseOf(action) is MouseButton m)
		{
			ReleaseMouse(output, m);
			return;
		}
		ushort held = HeldKeyOf(action);
		if (held != 0 && keyCount[held] > 0 && --keyCount[held] == 0)
		{
			output.Key(held, false);
		}
	}

	private void PressMouse(IInputSink output, MouseButton m)
	{
		if (mouseCount[(int)m]++ == 0)
		{
			output.MouseButton(m, true);
		}
	}

	private void ReleaseMouse(IInputSink output, MouseButton m)
	{
		if (mouseCount[(int)m] > 0 && --mouseCount[(int)m] == 0)
		{
			output.MouseButton(m, false);
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
		// Body bytes 0/1 are the left stick, 2/3 the right one.
		int pointer = s.PointerStick == PointerStick.Left ? 0 : 2, scroll = 2 - pointer;
		(double vx, double vy) = PointerVelocity(Axis(body[pointer]), Axis(body[pointer + 1]), s);
		remX += vx * dt;
		remY += vy * dt;
		int px = (int)Math.Truncate(remX), py = (int)Math.Truncate(remY);
		remX -= px;
		remY -= py;
		if (px != 0 || py != 0)
		{
			output.MoveMouse(px, py);
		}
		double lx = Axis(body[scroll]), ly = Axis(body[scroll + 1]);
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
			PressMouse(output, padClickButton);
			padClickDown = true;
			touchClicked = true;
		}
		else if (!click && padClickDown)
		{
			padClickDown = false;
			ReleaseMouse(output, padClickButton);
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
		for (int vk = 0; vk < keyCount.Length; vk++)
		{
			if (keyCount[vk] > 0)
			{
				keyCount[vk] = 0;
				output.Key((ushort)vk, false);
			}
		}
		for (int m = 0; m < mouseCount.Length; m++)
		{
			if (mouseCount[m] > 0)
			{
				mouseCount[m] = 0;
				output.MouseButton((MouseButton)m, false);
			}
		}
		Array.Clear(pressed);
		swallowed = PadButtons.None;
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
