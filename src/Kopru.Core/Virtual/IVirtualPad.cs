// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.UsbIp;

namespace Kopru.Core.Virtual;

// The controller games see. DualSense is the default; Xbox 360 and DualShock 4 are opt-ins per game for games without
// DualSense support.
public enum VirtualPadType
{
	DualSense,
	Xbox360,
	DualShock4
}

// A virtual controller served over USB/IP. The bridge always speaks DualSense to it: PushInput takes a DualSense USB
// input report (0x01, 64 bytes) and OutputReceived hands out DualSense output reports (0x02); a pad of another type
// converts both ways (see XboxReports).
public interface IVirtualPad : IUsbIpDevice, IDisposable
{
	VirtualPadType Type { get; }

	void PushInput(ReadOnlySpan<byte> usbInputReport);

	event Action<byte[]>? OutputReceived;

	// Game audio (DualSense only: speaker + haptics channels); never raised by other pads.
	event Action<ReadOnlyMemory<byte>>? AudioReceived;
}
