// SPDX-License-Identifier: GPL-3.0-or-later

namespace Kopru.Core.Hid;

public enum Transport
{
	Usb,
	Bluetooth
}


// One DualSense HID interface (Linux: a hidraw node, HidrawNode.ToHidDevice).
public sealed record HidDevice(string Path, int VendorId, int ProductId, int InputLength, int OutputLength, int FeatureLength)
{
	public const int SonyVendorId = 0x054C;
	public const int DualSenseProductId = 0x0CE6;
	public const int DualSenseEdgeProductId = 0x0DF2;

	private const string BluetoothHidService = "{00001124-0000-1000-8000-00805f9b34fb}";

	// Set by finders that learn the bus elsewhere (Linux hidraw: the uevent's HID_ID); null on Windows, where the
	// interface path tells it.
	public Transport? KnownTransport { get; init; }

	public Transport Transport => KnownTransport ??
		(Path.Contains(BluetoothHidService, StringComparison.OrdinalIgnoreCase) ? Transport.Bluetooth : Transport.Usb);

	public bool IsEdge => ProductId == DualSenseEdgeProductId;
}

// What a session needs from an open controller (faked in tests).
public interface IHidLink : IDisposable
{
	HidDevice Device { get; }

	// Blocks until the next input report; returns its length. Throws once the link is closed.
	int Read(Span<byte> buffer);

	void Write(ReadOnlySpan<byte> report);

	byte[] GetFeature(byte reportId);

	void CancelRead();
}
