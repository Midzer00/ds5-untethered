// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kopru.Core.Hid;

public enum Transport
{
	Usb,
	Bluetooth
}

// One DualSense HID interface as Windows lists it.
public sealed record HidDevice(string Path, int VendorId, int ProductId, int InputLength, int OutputLength, int FeatureLength)
{
	public const int SonyVendorId = 0x054C;
	public const int DualSenseProductId = 0x0CE6;
	public const int DualSenseEdgeProductId = 0x0DF2;

	private const string BluetoothHidService = "{00001124-0000-1000-8000-00805f9b34fb}";

	public Transport Transport => Path.Contains(BluetoothHidService, StringComparison.OrdinalIgnoreCase) ? Transport.Bluetooth : Transport.Usb;

	public bool IsEdge => ProductId == DualSenseEdgeProductId;
}

// Finds DualSense controllers and opens them through the Windows HID class driver (hid.dll and SetupAPI).
public static class WindowsHid
{
	public static IReadOnlyList<HidDevice> FindDualSenses()
	{
		List<HidDevice> found = [];
		Native.HidD_GetHidGuid(out Guid hidGuid);
		IntPtr set = Native.SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, Native.DigcfPresent | Native.DigcfDeviceInterface);
		if (set == Native.InvalidHandle)
		{
			throw new Win32Exception();
		}
		try
		{
			for (uint index = 0; ; index++)
			{
				Native.SpDeviceInterfaceData data = new() { CbSize = Marshal.SizeOf<Native.SpDeviceInterfaceData>() };
				if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data))
				{
					break;
				}
				if (InterfacePath(set, ref data) is { } path && Describe(path) is { } device)
				{
					found.Add(device);
				}
			}
		}
		finally
		{
			Native.SetupDiDestroyDeviceInfoList(set);
		}
		return found;
	}

	public static HidConnection Open(HidDevice device) => new(device, OpenHandle(device.Path, Native.GenericRead | Native.GenericWrite));

	// Shared read access only: input reports and feature reads; Write throws.
	public static ReadOnlyHidConnection OpenReadOnly(HidDevice device) => new(device, OpenHandle(device.Path, Native.GenericRead));

	// The feature report IDs a device declares (from its value and button capabilities).
	public static IReadOnlyList<byte> FeatureReportIds(HidDevice device)
	{
		using SafeFileHandle handle = OpenHandle(device.Path, 0);
		if (!Native.HidD_GetPreparsedData(handle, out IntPtr preparsed))
		{
			return [];
		}
		try
		{
			Native.HidP_GetCaps(preparsed, out Native.HidpCaps caps);
			SortedSet<byte> ids = [];
			// HIDP_VALUE_CAPS and HIDP_BUTTON_CAPS are both 72 bytes with the report ID at byte 2.
			const int capsSize = 72;
			foreach ((int count, bool values) in new[] { ((int)caps.NumberFeatureValueCaps, true), ((int)caps.NumberFeatureButtonCaps, false) })
			{
				if (count == 0)
				{
					continue;
				}
				byte[] buffer = new byte[count * capsSize];
				ushort length = (ushort)count;
				int status = values
					? Native.HidP_GetValueCaps(2, buffer, ref length, preparsed)
					: Native.HidP_GetButtonCaps(2, buffer, ref length, preparsed);
				if (status != Native.HidpStatusSuccess)
				{
					continue;
				}
				for (int i = 0; i < length; i++)
				{
					ids.Add(buffer[i * capsSize + 2]);
				}
			}
			return [.. ids];
		}
		finally
		{
			Native.HidD_FreePreparsedData(preparsed);
		}
	}

	private static string? InterfacePath(IntPtr set, ref Native.SpDeviceInterfaceData data)
	{
		Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
		if (required == 0)
		{
			return null;
		}
		IntPtr buffer = Marshal.AllocHGlobal(required);
		try
		{
			Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
			return Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, required, out _, IntPtr.Zero)
				? Marshal.PtrToStringUni(buffer + 4)
				: null;
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	// Null for anything that isn't a DualSense gamepad collection, or can't be queried.
	private static HidDevice? Describe(string path)
	{
		using SafeFileHandle handle = Native.CreateFileW(path, 0, Native.FileShareReadWrite, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
		if (handle.IsInvalid)
		{
			return null;
		}
		Native.HiddAttributes attributes = new() { Size = Marshal.SizeOf<Native.HiddAttributes>() };
		if (!Native.HidD_GetAttributes(handle, ref attributes) || attributes.VendorId != HidDevice.SonyVendorId
			|| attributes.ProductId is not (HidDevice.DualSenseProductId or HidDevice.DualSenseEdgeProductId))
		{
			return null;
		}
		if (!Native.HidD_GetPreparsedData(handle, out IntPtr preparsed))
		{
			return null;
		}
		try
		{
			if (Native.HidP_GetCaps(preparsed, out Native.HidpCaps caps) != Native.HidpStatusSuccess || caps.UsagePage != 1 || caps.Usage != 5)
			{
				return null;
			}
			return new HidDevice(path, attributes.VendorId, attributes.ProductId, caps.InputReportByteLength, caps.OutputReportByteLength,
				caps.FeatureReportByteLength);
		}
		finally
		{
			Native.HidD_FreePreparsedData(preparsed);
		}
	}

	internal static SafeFileHandle OpenHandle(string path, uint access)
	{
		SafeFileHandle handle = Native.CreateFileW(path, access, Native.FileShareReadWrite, IntPtr.Zero, Native.OpenExisting, 0, IntPtr.Zero);
		if (handle.IsInvalid)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), $"Couldn't open {path}");
		}
		return handle;
	}
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

