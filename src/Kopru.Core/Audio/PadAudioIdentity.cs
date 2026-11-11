// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Core.Audio;

// One device node on the way from an audio endpoint's USB audio device up to the root (see PadAudioIdentity).
public sealed record DeviceNodeInfo(string InstanceId, IReadOnlyList<string> HardwareIds, string? Service, string? LocationInfo);

// Which audio endpoints belong to a virtual pad. The endpoint's property store names its audio device ("{1}.USB\VID_054C&
// PID_0CE6&MI_00\..."); from there the device tree goes up to the USB device ("USB\VID_054C&PID_0CE6\...", location
// "Port_#0001.Hub_#0003": the virtual hub port usbip attach returned) and on to the usbip-win2 host controller
// (hardware id ROOT\USBIP_WIN2\UDE, service usbip2_ude). Seen read-only on the dev machine while the virtual pad was
// attached (usbip port 1 = Port_#0001). A real DualSense on a cable has the same ids but a PCI host above it.
// When the tree cannot be read, the endpoint name decides (NameMatch): that would also take a real DualSense on a cable
// (the bridge stops while one is plugged in, so no pad of ours is attached then).
public static class PadAudioIdentity
{
	// The audio device's instance id property in the endpoint's property store ({b3f8fa53-0004-438e-9003-51a46e139bfc},2).
	public static readonly Guid AdapterInstanceFormat = new("b3f8fa53-0004-438e-9003-51a46e139bfc");
	public const int AdapterInstancePid = 2;

	public const string SonyVid = "VID_054C";

	// The endpoint property value without its "{n}." prefix.
	public static string StripPrefix(string value)
	{
		if (value.StartsWith('{'))
		{
			int end = value.IndexOf("}.", StringComparison.Ordinal);
			if (end > 0)
			{
				return value[(end + 2)..];
			}
		}
		return value;
	}

	// chain: the audio device first, then its parents.
	public static PadAudioMatch Classify(IReadOnlyList<DeviceNodeInfo> chain)
	{
		bool underUsbIp = chain.Any(IsUsbIpHost);
		DeviceNodeInfo? usb = chain.FirstOrDefault(n => n.InstanceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase)
			&& !n.InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase));
		if (!underUsbIp)
		{
			return new PadAudioMatch(false, null, PadMatchMethod.NotPad, "audio device not on the USB/IP virtual host");
		}
		if (usb == null || !usb.InstanceId.Contains(SonyVid, StringComparison.OrdinalIgnoreCase))
		{
			return new PadAudioMatch(false, null, PadMatchMethod.NotPad, "another device on the USB/IP virtual host");
		}
		int? port = ParsePort(usb.LocationInfo);
		return new PadAudioMatch(true, port, PadMatchMethod.DeviceTree,
			"USB DualSense on the USB/IP virtual host" + (port is int p ? string.Create(CultureInfo.InvariantCulture, $", port {p}") : ", port unknown"));
	}

	public static bool IsUsbIpHost(DeviceNodeInfo node) =>
		string.Equals(node.Service, "usbip2_ude", StringComparison.OrdinalIgnoreCase)
		|| node.HardwareIds.Any(h => h.Contains("USBIP_WIN2", StringComparison.OrdinalIgnoreCase));

	// "Port_#0001.Hub_#0003" -> 1.
	public static int? ParsePort(string? location)
	{
		const string prefix = "Port_#";
		if (location == null || !location.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		int end = location.IndexOf('.', prefix.Length);
		string digits = end < 0 ? location[prefix.Length..] : location[prefix.Length..end];
		return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port > 0 ? port : null;
	}

	// Fallback when the device tree cannot be read.
	public static PadAudioMatch NameMatch(string name) =>
		name.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
			? new PadAudioMatch(true, null, PadMatchMethod.NameOnly, "name contains DualSense (device tree not readable)")
			: new PadAudioMatch(false, null, PadMatchMethod.NotPad, "device tree not readable, name is not a DualSense");
}
