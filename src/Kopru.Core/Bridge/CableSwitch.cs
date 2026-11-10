// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Bridge;

public enum CableDecision
{
	None,
	// The bridged controller is now on a cable: stop the bridge (games see it as a wired pad already).
	StopForCable,
	// The cable is gone and the controller is back on Bluetooth: start the bridge again.
	Resume
}

// USB <-> Bluetooth switching. The same controller is recognised by its Bluetooth address: over USB it is read from
// feature report 0x09 (bytes 1..6, LSB first); on the Bluetooth side the address comes from 0x09 read over Bluetooth
// when the bridge (or the read-only probe) opened it, and from the Windows paired list (BTHENUM instance /
// BluetoothFindFirstDevice address of the paired DualSense). Our own virtual pad answers 0x09 with
// PairingInfo.VirtualAddress, so it never matches. Fed once per presence tick; pure, so it is tested without hardware.
public sealed class CableSwitch
{
	// True while the bridge is stopped because the controller is on a cable (and will resume on Bluetooth).
	public bool PausedForCable { get; private set; }

	// bridgeWanted: the bridge is started (by the user). usbAddresses: 0x09 addresses of the USB DualSenses present.
	// knownAddresses: Bluetooth addresses of our controller (last bridged, paired list). bluetoothPresent: a
	// Bluetooth DualSense is listed.
	public CableDecision Next(bool bridgeWanted, IReadOnlyCollection<ulong> usbAddresses, IReadOnlyCollection<ulong> knownAddresses, bool bluetoothPresent)
	{
		bool onCable = usbAddresses.Any(a => a != Hid.PairingInfo.VirtualAddress && knownAddresses.Contains(a));
		if (!PausedForCable)
		{
			if (bridgeWanted && onCable)
			{
				PausedForCable = true;
				return CableDecision.StopForCable;
			}
			return CableDecision.None;
		}
		if (bridgeWanted)
		{
			// The user started it again by hand.
			PausedForCable = false;
			return CableDecision.None;
		}
		if (!onCable && bluetoothPresent)
		{
			PausedForCable = false;
			return CableDecision.Resume;
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
