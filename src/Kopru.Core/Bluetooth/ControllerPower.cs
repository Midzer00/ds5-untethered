// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Kopru.Core.Hid;

namespace Kopru.Core.Bluetooth;

// Turns a Bluetooth controller off by dropping its link from the PC side (the DualSense powers down when its host
// disconnects it, like "Disconnect" in Windows Bluetooth settings).
public interface IControllerPower
{
	// The controller's Bluetooth address, or null when it can't be found.
	ulong? FindAddress(HidDevice device);

	// True when the radio accepted the disconnect.
	Task<bool> DisconnectAsync(ulong address, CancellationToken cancel = default);
}

// Bluetooth device addresses (BTH_ADDR: the 48-bit address in the low bytes, most significant byte first when printed).
public static partial class BluetoothAddress
{
	// The Bluetooth HID service node's instance ID ends in "&<address>_C<8 hex>", e.g.
	// BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\7&1D1B1AD6&0&A0AB51123456_C00000000.
	// Its child HID node ("HID\{00001124-...}\8&...&0&0000") does not carry the address, so ask for the parent.
	public static ulong? FromInstanceId(string? instanceId)
	{
		if (instanceId == null)
		{
			return null;
		}
		Match m = InstanceAddress().Match(instanceId);
		return m.Success && ulong.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong a) && a != 0 ? a : null;
	}

	// Feature report 0x09 (pairing info): the controller's own address in bytes 1..6, least significant byte first.
	public static ulong? FromPairingFeature(ReadOnlySpan<byte> report)
	{
		if (report.Length < 7 || report[0] != 0x09)
		{
			return null;
		}
		Span<byte> bytes = stackalloc byte[8];
		report.Slice(1, 6).CopyTo(bytes);
		ulong a = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
		return a == 0 ? null : a;
	}

	public static string Format(ulong address) => string.Join(":",
		Enumerable.Range(0, 6).Select(i => ((byte)(address >> (8 * (5 - i)))).ToString("X2", CultureInfo.InvariantCulture)));

	[GeneratedRegex(@"&([0-9A-Fa-f]{12})_C[0-9A-Fa-f]{8}$")]
	private static partial Regex InstanceAddress();
}

// Windows: the address comes from the parent of the HID collection (the BTHENUM service node, see DeviceInstance);
// the disconnect is IOCTL_BTH_DISCONNECT_DEVICE (bthioctl.h, public) on each radio handle from BluetoothFindFirstRadio.
public sealed class WindowsControllerPower : IControllerPower
{
	// CTL_CODE(FILE_DEVICE_BLUETOOTH 0x41, 0x03, METHOD_BUFFERED, FILE_ANY_ACCESS)
	public const uint IoctlBthDisconnectDevice = 0x41000C;

	public static WindowsControllerPower Instance { get; } = new();

	public ulong? FindAddress(HidDevice device)
	{
		string? instance = DeviceInstance.FromInterfacePath(device.Path);
		if (instance == null)
		{
			return null;
		}
		return BluetoothAddress.FromInstanceId(DeviceInstance.Parent(instance)) ?? BluetoothAddress.FromInstanceId(instance);
	}

	public Task<bool> DisconnectAsync(ulong address, CancellationToken cancel = default) =>
		OperatingSystem.IsWindows() ? Task.Run(() => Disconnect(address), cancel) : Task.FromResult(false);

	private static bool Disconnect(ulong address)
	{
		var p = new FindRadioParams { Size = Marshal.SizeOf<FindRadioParams>() };
		IntPtr find = BluetoothFindFirstRadio(ref p, out IntPtr radio);
		if (find == IntPtr.Zero)
		{
			return false;
		}
		bool done = false;
		try
		{
			do
			{
				done |= DeviceIoControl(radio, IoctlBthDisconnectDevice, ref address, sizeof(ulong), IntPtr.Zero, 0, out _, IntPtr.Zero);
				CloseHandle(radio);
			}
			while (!done && BluetoothFindNextRadio(find, out radio));
		}
		finally
		{
			BluetoothFindRadioClose(find);
		}
		return done;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FindRadioParams
	{
		public int Size;
	}

	[DllImport("BluetoothApis.dll", SetLastError = true)]
	private static extern IntPtr BluetoothFindFirstRadio(ref FindRadioParams p, out IntPtr radio);

	[DllImport("BluetoothApis.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool BluetoothFindNextRadio(IntPtr find, out IntPtr radio);

	[DllImport("BluetoothApis.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool BluetoothFindRadioClose(IntPtr find);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeviceIoControl(IntPtr device, uint code, ref ulong input, int inputSize, IntPtr output, int outputSize,
		out int returned, IntPtr overlapped);

	[DllImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);
}
