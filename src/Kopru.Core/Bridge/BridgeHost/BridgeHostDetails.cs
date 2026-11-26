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

	// A new connection seen by the presence tick: the next battery probe reads the details again. Only while the bridge
	// is stopped: a running bridge reads them itself on every connect (ReadDetailsFrom), and between that read and the
	// moment its connection is published (Device still null) a reset here would drop them for the whole connection —
	// the cable switch then never sees the controller as bridged (no hiding, no wired mode; seen on hardware after a
	// power cycle).
	public void ForgetDetailsIfStopped()
	{
		lock (gate)
		{
			if (supervisor == null)
			{
				details = null;
			}
		}
	}

	// How often a running connection asks again for details that lack the Bluetooth address (the controller did not
	// answer 0x09 right after connecting): the cable switch matches the USB twin by it. Timeout.InfiniteTimeSpan: never.
	public TimeSpan DetailsRetryInterval { get; set; } = TimeSpan.FromSeconds(5);

	// From the supervisor's monitor loop: reads the details again (GET_FEATURE only) while the ones held for this link
	// have no address, at most every DetailsRetryInterval. Logs the first retry and the one that finds the address.
	private void RereadMissingDetails(Connection c, IHidLink link)
	{
		lock (gate)
		{
			if (details is { Address: not null } held && string.Equals(held.Path, link.Device.Path, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
		}
		if (DetailsRetryInterval == Timeout.InfiniteTimeSpan)
		{
			return;
		}
		long now = time.GetUtcNow().UtcTicks;
		if (c.DetailsRetries > 0 && now < c.DetailsRetryAt)
		{
			return;
		}
		c.DetailsRetryAt = now + DetailsRetryInterval.Ticks;
		if (c.DetailsRetries++ == 0)
		{
			Diagnostics.AppLog.Write($"bridge{LogSlot}: controller address missing while running, reading the details again");
		}
		ReadDetailsFrom(link);
		if (Details is { Address: not null })
		{
			Diagnostics.AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
				$"bridge{LogSlot}: controller details read again (try {c.DetailsRetries})"));
		}
	}

	private sealed partial class Connection
	{
		// RereadMissingDetails bookkeeping (the supervisor's thread only).
		public int DetailsRetries;
		public long DetailsRetryAt;
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
