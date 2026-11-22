// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Bridge;

public enum CableDecision
{
	None,
	// Bluetooth dropped while a known controller stayed on the cable (USB only): stop the bridge; games use the wired pad.
	StopForCable,
	// The controller is back on Bluetooth (the cable may still be in): start the bridge again.
	Resume
}

// Which link feeds the bridge while the bridged controller is also on a USB cable. ChargeOnly: the cable only
// charges; the bridge keeps running on Bluetooth (every feature keeps working) and the USB twin is hidden. Wired
// (EXPERIMENTAL): while the cable is in and the twin is hidden, the bridge talks to the controller over USB;
// Bluetooth stays connected and takes over again when the cable is out.
public enum CableMode
{
	ChargeOnly,
	Wired
}

// USB <-> Bluetooth switching. The same controller is recognised by its Bluetooth address: over USB it is read from
// feature report 0x09 (bytes 1..6, LSB first); on the Bluetooth side the address comes from 0x09 read over Bluetooth
// when the bridge (or the read-only probe) opened it, and from the Windows paired list (BTHENUM instance /
// BluetoothFindFirstDevice address of the paired DualSense). Our own virtual pads answer 0x09 with a virtual address
// (PairingInfo.IsVirtual), so they never match. Fed once per presence tick; pure, so it is tested without hardware.
//
// What it decides per tick: the set of bridged-and-cabled addresses whose USB twin should be hidden (HiddenTwins; the
// service reconciles the actual hiding), a debounced stop when only the cable is left, and a debounced resume once
// Bluetooth is back. resumeDelay debounces all three (unhide, stop, resume): a USB or Bluetooth listing that drops out
// for one tick while the cable settles undoes nothing.
public sealed class CableSwitch
{
	private readonly TimeSpan resumeDelay;
	private readonly HashSet<ulong> hidden = [];
	private readonly HashSet<ulong> wiredNow = [];
	// Per wired address: consecutive ticks it was wanted but not (cabled AND bridged). One miss keeps it in
	// WiredTwins; the second drops it.
	private readonly Dictionary<ulong, int> wiredMisses = [];
	private readonly Dictionary<ulong, DateTimeOffset> twinOffSince = [];
	private DateTimeOffset? waitSince;

	// resumeDelay: how long a change must hold before it is acted on (the USB twin is unhidden, the bridge is stopped
	// for the cable, a stopped bridge resumes on Bluetooth). A DualSense that is plugged in while connected over
	// Bluetooth can keep its Bluetooth link and drop off the USB listing for a moment while the cable settles (seen
	// on hardware: the USB pad disappeared for one tick), so a one-tick blip must not unhide, stop or resume.
	// Zero: at once.
	public CableSwitch(TimeSpan? resumeDelay = null)
	{
		this.resumeDelay = resumeDelay ?? TimeSpan.Zero;
	}

	// The one place that says which link feeds the bridge while a cable is present; Next acts on it.
	public CableMode Mode { get; set; } = CableMode.ChargeOnly;

	// True while the bridge is stopped because the controller is only on a cable (Bluetooth lost; resumes on Bluetooth).
	public bool PausedForCable { get; private set; }

	// The addresses of bridged controllers whose USB twin should be hidden now (CableMode.ChargeOnly): both links are
	// up and the bridge keeps running on Bluetooth. The service reconciles the actual hiding with this.
	public IReadOnlyCollection<ulong> HiddenTwins => hidden;

	// CableMode.Wired: the hidden twins that are cabled AND bridged on this tick — with a one-tick listing gap, so a
	// USB pad missing for one tick keeps its entry (a listing that drops out while the cable settles undoes nothing);
	// the second missing tick in a row drops it. Otherwise empty: in charge-only mode, while paused, when the bridge
	// is not wanted, and on StopForCable.
	public IReadOnlyCollection<ulong> WiredTwins => wiredNow;

