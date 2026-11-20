// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Kopru.Core.Hid;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.Core.Health;

public enum ConnectionKind
{
	// The radio exists but is off (not connectable).
	BluetoothOff,
	// No Bluetooth radio on this PC.
	NoRadio,
	// No DualSense is paired in Windows.
	NeverPaired,
	// Paired, but off or asleep (no HID device).
	PairedNotConnected,
	ConnectedBluetooth,
	// On a USB cable: games see it as a wired pad already, the bridge is not needed.
	ConnectedUsb
}

// Where the controller stands. IsEdge: the connected (or paired) controller is a DualSense Edge.
public sealed record ConnectionState(ConnectionKind Kind, bool IsEdge = false)
{
	public static ConnectionState Unknown { get; } = new(ConnectionKind.NeverPaired);

	public bool IsConnected => Kind is ConnectionKind.ConnectedBluetooth or ConnectionKind.ConnectedUsb;

	public string Label(Language? l = null) => Kind switch
	{
		ConnectionKind.BluetoothOff => Text.ConnectionBluetoothOff(l),
		ConnectionKind.NoRadio => Text.ConnectionNoRadio(l),
		ConnectionKind.NeverPaired => Text.ConnectionNeverPaired(l),
		ConnectionKind.PairedNotConnected => Text.ConnectionPairedNotConnected(l),
		ConnectionKind.ConnectedBluetooth => Text.ConnectionOnBluetooth(l),
		_ => Text.ConnectionOnUsb(l)
	};

	public string Reason(Language? l = null) => Kind switch
	{
		ConnectionKind.BluetoothOff => Text.ConnectionBluetoothOffReason(l),
		ConnectionKind.NoRadio => Text.ConnectionNoRadioReason(l),
		ConnectionKind.NeverPaired => Text.ConnectionNeverPairedReason(l),
		ConnectionKind.PairedNotConnected => Text.ConnectionPairedNotConnectedReason(l),
		ConnectionKind.ConnectedBluetooth => Text.ConnectionBluetoothReason(l),
		_ => Text.ConnectionUsbReason(l)
	};

	// A HID controller wins (Bluetooth before USB); then the radio; then the Windows paired-device list.
	public static ConnectionState Detect(RadioState radio, IEnumerable<HidDevice> controllers, IEnumerable<PairedController> paired)
	{
		List<HidDevice> hid = [.. controllers];
		if (hid.FirstOrDefault(d => d.Transport == Transport.Bluetooth) is { } bt)
		{
			return new(ConnectionKind.ConnectedBluetooth, bt.IsEdge);
		}
		if (hid.FirstOrDefault() is { } usb)
		{
			return new(ConnectionKind.ConnectedUsb, usb.IsEdge);
		}
		if (radio == RadioState.Missing)
		{
			return new(ConnectionKind.NoRadio);
		}
		if (radio == RadioState.Off)
		{
			return new(ConnectionKind.BluetoothOff);
		}
		List<PairedController> list = [.. paired];
		return list.Count == 0
			? new(ConnectionKind.NeverPaired)
			: new(ConnectionKind.PairedNotConnected, list.Any(p => p.IsEdge));
	}
}

// A DualSense in the Windows Bluetooth paired-device list. Address = 48-bit Bluetooth address.
public sealed record PairedController(string Name, ulong Address, bool Connected, bool IsEdge);

public interface IPairedControllers
{
	IReadOnlyList<PairedController> Find();
}
