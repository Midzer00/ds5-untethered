// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Kopru.Core.Hid;

namespace Kopru.Platform.Linux.Hidraw;

// The bus of a HID device, as the kernel numbers it (BUS_* in the public <linux/input.h>).
public static class HidBus
{
	public const int Usb = 0x03;
	public const int Bluetooth = 0x05;

	// Null for any other bus (I2C, virtual uhid, ...).
	public static Transport? ToTransport(int bus) => bus switch
	{
		Usb => Transport.Usb,
		Bluetooth => Transport.Bluetooth,
		_ => null
	};
}

// HID_ID from a HID device's uevent: "BBBB:VVVVVVVV:PPPPPPPP" in hex (bus, vendor, product).
public readonly record struct HidId(int Bus, int VendorId, int ProductId)
{
	public Transport? Transport => HidBus.ToTransport(Bus);

	public bool IsDualSense => VendorId == HidDevice.SonyVendorId &&
		ProductId is HidDevice.DualSenseProductId or HidDevice.DualSenseEdgeProductId;

	public static HidId? Parse(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		string[] parts = text.Trim().Split(':');
		if (parts.Length != 3)
		{
			return null;
		}
		return TryHex(parts[0], out int bus) && TryHex(parts[1], out int vendor) && TryHex(parts[2], out int product)
			? new HidId(bus, vendor, product)
			: null;
	}

	private static bool TryHex(string text, out int value)
	{
		value = 0;
		return text.Length is > 0 and <= 8 &&
			int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) && value >= 0;
	}
}

// The uevent file of a HID device (/sys/class/hidraw/hidrawN/device/uevent): KEY=VALUE lines such as DRIVER, HID_ID,
// HID_NAME, HID_PHYS, HID_UNIQ and MODALIAS. Over Bluetooth HID_UNIQ is the controller's address and HID_PHYS the
// adapter's: never log either unmasked.
public sealed record HidrawUevent(IReadOnlyDictionary<string, string> Values)
{
	public string? Driver => Get("DRIVER");

	public HidId? Id => HidId.Parse(Get("HID_ID"));

	public string? Name => Get("HID_NAME");

	public string? Phys => Get("HID_PHYS");

	public string? Uniq => Get("HID_UNIQ");

	public string? Get(string key) => Values.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;

	public static HidrawUevent Parse(string text)
	{
		Dictionary<string, string> values = new(StringComparer.Ordinal);
		foreach (string raw in text.Split('\n'))
		{
			string line = raw.TrimEnd('\r');
			int equals = line.IndexOf('=');
			if (equals <= 0)
			{
				continue;
			}
			values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
		}
		return new HidrawUevent(values);
	}
}
