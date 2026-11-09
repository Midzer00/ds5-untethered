// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Kopru.Core.Virtual;

// What the virtual controller says about itself: the descriptors and feature reports read from a real DualSense on a
// cable (Kopru.Probe capture), adapted for a full-speed virtual device.
public sealed class DeviceIdentity
{
	public required byte[] DeviceDescriptor { get; init; }

	public required byte[] ConfigurationDescriptor { get; init; }

	public required byte[] HidReportDescriptor { get; init; }

	public required IReadOnlyDictionary<int, string> Strings { get; init; }

	public required IReadOnlyDictionary<byte, byte[]> FeatureReports { get; init; }

	public const byte PairingFeature = 0x09;

	// The virtual controller's own Bluetooth address, so it is never taken for the real one.
	public static readonly byte[] VirtualAddress = [0x02, 0x4B, 0x50, 0x52, 0x55, 0x01];

	public static DeviceIdentity FromCaptureFile(string path) => FromCaptureJson(File.ReadAllText(path));

	// The capture format (also used by the built-in identity under Virtual/Identity).
	public static DeviceIdentity FromCaptureJson(string text)
	{
		using JsonDocument json = JsonDocument.Parse(text);
		JsonElement root = json.RootElement;
		Dictionary<byte, byte[]> features = [];
		foreach (JsonProperty p in root.GetProperty("FeatureReports").EnumerateObject())
		{
			string value = p.Value.GetString() ?? "";
			if (!value.StartsWith("error", StringComparison.Ordinal))
			{
				features[Convert.ToByte(p.Name, 16)] = Convert.FromHexString(value);
			}
		}
		Dictionary<int, string> strings = [];
		foreach (JsonProperty p in root.GetProperty("Strings").EnumerateObject())
		{
			strings[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = p.Value.GetString() ?? "";
		}
		string hid = root.GetProperty("HidReportDescriptors").EnumerateObject().First().Value.GetString()!;
		return Create(Convert.FromHexString(root.GetProperty("Device").GetString()!), Convert.FromHexString(root.GetProperty("Configuration").GetString()!),
			Convert.FromHexString(hid), strings, features);
	}

	public static DeviceIdentity Create(byte[] device, byte[] configuration, byte[] hidReport, IReadOnlyDictionary<int, string> strings,
		IReadOnlyDictionary<byte, byte[]> features)
	{
		Dictionary<byte, byte[]> adapted = features.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone());
		if (adapted.TryGetValue(PairingFeature, out byte[]? pairing) && pairing.Length >= 7)
		{
			VirtualAddress.CopyTo(pairing, 1);
		}
		return new DeviceIdentity
		{
			DeviceDescriptor = (byte[])device.Clone(),
			ConfigurationDescriptor = ForFullSpeed(configuration),
			HidReportDescriptor = (byte[])hidReport.Clone(),
			Strings = strings,
			FeatureReports = adapted
		};
	}

	// The real controller is a high-speed device; the virtual one runs at full speed, where an isochronous bInterval of 4
	// would mean every 8 ms and an interrupt one 6 ms. Every endpoint polls each 1 ms frame instead.
	public static byte[] ForFullSpeed(byte[] configuration)
	{
		byte[] adapted = (byte[])configuration.Clone();
		for (int at = 0; at + 6 < adapted.Length && adapted[at] > 0; at += adapted[at])
		{
			if (adapted[at + 1] == 5)
			{
				adapted[at + 6] = 1;
			}
		}
		return adapted;
	}

	public IReadOnlyList<(byte Class, byte SubClass, byte Protocol)> Interfaces()
	{
		List<(byte, byte, byte)> found = [];
		HashSet<int> seen = [];
		byte[] c = ConfigurationDescriptor;
		for (int at = 0; at + 7 < c.Length && c[at] > 0; at += c[at])
		{
			if (c[at + 1] == 4 && seen.Add(c[at + 2]))
			{
				found.Add((c[at + 5], c[at + 6], c[at + 7]));
			}
		}
		return found;
	}

	// The HID class descriptor (type 0x21) inside the configuration.
	public byte[] HidDescriptor()
	{
		byte[] c = ConfigurationDescriptor;
		for (int at = 0; at + 1 < c.Length && c[at] > 0; at += c[at])
		{
			if (c[at + 1] == 0x21)
			{
				return c[at..(at + c[at])];
			}
		}
		return [];
	}
}
