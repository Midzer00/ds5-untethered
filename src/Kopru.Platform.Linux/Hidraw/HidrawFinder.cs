// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;
using Kopru.Core.Setup;

namespace Kopru.Platform.Linux.Hidraw;

// One hidraw node of a DualSense: its name ("hidraw3"), the device node, what its uevent says and the report sizes of its
// descriptor (null when the descriptor could not be read).
public sealed record HidrawNode(string Name, string DevicePath, HidrawUevent Uevent, HidReportDescriptor? Descriptor)
{
	// When the descriptor can't be read: enough for every DualSense feature report the bridge reads.
	public const int DefaultFeatureLength = 64;

	public HidId Id => Uevent.Id ?? default;

	public Transport? Transport => Id.Transport;

	public bool IsBluetooth => Transport == Kopru.Core.Hid.Transport.Bluetooth;

	// Stable while the controller stays paired: its Bluetooth address (HID_UNIQ) when there is one, else the node name.
	// Like the Windows device instance id it contains the address: mask it in logs (DiagnosticsExport.Scrub).
	public string StableId => Uevent.Uniq is { } uniq ? "hid-uniq:" + uniq.ToLowerInvariant() : "hidraw:" + Name;

	// The same shape Windows reports (lengths include the report ID byte), with the bus taken from HID_ID.
	public HidDevice ToHidDevice() => new(DevicePath, Id.VendorId, Id.ProductId,
		Descriptor?.MaxLength(HidReportKind.Input) ?? BluetoothReports.InputSize,
		Descriptor?.MaxLength(HidReportKind.Output) ?? BluetoothReports.OutputSize,
		Descriptor?.MaxLength(HidReportKind.Feature) ?? DefaultFeatureLength)
	{
		KnownTransport = Transport
	};
}

// Lists DualSense controllers through sysfs: every /sys/class/hidraw/hidrawN whose device/uevent has a HID_ID with Sony's
// vendor id and a DualSense or Edge product id, USB or Bluetooth. Reading sysfs needs no special rights; opening the
// /dev/hidrawN node does (see docs/linux/README.md). The folders are parameters so tests can use a fake tree.
public sealed class HidrawFinder(string classDirectory = HidrawFinder.SysClassHidraw, string deviceDirectory = "/dev") : IControllerFinder
{
	public const string SysClassHidraw = "/sys/class/hidraw";

	public IReadOnlyList<HidrawNode> FindNodes()
	{
		if (!Directory.Exists(classDirectory))
		{
			return [];
		}
		List<(int Number, HidrawNode Node)> found = [];
		foreach (string entry in Directory.EnumerateFileSystemEntries(classDirectory))
		{
			string name = Path.GetFileName(entry);
			if (Describe(entry, name) is { } node)
			{
				found.Add((NodeNumber(name), node));
			}
		}
		return [.. found.OrderBy(f => f.Number).ThenBy(f => f.Node.Name, StringComparer.Ordinal).Select(f => f.Node)];
	}

	public IReadOnlyList<HidDevice> Find() => [.. FindNodes().Select(n => n.ToHidDevice())];

	// "hidraw12" -> 12; anything else sorts last.
	public static int NodeNumber(string name) =>
		name.StartsWith("hidraw", StringComparison.Ordinal) &&
		int.TryParse(name.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;

	private HidrawNode? Describe(string entry, string name)
	{
		if (!name.StartsWith("hidraw", StringComparison.Ordinal))
		{
			return null;
		}
		string? ueventText = ReadText(Path.Combine(entry, "device", "uevent"));
		if (ueventText == null)
		{
			return null;
		}
		HidrawUevent uevent = HidrawUevent.Parse(ueventText);
		if (uevent.Id is not { IsDualSense: true })
		{
			return null;
		}
		byte[]? raw = ReadBytes(Path.Combine(entry, "device", "report_descriptor"));
		HidReportDescriptor? descriptor = raw is { Length: > 0 } ? HidReportDescriptor.Parse(raw) : null;
		if (descriptor is { UsagePage: not 0 } && !descriptor.IsGamepad)
		{
			// Another collection of the same device (none on a DualSense today); only the gamepad is bridged.
			return null;
		}
		return new HidrawNode(name, Path.Combine(deviceDirectory, name), uevent, descriptor);
	}

	private static string? ReadText(string path) => ReadBytes(path) is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null;

	// sysfs attributes report a size (often 4096) that is not their length: read until the end instead of trusting it.
	private static byte[]? ReadBytes(string path)
	{
		try
		{
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
			using MemoryStream copy = new();
			byte[] buffer = new byte[4096];
			int read;
			while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
			{
				copy.Write(buffer, 0, read);
			}
			return copy.ToArray();
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
