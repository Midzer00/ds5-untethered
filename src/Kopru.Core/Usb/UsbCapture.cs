// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Kopru.Core.Usb;

// What a wired DualSense tells the host about itself, read once from the user's own controller: the USB descriptors
// (through its hub, as Windows' USB viewer does) and the HID feature reports. The virtual controller presents these.
public sealed record UsbCaptureResult(
	byte[] DeviceDescriptor,
	byte[] ConfigurationDescriptor,
	IReadOnlyDictionary<int, string> Strings,
	IReadOnlyDictionary<int, byte[]> HidReportDescriptors,
	IReadOnlyDictionary<byte, byte[]> FeatureReports,
	IReadOnlyList<string> Notes);

public static class UsbCapture
{
	private static readonly Guid UsbDeviceInterface = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

	private static readonly Guid UsbHubInterface = new("F18A0E88-C30C-11D0-8815-00A0C906BED8");

	private const uint IoctlGetDescriptorFromNodeConnection = 0x220410;

	private const byte DeviceDescriptorType = 1;
	private const byte ConfigurationDescriptorType = 2;
	private const byte StringDescriptorType = 3;
	private const byte HidReportDescriptorType = 0x22;

	public static UsbCaptureResult? Capture(int vendorId, int productId)
	{
		if (FindDevice(vendorId, productId) is not { } device)
		{
			return null;
		}
		List<string> notes = [$"hub {device.HubPath}, port {device.Port}"];
		using SafeFileHandle hub = UsbNative.CreateFileW(device.HubPath, 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
		if (hub.IsInvalid)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't open the USB hub");
		}
		byte[] deviceDescriptor = GetDescriptor(hub, device.Port, 0x80, DeviceDescriptorType, 0, 0, 18);
		byte[] head = GetDescriptor(hub, device.Port, 0x80, ConfigurationDescriptorType, 0, 0, 9);
		int total = head[2] | (head[3] << 8);
		byte[] configuration = GetDescriptor(hub, device.Port, 0x80, ConfigurationDescriptorType, 0, 0, total);

		Dictionary<int, string> strings = [];
		foreach (int index in new[] { deviceDescriptor[14], deviceDescriptor[15], deviceDescriptor[16] }.Where(i => i != 0).Distinct())
		{
			try
			{
				byte[] s = GetDescriptor(hub, device.Port, 0x80, StringDescriptorType, (byte)index, 0x0409, 255);
				strings[index] = Encoding.Unicode.GetString(s, 2, Math.Max(0, s[0] - 2));
			}
			catch (Win32Exception e)
			{
				notes.Add($"string {index}: {e.Message}");
			}
		}

		Dictionary<int, byte[]> hidReports = [];
		foreach ((int iface, int length) in HidInterfaces(configuration))
		{
			try
			{
				hidReports[iface] = GetDescriptor(hub, device.Port, 0x81, HidReportDescriptorType, 0, (ushort)iface, length);
			}
			catch (Win32Exception e)
			{
				notes.Add($"HID report descriptor of interface {iface}: {e.Message}");
			}
		}
		return new UsbCaptureResult(deviceDescriptor, configuration, strings, hidReports, new Dictionary<byte, byte[]>(), notes);
	}

	// Interface numbers and report descriptor lengths of the HID interfaces in a configuration descriptor.
	public static IEnumerable<(int Interface, int Length)> HidInterfaces(byte[] configuration)
	{
		int currentInterface = -1;
		for (int at = 0; at + 1 < configuration.Length && configuration[at] > 0; at += configuration[at])
		{
			byte type = configuration[at + 1];
			if (type == 4)
			{
				currentInterface = configuration[at + 2];
			}
			else if (type == 0x21 && at + 8 < configuration.Length)
			{
				// HID descriptor: bNumDescriptors at 5, then (type, length) pairs.
				int count = configuration[at + 5];
				for (int i = 0; i < count; i++)
				{
					int entry = at + 6 + 3 * i;
					if (configuration[entry] == HidReportDescriptorType)
					{
						yield return (currentInterface, configuration[entry + 1] | (configuration[entry + 2] << 8));
					}
				}
			}
		}
	}

