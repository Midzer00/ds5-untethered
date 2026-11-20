// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;
using Kopru.Core.Protocol;

namespace Kopru.Core.Bridge;

// Controller shortcuts (PS held as a modifier) and short player LED feedback for them.
public sealed partial class BridgeHost
{
	public static readonly TimeSpan PlayerLedFeedbackLength = TimeSpan.FromSeconds(1.5);

	private int ledFeedback;

	// Applied to the input of every connection; subscribe to Triggered (raised on the input thread).
	public ShortcutProcessor Shortcuts { get; } = new();

	// Shows a pattern on the player LEDs for a moment, then puts the user's light back (a game's own light comes back with
	// its next output). False while no controller session is open.
	public bool ShowPlayerLeds(byte pattern, TimeSpan? duration = null)
	{
		Connection? c = current;
		if (c == null)
		{
			return false;
		}
		int mine = Interlocked.Increment(ref ledFeedback);
		TimeSpan length = duration ?? PlayerLedFeedbackLength;
		// The light effect sender must not overwrite the indicator while it shows.
		lightSender?.Hold(length);
		try
		{
			c.Session.UpdateOutput(b =>
			{
				b[OutputBlock.Flag1] |= OutputBlock.PlayerLedControl;
				b[OutputBlock.PlayerLeds] = pattern;
			});
		}
		catch (Exception)
		{
			return false;
		}
		_ = Task.Delay(length, time).ContinueWith(_ =>
		{
			if (Volatile.Read(ref ledFeedback) == mine && current == c)
			{
				// The user's light and LED pattern come back (from the sender when one runs).
				if (!InvalidateLightSender())
				{
					ApplyLightTo(c.Session);
				}
				c.Bridge.ResendGameOutput();
			}
		}, TaskScheduler.Default);
		return true;
	}
}
