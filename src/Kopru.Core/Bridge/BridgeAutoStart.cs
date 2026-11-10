// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Bridge;

// "Start the bridge automatically": starts it once when the app runs, nothing blocks it (setup done: driver, Bluetooth,
// a paired controller not on a cable) and the controller is connected. Once the bridge ran (or a start was tried) it stays
// out of the way, so a bridge the user stopped by hand is not started again; it re-arms when the controller connects
// again after being gone (and on the next app start, a new instance).
public sealed class BridgeAutoStart
{
	private bool _armed = true;
	private bool _present;

	// Called on every state change. True: start the bridge now.
	public bool Update(bool enabled, bool ready, bool controllerPresent, bool running)
	{
		if (!controllerPresent)
		{
			// Gone: the next connection counts as a reconnect.
			_present = false;
			return false;
		}
		if (!_present)
		{
			_present = true;
			_armed = true;
		}
		if (running)
		{
			// It runs (by itself or by hand): a later stop is the user's or the app's own, never undone here.
			_armed = false;
			return false;
		}
		if (!enabled || !ready || !_armed)
		{
			return false;
		}
		_armed = false;
		return true;
	}
}
