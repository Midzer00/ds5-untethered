// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Kopru.Core.Hid;

// Feature report 0x20 ("firmware info", 64 bytes; byte 0 is the report ID, over Bluetooth the last 4 bytes are a CRC).
// Public layout, as parsed by the Linux kernel's hid-playstation driver (dualsense_get_firmware_info:
// hw_version = le32 at 24, fw_version = le32 at 28, update_version = le16 at 44) and as documented in the community
// DualSense data-structure notes (build date/time are the firmware's C __DATE__ / __TIME__ strings):
//   1..11  build date, ASCII "Mmm dd yyyy" (day padded with a space, e.g. "Jun  5 2023")
//   12..19 build time, ASCII "hh:mm:ss"
//   24..27 hardware info/version, le32
//   28..31 firmware version, le32
//   44..45 update version, le16 (what Sony's updater shows, e.g. 0x0630 -> "06.30")
// Reading it is a plain GET_FEATURE: it changes nothing on the controller.
public sealed record FirmwareInfo(DateTime? BuildTime, string BuildText, uint HardwareVersion, uint FirmwareVersion, ushort UpdateVersion)
{
	public const byte ReportId = 0x20;
	public const int Size = 64;
	public const int BuildDateOffset = 1;
	public const int BuildDateLength = 11;
	public const int BuildTimeOffset = 12;
	public const int BuildTimeLength = 8;
	public const int HardwareOffset = 24;
	public const int FirmwareOffset = 28;
	public const int UpdateOffset = 44;

	// Null when the report is too short or isn't 0x20.
	public static FirmwareInfo? Parse(ReadOnlySpan<byte> report)
	{
		if (report.Length < UpdateOffset + 2 || report[0] != ReportId)
		{
			return null;
		}
		string date = Ascii(report.Slice(BuildDateOffset, BuildDateLength));
		string clock = Ascii(report.Slice(BuildTimeOffset, BuildTimeLength));
		string text = string.Join(' ', $"{date} {clock}".Split(' ', StringSplitOptions.RemoveEmptyEntries));
		DateTime? built = DateTime.TryParseExact(text, "MMM d yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t)
			? t
			: null;
		return new FirmwareInfo(built, text, BinaryPrimitives.ReadUInt32LittleEndian(report[HardwareOffset..]),
			BinaryPrimitives.ReadUInt32LittleEndian(report[FirmwareOffset..]), BinaryPrimitives.ReadUInt16LittleEndian(report[UpdateOffset..]));
	}

	// "06.30" from the update version; the raw firmware version in hex when that is 0.
	public string FirmwareText => UpdateVersion != 0
		? string.Create(CultureInfo.InvariantCulture, $"{UpdateVersion >> 8:X2}.{UpdateVersion & 0xFF:X2}")
		: Hex(FirmwareVersion);

	public string FirmwareHex => Hex(FirmwareVersion);

	public string HardwareText => Hex(HardwareVersion);

	public static string Hex(uint value) => string.Create(CultureInfo.InvariantCulture, $"0x{value:X8}");

	private static string Ascii(ReadOnlySpan<byte> bytes)
	{
		StringBuilder b = new(bytes.Length);
		foreach (byte c in bytes)
		{
			if (c == 0)
			{
				break;
			}
			b.Append(c is >= 0x20 and < 0x7F ? (char)c : ' ');
		}
		return b.ToString().Trim();
	}
}

// Feature report 0x09 ("pairing info", 20 bytes): bytes 1..6 are the controller's own Bluetooth address, least
// significant byte first (Linux hid-playstation dualsense_get_mac_address copies &buf[1], 6 bytes, and prints it
// reversed). Readable over USB and Bluetooth; the same controller reports the same address on both.
public static class PairingInfo
{
	public const byte ReportId = 0x09;
	public const int AddressOffset = 1;

	// The 48-bit address as Windows uses it (BLUETOOTH_DEVICE_INFO.Address, the BTHENUM instance's 12 hex digits),
	// or null when the report is short, isn't 0x09 or holds no address.
	public static ulong? ParseAddress(ReadOnlySpan<byte> report)
	{
		if (report.Length < AddressOffset + 6 || report[0] != ReportId)
		{
			return null;
		}
		ulong address = 0;
		for (int i = 5; i >= 0; i--)
		{
			address = (address << 8) | report[AddressOffset + i];
		}
		return address is 0 or 0xFFFFFFFFFFFF ? null : address;
	}

	// The virtual pad's address (Virtual.DeviceIdentity.VirtualAddress), so our own pad is never taken for the controller.
	public static ulong VirtualAddress { get; } = ParseAddress([ReportId, .. Virtual.DeviceIdentity.VirtualAddress])!.Value;
}

public static class AddressText
{
	// "A0:B1:C2:D3:E4:F5" (most significant byte first, as Windows shows it).
	public static string Format(ulong address) => string.Join(':', Bytes(address).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

	// Only the last 2 bytes visible: "XX:XX:XX:XX:E4:F5".
	public static string Mask(ulong address) => "XX:XX:XX:XX:" + Format(address)[^5..];

	private static IEnumerable<byte> Bytes(ulong address)
	{
		for (int shift = 40; shift >= 0; shift -= 8)
		{
			yield return (byte)(address >> shift);
		}
	}
}
