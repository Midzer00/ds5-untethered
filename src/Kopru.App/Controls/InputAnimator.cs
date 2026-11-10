// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Kopru.Core.Input;

namespace Kopru.App.Controls;

// Live-feedback state of the controller drawing, kept apart from drawing so it can be tested with a fake clock: a
// press shows at full strength at once; a release fades out over FadeOut with an ease-out curve, so short taps stay
// visible. Touch fingers fade the same way at their last position. Nothing here allocates per update.
public sealed class InputAnimator
{
	public static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(140);

	private static readonly TimeSpan LongAgo = TimeSpan.FromDays(-1);
	private readonly TimeSpan[] _releasedAt = new TimeSpan[32];
	private readonly Finger[] _fingers = new Finger[2];
	private uint _pressed;

	private struct Finger
	{
		public bool Active;
		public double X, Y; // 0..1 on the touch surface
		public TimeSpan ReleasedAt;
	}

	public InputAnimator()
	{
		Array.Fill(_releasedAt, LongAgo);
		_fingers[0].ReleasedAt = _fingers[1].ReleasedAt = LongAgo;
	}

	public PadButtons Pressed => (PadButtons)_pressed;

	public void SetButtons(PadButtons pressed, TimeSpan now)
	{
		uint released = _pressed & ~(uint)pressed;
		for (int bit = 0; released != 0; bit++, released >>= 1)
		{
			if ((released & 1) != 0)
			{
				_releasedAt[bit] = now;
			}
		}
		_pressed = (uint)pressed;
	}

	// 0..1 strength of one button's highlight.
	public double Level(PadButtons button, TimeSpan now)
	{
		uint bits = (uint)button;
		if ((_pressed & bits) != 0)
		{
			return 1;
		}
		return bits == 0 ? 0 : Fade(now - _releasedAt[System.Numerics.BitOperations.TrailingZeroCount(bits)]);
	}

	public void SetTouch(int index, TouchPoint point, TimeSpan now)
	{
		ref Finger f = ref _fingers[index];
		if (point.Active)
		{
			f.X = Math.Clamp(point.X, 0, TouchPoint.Width - 1) / (double)(TouchPoint.Width - 1);
			f.Y = Math.Clamp(point.Y, 0, TouchPoint.Height - 1) / (double)(TouchPoint.Height - 1);
		}
		else if (f.Active)
		{
			f.ReleasedAt = now;
		}
		f.Active = point.Active;
	}

	// A finger's position (0..1 on the touch surface) and strength; strength 0 = nothing to draw.
	public (double X, double Y, double Level) Touch(int index, TimeSpan now)
	{
		Finger f = _fingers[index];
		return (f.X, f.Y, f.Active ? 1 : Fade(now - f.ReleasedAt));
	}

	// True while something is fading, i.e. later frames will look different from this one.
	public bool IsAnimating(TimeSpan now)
	{
		for (int bit = 0; bit < 32; bit++)
		{
			if ((_pressed >> bit & 1) == 0 && now - _releasedAt[bit] < FadeOut)
			{
				return true;
			}
		}
		foreach (Finger f in _fingers)
		{
			if (!f.Active && now - f.ReleasedAt < FadeOut)
			{
				return true;
			}
		}
		return false;
	}

	// Ease-out: drops quickly at first, settles softly at zero.
	private static double Fade(TimeSpan since)
	{
		if (since < TimeSpan.Zero || since >= FadeOut)
		{
			return since < TimeSpan.Zero ? 1 : 0;
		}
		double u = 1 - since.TotalMilliseconds / FadeOut.TotalMilliseconds;
		return u * u;
	}

	// Where a stick cap sits for a deflection of -1..1 per axis: proportional, clamped to a circle of radius travel.
	public static Vector StickOffset(double x, double y, double travel)
	{
		x = double.IsFinite(x) ? Math.Clamp(x, -1, 1) : 0;
		y = double.IsFinite(y) ? Math.Clamp(y, -1, 1) : 0;
		double length = Math.Sqrt(x * x + y * y);
		double k = length > 1 ? 1 / length : 1;
		return new Vector(x * k * travel, y * k * travel);
	}

	// How full a trigger is drawn: its analog level (0..100 %), or full when only the digital bit is known.
	public static double TriggerFill(double levelPercent, bool pressedBit)
	{
		double fill = double.IsFinite(levelPercent) ? Math.Clamp(levelPercent / 100, 0, 1) : 0;
		return fill <= 0 && pressedBit ? 1 : fill;
	}
}

// Asks for one more frame from the render loop (TopLevel.RequestAnimationFrame) while an animation runs; never while
// the owner is hidden or detached, and at most one request at a time. No timers: when nothing animates, nothing ticks.
public sealed class FrameRequester
{
	private readonly Visual _owner;
	private readonly Action<TimeSpan> _onFrame;
	private bool _pending;

	public FrameRequester(Visual owner)
	{
		_owner = owner;
		_onFrame = OnFrame;
	}

	// How many frames were requested (tests: requests stop when idle or hidden).
	public int Requests { get; private set; }

	public void Request()
	{
		if (_pending || !_owner.IsEffectivelyVisible || TopLevel.GetTopLevel(_owner) is not { } top)
		{
			return;
		}
		_pending = true;
		Requests++;
		top.RequestAnimationFrame(_onFrame);
	}

	private void OnFrame(TimeSpan _)
	{
		_pending = false;
		if (_owner.IsEffectivelyVisible)
		{
			_owner.InvalidateVisual();
		}
	}
}

// The clock the animations read; one monotonic source for every control. Tests replace a control's Clock.
public static class AnimationClock
{
	private static readonly long Start = Stopwatch.GetTimestamp();

	public static TimeSpan Now() => Stopwatch.GetElapsedTime(Start);
}

// Pixel snapping for thin lines at any display scaling (100 %, 125 %, 150 %...).
public static class PixelSnap
{
	// A line thickness of a whole number of device pixels, close to the wanted logical width.
	public static double Thickness(double logical, double scaling) => Math.Max(1, Math.Round(logical * scaling)) / scaling;

	// A coordinate on which a line of the given (snapped) thickness lands on whole device pixels.
	public static double Line(double value, double thickness, double scaling)
	{
		double device = Math.Round(thickness * scaling);
		double offset = device % 2 == 1 ? 0.5 : 0;
		return (Math.Floor(value * scaling) + offset) / scaling;
	}

	// A point on the device-pixel grid.
	public static double Point(double value, double scaling) => Math.Round(value * scaling) / scaling;
}
