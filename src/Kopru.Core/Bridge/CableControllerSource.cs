// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Hid;

namespace Kopru.Core.Bridge;

// The controller source wired mode uses (EXPERIMENTAL): the Bluetooth controllers of the inner source, plus the
// controllers the app offers on the cable alone (Offer). An offered controller carries the id it has over Bluetooth
// (the device instance id of its Bluetooth HID node, found through its 0x09 address), so the manager binds it to the
// same slot and everything keyed by that id (look, name, own profile) applies exactly as over Bluetooth. Opening an
// offered controller opens its USB link (the host's session then runs on USB alone). Bluetooth wins: an id the inner
// source lists is never taken from the offers.
public sealed class CableControllerSource(IControllerSource inner, Func<HidDevice, IHidLink?>? openUsb = null) : IControllerSource
{
	private readonly Func<HidDevice, IHidLink?> openUsb = openUsb ?? (d => WindowsHid.Open(d));
	private readonly object gate = new();
	private IReadOnlyList<ControllerPresence> offered = [];

	// The controllers on the cable alone to take over now (id, USB pad), replacing the earlier offer; empty: none.
	public void Offer(IReadOnlyList<ControllerPresence> cableOnly)
	{
		lock (gate)
		{
			offered = [.. cableOnly];
		}
	}

	public IReadOnlyList<ControllerPresence> Offered
	{
		get
		{
			lock (gate)
			{
				return offered;
			}
		}
	}

	public IReadOnlyList<ControllerPresence> Enumerate()
	{
		IReadOnlyList<ControllerPresence> bluetooth = inner.Enumerate();
		List<ControllerPresence> all = [.. bluetooth];
		foreach (ControllerPresence p in Offered)
		{
			if (!all.Any(b => string.Equals(b.Id, p.Id, StringComparison.OrdinalIgnoreCase)))
			{
				all.Add(p);
			}
		}
		return all;
	}

	public IHidLink? TryOpen() => inner.TryOpen() ?? OpenOffered(Offered.FirstOrDefault());

	public IHidLink? TryOpen(string id) =>
		inner.TryOpen(id) ?? OpenOffered(Offered.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)));

	private IHidLink? OpenOffered(ControllerPresence? p)
	{
		if (p == null)
		{
			return null;
		}
		try
		{
			return openUsb(p.Device);
		}
		catch (Exception)
		{
			// Hidden from us, in use or gone: asked again on the next poll while it is still offered.
			return null;
		}
	}
}