// An open controller. Reads and writes use separate handles, so a blocking read never holds up a write.
public sealed class HidConnection : IHidLink
{
	private readonly SafeFileHandle writeHandle;

	private readonly SafeFileHandle readHandle;

	private readonly object writeGate = new();

	internal HidConnection(HidDevice device, SafeFileHandle handle)
	{
		Device = device;
		writeHandle = handle;
		readHandle = WindowsHid.OpenHandle(device.Path, Native.GenericRead);
	}

	public HidDevice Device { get; }

	// Blocks until the next input report; returns its length.
	public int Read(Span<byte> buffer)
	{
		unsafe
		{
			fixed (byte* p = buffer)
			{
				if (!Native.ReadFile(readHandle, p, buffer.Length, out int read, IntPtr.Zero))
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}
				return read;
			}
		}
	}

	// The buffer must be the device's full output length (Windows requires it; 547 bytes over Bluetooth).
	public void Write(ReadOnlySpan<byte> report)
	{
		lock (writeGate)
		{
			unsafe
			{
				fixed (byte* p = report)
				{
					if (!Native.WriteFile(writeHandle, p, report.Length, out _, IntPtr.Zero))
					{
						throw new Win32Exception(Marshal.GetLastWin32Error());
					}
				}
			}
		}
	}

	public byte[] GetFeature(byte reportId)
	{
		byte[] buffer = new byte[Device.FeatureLength];
		buffer[0] = reportId;
		if (!Native.HidD_GetFeature(writeHandle, buffer, buffer.Length))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
		return buffer;
	}

	private int disposed;

	// Unblocks a pending Read (it throws), e.g. when stopping a reader thread. No-op once disposed.
	public void CancelRead()
	{
		if (Volatile.Read(ref disposed) == 0)
		{
			Native.CancelIoEx(readHandle, IntPtr.Zero);
		}
	}

	// Safe to call more than once (a session and its owner may both dispose the link).
	public void Dispose()
	{
		CancelRead();
		if (Interlocked.Exchange(ref disposed, 1) != 0)
		{
			return;
		}
		readHandle.Dispose();
		writeHandle.Dispose();
	}
}

// A controller opened for reading only (no write access to the device at all).
public sealed class ReadOnlyHidConnection : IHidLink
{
	private readonly SafeFileHandle handle;

