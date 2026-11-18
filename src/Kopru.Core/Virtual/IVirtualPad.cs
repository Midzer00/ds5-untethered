// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.UsbIp;

namespace Kopru.Core.Virtual;

// The controller games see. DualSense is the default; Xbox Series, Xbox 360 and DualShock 4 are for games without
// DualSense support. Saved by name; new values go at the end.
public enum VirtualPadType
{
	DualSense,
	Xbox360,
	DualShock4,

	// A wired Xbox Series controller (GIP, [MS-GIPUSB]): rumble plus the trigger motors (played on the adaptive triggers).
	// Windows 10 21H2 or later (XboxSeriesSupport).
	XboxSeries
}

// What each type can carry besides buttons and rumble.
public static class VirtualPadTypes
{
	// An Xbox pad: rumble only, no light bar, speaker, microphone or haptics audio.
	public static bool IsXbox(this VirtualPadType type) => type is VirtualPadType.Xbox360 or VirtualPadType.XboxSeries;

	// Games can set the light bar (DualSense, DualShock 4).
	public static bool HasLight(this VirtualPadType type) => !type.IsXbox();

	// The pad has a USB audio interface (DualSense, DualShock 4).
	public static bool HasAudio(this VirtualPadType type) => !type.IsXbox();
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

	// The host let go of the pad (the USB/IP connection ended: detach, the host driver unplugged it).
	event Action? HostReleased;
}

// A pad that has something to tell the host before it is unplugged (the experimental Xbox One pad's "powering off"
// status). The bridge awaits it just before detaching; pads without it are unplugged as before.
public interface IUnplugNotice
{
	Task BeforeUnplugAsync();
}