	// bridgeWanted: the bridge is started (by the user). usbAddresses: 0x09 addresses of the USB DualSenses present.
	// bridgedAddresses: addresses whose Bluetooth link a running bridge holds right now (decides hiding only).
	// knownAddresses: the broad set (paired list, last bridged), used only for the stop. bluetoothPresent: a Bluetooth
	// DualSense is listed. now: the time of this tick (only needed with a resume delay).
	public CableDecision Next(bool bridgeWanted, IReadOnlyCollection<ulong> usbAddresses, IReadOnlyCollection<ulong> bridgedAddresses,
		IReadOnlyCollection<ulong> knownAddresses, bool bluetoothPresent, DateTimeOffset now = default)
	{
		CableDecision decision = Decide(bridgeWanted, usbAddresses, bridgedAddresses, knownAddresses, bluetoothPresent, now);
		HashSet<ulong> wasWired = [.. wiredNow];
		wiredNow.Clear();
		if (decision == CableDecision.StopForCable || Mode != CableMode.Wired)
		{
			wiredMisses.Clear();
			return decision;
		}
		foreach (ulong a in hidden)
		{
			if (usbAddresses.Contains(a) && bridgedAddresses.Contains(a))
			{
				wiredNow.Add(a);
				wiredMisses.Remove(a);
			}
			else if (wasWired.Contains(a))
			{
				// A one-tick listing gap keeps the entry; the second miss in a row drops it.
				int miss = (wiredMisses.TryGetValue(a, out int m) ? m : 0) + 1;
				if (miss < 2)
				{
					wiredNow.Add(a);
					wiredMisses[a] = miss;
				}
				else
				{
					wiredMisses.Remove(a);
				}
			}
		}
		// An address that left `hidden` keeps nothing to count.
		foreach (ulong a in wiredMisses.Keys.Where(k => !wiredNow.Contains(k)).ToList())
		{
			wiredMisses.Remove(a);
		}
		return decision;
	}

	private CableDecision Decide(bool bridgeWanted, IReadOnlyCollection<ulong> usbAddresses, IReadOnlyCollection<ulong> bridgedAddresses,
		IReadOnlyCollection<ulong> knownAddresses, bool bluetoothPresent, DateTimeOffset now)
	{
		List<ulong> cabled = [.. usbAddresses.Where(a => !Hid.PairingInfo.IsVirtual(a))];
		if (PausedForCable)
		{
			if (bridgeWanted)
			{
				// Started again by hand.
				PausedForCable = false;
				waitSince = null;
				return CableDecision.None;
			}
			// Resume once Bluetooth is back and stayed for the delay, even with the cable still in: the next ticks
			// hide the twin again.
			if (!bluetoothPresent)
			{
				waitSince = null;
				return CableDecision.None;
			}
			waitSince ??= now;
			if (now - waitSince < resumeDelay)
			{
				return CableDecision.None;
			}
			PausedForCable = false;
			waitSince = null;
			return CableDecision.Resume;
		}
		if (!bridgeWanted)
		{
			// A bridge stop of any kind already restored all hiding.
			hidden.Clear();
			twinOffSince.Clear();
			waitSince = null;
			return CableDecision.None;
		}
		// CableMode.ChargeOnly: a controller that is cabled AND bridged over Bluetooth keeps its bridge; its USB twin
		// is hidden.
		foreach (ulong a in cabled.Where(bridgedAddresses.Contains))
		{
			hidden.Add(a);
			twinOffSince.Remove(a);
		}
		foreach (ulong a in hidden.ToList())
		{
			if (cabled.Contains(a) && bridgedAddresses.Contains(a))
			{
				continue;
			}
			// Cable out, or its Bluetooth link gone: unhidden once it stayed that way for the delay.
			if (!twinOffSince.TryGetValue(a, out DateTimeOffset off))
			{
				twinOffSince[a] = off = now;
			}
			if (now - off >= resumeDelay)
			{
				hidden.Remove(a);
				twinOffSince.Remove(a);
			}
		}
		// Only the cable is left: a known controller is on USB and no Bluetooth DualSense is listed, for the whole delay.
		if (cabled.Any(knownAddresses.Contains) && !bluetoothPresent)
		{
			waitSince ??= now;
			if (now - waitSince >= resumeDelay)
			{
				PausedForCable = true;
				waitSince = null;
				hidden.Clear();
				twinOffSince.Clear();
				return CableDecision.StopForCable;
			}
		}
		else
		{
			waitSince = null;
		}
		return CableDecision.None;
	}
}

// "Reconnect Controller": tear the bridge's connection down, open the controller again (read-only details read),
// start the bridge again when it was running. Bluetooth itself is never disconnected.
public static class ReconnectSequence
{
	public static async Task RunAsync(bool bridgeWasRunning, Func<Task> stopBridge, Func<Task> reopen, Func<Task> startBridge)
	{
		if (bridgeWasRunning)
		{
			await stopBridge().ConfigureAwait(false);
		}
		await reopen().ConfigureAwait(false);
		if (bridgeWasRunning)
		{
			await startBridge().ConfigureAwait(false);
		}
	}
}
