// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Input;

// Two touchpad clicks (the physical press, not touches) close together: the gesture that turns Mouse Mode on while the
// app's own window is in front (ShortcutProcessor raises ShortcutAction.TouchpadDoublePress).
// - the second press must go down within Window of the first one going down;
// - a press held longer than Window is not a quick press: it starts nothing and the next press is a first one again;
// - after a double press the next press is a first one again (a third quick press does not make a second gesture);
// - `allowed` is asked on each press going down only (the app's window in front and Mouse Mode off); a press while it is
//   not allowed starts nothing.
// Not thread-safe: the input thread only.
public sealed class TouchpadDoublePress
{
	public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(350);

	private bool down, armed;
	private long firstAt;

	// Forgets a press in progress (a new connection, shortcuts turned off).
	public void Reset() => down = armed = false;

	// One input report: whether the touchpad is pressed now, its timestamp (frequency: ticks per second). True when this
	// report completes a double press.
	public bool Update(bool pressed, long now, long frequency, Func<bool> allowed)
	{
		long window = (long)(Window.TotalSeconds * frequency);
		if (!pressed)
		{
			down = false;
			return false;
		}
		if (down)
		{
			// Held: once longer than the window it is no quick press.
			if (armed && now - firstAt > window)
			{
				armed = false;
			}
			return false;
		}
		down = true;
		if (!allowed())
		{
			armed = false;
			return false;
		}
		if (armed && now - firstAt <= window)
		{
			armed = false;
			return true;
		}
		armed = true;
		firstAt = now;
		return false;
	}
}