	private static byte[] GetDescriptor(SafeFileHandle hub, int port, byte requestType, byte type, byte index, ushort wIndex, int length)
	{
		const int header = 12;
		byte[] buffer = new byte[header + length];
		BitConverter.TryWriteBytes(buffer.AsSpan(0), port);
		buffer[4] = requestType;
		buffer[5] = 6; // GET_DESCRIPTOR
		buffer[6] = index;
		buffer[7] = type;
		BitConverter.TryWriteBytes(buffer.AsSpan(8), wIndex);
		BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)length);
		if (!UsbNative.DeviceIoControl(hub, IoctlGetDescriptorFromNodeConnection, buffer, buffer.Length, buffer, buffer.Length, out int returned, IntPtr.Zero))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
		return buffer[header..Math.Max(header, returned)];
	}

	private sealed record FoundDevice(string HubPath, int Port);

	private static FoundDevice? FindDevice(int vendorId, int productId)
	{
		string match = $"VID_{vendorId:X4}&PID_{productId:X4}";
		Guid guid = UsbDeviceInterface;
		IntPtr set = UsbNative.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
		try
		{
			for (uint i = 0; ; i++)
			{
				UsbNative.SpDevinfoData info = new() { CbSize = Marshal.SizeOf<UsbNative.SpDevinfoData>() };
				if (!UsbNative.SetupDiEnumDeviceInfo(set, i, ref info))
				{
					return null;
				}
				StringBuilder id = new(512);
				UsbNative.CM_Get_Device_IDW(info.DevInst, id, id.Capacity, 0);
				if (!id.ToString().Contains(match, StringComparison.OrdinalIgnoreCase) || id.ToString().Contains("&MI_", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (!UsbNative.SetupDiGetDeviceRegistryPropertyW(set, ref info, 0x1C, out _, out int port, 4, out _))
				{
					continue;
				}
				UsbNative.CM_Get_Parent(out int hubInst, info.DevInst, 0);
				StringBuilder hubId = new(512);
				UsbNative.CM_Get_Device_IDW(hubInst, hubId, hubId.Capacity, 0);
				if (HubInterfacePath(hubId.ToString()) is { } hubPath)
				{
					return new FoundDevice(hubPath, port);
				}
			}
		}
		finally
		{
			UsbNative.SetupDiDestroyDeviceInfoList(set);
		}
	}

	private static string? HubInterfacePath(string hubInstanceId)
	{
		Guid guid = UsbHubInterface;
		IntPtr set = UsbNative.SetupDiGetClassDevsW(ref guid, hubInstanceId, IntPtr.Zero, 0x12);
		try
		{
			UsbNative.SpDeviceInterfaceData data = new() { CbSize = Marshal.SizeOf<UsbNative.SpDeviceInterfaceData>() };
			if (!UsbNative.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, 0, ref data))
			{
				return null;
			}
			UsbNative.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
			IntPtr buffer = Marshal.AllocHGlobal(required);
			try
			{
				Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
				return UsbNative.SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, required, out _, IntPtr.Zero) ? Marshal.PtrToStringUni(buffer + 4) : null;
			}
			finally
			{
				Marshal.FreeHGlobal(buffer);
			}
		}
		finally
		{
			UsbNative.SetupDiDestroyDeviceInfoList(set);
		}
	}
}

internal static class UsbNative
{
	[StructLayout(LayoutKind.Sequential)]
	public struct SpDevinfoData
	{
		public int CbSize;
		public Guid ClassGuid;
		public int DevInst;
		public IntPtr Reserved;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct SpDeviceInterfaceData
	{
		public int CbSize;
		public Guid InterfaceClassGuid;
		public int Flags;
		public IntPtr Reserved;
	}

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

	[DllImport("setupapi.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevinfoData info);

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SpDevinfoData info, uint property, out int type, out int value, int size, out int required);

	[DllImport("setupapi.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid classGuid, uint index, ref SpDeviceInterfaceData data);

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, int size, out int required, IntPtr info);

	[DllImport("setupapi.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	public static extern int CM_Get_Device_IDW(int devInst, StringBuilder buffer, int length, int flags);

	[DllImport("cfgmgr32.dll")]
	public static extern int CM_Get_Parent(out int parent, int devInst, int flags);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