	internal ReadOnlyHidConnection(HidDevice device, SafeFileHandle handle)
	{
		Device = device;
		this.handle = handle;
	}

	public HidDevice Device { get; }

	public int Read(Span<byte> buffer)
	{
		unsafe
		{
			fixed (byte* p = buffer)
			{
				if (!Native.ReadFile(handle, p, buffer.Length, out int read, IntPtr.Zero))
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}
				return read;
			}
		}
	}

	public void Write(ReadOnlySpan<byte> report) => throw new NotSupportedException("Opened read-only");

	public byte[] GetFeature(byte reportId)
	{
		byte[] buffer = new byte[Device.FeatureLength];
		buffer[0] = reportId;
		if (!Native.HidD_GetFeature(handle, buffer, buffer.Length))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
		return buffer;
	}

	private int disposed;

	public void CancelRead()
	{
		if (Volatile.Read(ref disposed) == 0)
		{
			Native.CancelIoEx(handle, IntPtr.Zero);
		}
	}

	// Safe to call more than once.
	public void Dispose()
	{
		CancelRead();
		if (Interlocked.Exchange(ref disposed, 1) != 0)
		{
			return;
		}
		handle.Dispose();
	}
}

internal static unsafe class Native
{
	public const uint DigcfPresent = 0x02;
	public const uint DigcfDeviceInterface = 0x10;
	public const uint GenericRead = 0x80000000;
	public const uint GenericWrite = 0x40000000;
	public const uint FileShareReadWrite = 0x3;
	public const uint OpenExisting = 3;
	public const int HidpStatusSuccess = 0x00110000;
	public static readonly IntPtr InvalidHandle = new(-1);

	[StructLayout(LayoutKind.Sequential)]
	public struct SpDeviceInterfaceData
	{
		public int CbSize;
		public Guid InterfaceClassGuid;
		public int Flags;
		public IntPtr Reserved;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct HiddAttributes
	{
		public int Size;
		public ushort VendorId;
		public ushort ProductId;
		public ushort VersionNumber;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct HidpCaps
	{
		public ushort Usage;
		public ushort UsagePage;
		public ushort InputReportByteLength;
		public ushort OutputReportByteLength;
		public ushort FeatureReportByteLength;
		public fixed ushort Reserved[17];
		public ushort NumberLinkCollectionNodes;
		public ushort NumberInputButtonCaps;
		public ushort NumberInputValueCaps;
		public ushort NumberInputDataIndices;
		public ushort NumberOutputButtonCaps;
		public ushort NumberOutputValueCaps;
		public ushort NumberOutputDataIndices;
		public ushort NumberFeatureButtonCaps;
		public ushort NumberFeatureValueCaps;
		public ushort NumberFeatureDataIndices;
	}

	[DllImport("hid.dll")]
	public static extern void HidD_GetHidGuid(out Guid guid);

	[DllImport("hid.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	public static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HiddAttributes attributes);

	[DllImport("hid.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsed);

	[DllImport("hid.dll")]
	[return: MarshalAs(UnmanagedType.U1)]
	public static extern bool HidD_FreePreparsedData(IntPtr preparsed);

	[DllImport("hid.dll")]
	public static extern int HidP_GetCaps(IntPtr preparsed, out HidpCaps caps);

	[DllImport("hid.dll")]
	public static extern int HidP_GetValueCaps(int reportType, byte[] caps, ref ushort length, IntPtr preparsed);

	[DllImport("hid.dll")]
	public static extern int HidP_GetButtonCaps(int reportType, byte[] caps, ref ushort length, IntPtr preparsed);

	[DllImport("hid.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.U1)]
	public static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] buffer, int length);

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

	[DllImport("setupapi.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid classGuid, uint index, ref SpDeviceInterfaceData data);

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, int size, out int required, IntPtr deviceInfo);

	[DllImport("setupapi.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool ReadFile(SafeFileHandle handle, byte* buffer, int length, out int read, IntPtr overlapped);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool WriteFile(SafeFileHandle handle, byte* buffer, int length, out int written, IntPtr overlapped);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
}
