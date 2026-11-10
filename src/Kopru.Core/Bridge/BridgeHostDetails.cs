// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Hid;

namespace Kopru.Core.Bridge;

// Controller details (feature reports 0x20 and 0x09, GET_FEATURE only, nothing is ever written). Read once per
// connection on a link that is open anyway: by the bridge right after BluetoothSession.Start (which itself reads 0x05),
// or - while the bridge is stopped - inside the battery probe's short read-only open (BatteryProbe.Opened), so no extra
// open is made and the same rules apply (never while the bridge starts or runs, or a test holds the controller).
public sealed partial class BridgeHost
{
	private ControllerDetails? details;

	// The last details read (kept after the controller left; ForgetDetails makes the next probe read them again).
	public ControllerDetails? Details
	{
		get
		{
			lock (gate)
			{
				return details;
			}
		}
	}

	public event Action<ControllerDetails>? DetailsRead;

	// A new connection (or Reconnect): the next open reads the details again.
	public void ForgetDetails()
	{
		lock (gate)
		{
			details = null;
		}
	}

	// Battery probe: only when nothing was read for this controller yet.
	private void ReadDetailsOnce(IHidLink link)
	{
		lock (gate)
		{
			if (details != null && string.Equals(details.Path, link.Device.Path, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
		}
		ReadDetailsFrom(link);
	}

	// Called by the supervisor right after the session started on link.
	private void ReadDetailsFrom(IHidLink link)
	{
		ControllerDetails read;
		try
		{
			read = ControllerDetails.Read(link);
		}
		catch (Exception)
		{
			return;
		}
		lock (gate)
		{
			details = read;
		}
		DetailsRead?.Invoke(read);
	}
}
