// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Kopru.Core.Hid;
using Kopru.Core.Localization;
using Kopru.Core.Setup;
using Microsoft.Win32;

namespace Kopru.Core.Health;

public enum ConnectionKind
{
	// The radio exists but is off (not connectable).
	BluetoothOff,
	// No Bluetooth radio on this PC.
	NoRadio,
	// No DualSense is paired in Windows.
	NeverPaired,
	// Paired, but off or asleep (no HID device).
	PairedNotConnected,
	ConnectedBluetooth,
	// On a USB cable: games see it as a wired pad already, the bridge is not needed.
	ConnectedUsb
}

// Where the controller stands. IsEdge: the connected (or paired) controller is a DualSense Edge.
public sealed record ConnectionState(ConnectionKind Kind, bool IsEdge = false)
{
	public static ConnectionState Unknown { get; } = new(ConnectionKind.NeverPaired);

	public bool IsConnected => Kind is ConnectionKind.ConnectedBluetooth or ConnectionKind.ConnectedUsb;

	public string Label(Language? l = null) => Kind switch
	{
		ConnectionKind.BluetoothOff => Text.ConnectionBluetoothOff(l),
		ConnectionKind.NoRadio => Text.ConnectionNoRadio(l),
		ConnectionKind.NeverPaired => Text.ConnectionNeverPaired(l),
		ConnectionKind.PairedNotConnected => Text.ConnectionPairedNotConnected(l),
		ConnectionKind.ConnectedBluetooth => Text.ConnectionOnBluetooth(l),
		_ => Text.ConnectionOnUsb(l)
	};

	public string Reason(Language? l = null) => Kind switch
	{
		ConnectionKind.BluetoothOff => Text.ConnectionBluetoothOffReason(l),
		ConnectionKind.NoRadio => Text.ConnectionNoRadioReason(l),
		ConnectionKind.NeverPaired => Text.ConnectionNeverPairedReason(l),
		ConnectionKind.PairedNotConnected => Text.ConnectionPairedNotConnectedReason(l),
		ConnectionKind.ConnectedBluetooth => Text.ConnectionBluetoothReason(l),
		_ => Text.ConnectionUsbReason(l)
	};

	// A HID controller wins (Bluetooth before USB); then the radio; then the Windows paired-device list.
	public static ConnectionState Detect(RadioState radio, IEnumerable<HidDevice> controllers, IEnumerable<PairedController> paired)
	{
		List<HidDevice> hid = [.. controllers];
		if (hid.FirstOrDefault(d => d.Transport == Transport.Bluetooth) is { } bt)
		{
			return new(ConnectionKind.ConnectedBluetooth, bt.IsEdge);
		}
		if (hid.FirstOrDefault() is { } usb)
		{
			return new(ConnectionKind.ConnectedUsb, usb.IsEdge);
		}
		if (radio == RadioState.Missing)
		{
			return new(ConnectionKind.NoRadio);
		}
		if (radio == RadioState.Off)
		{
			return new(ConnectionKind.BluetoothOff);
		}
		List<PairedController> list = [.. paired];
		return list.Count == 0
			? new(ConnectionKind.NeverPaired)
			: new(ConnectionKind.PairedNotConnected, list.Any(p => p.IsEdge));
	}
}

// A DualSense in the Windows Bluetooth paired-device list. Address = 48-bit Bluetooth address.
public sealed record PairedController(string Name, ulong Address, bool Connected, bool IsEdge);

public interface IPairedControllers
{
	IReadOnlyList<PairedController> Find();
}

// Paired DualSense controllers from BluetoothFindFirstDevice (remembered/authenticated devices, no inquiry). A device
// counts when its address appears in a BTHENUM HID entry with Sony's VID and a DualSense/Edge PID (the Bluetooth name
// "Wireless Controller" alone is shared with the DualShock 4), or when its name says DualSense.
public sealed class WindowsPairedControllers : IPairedControllers
{
	private const string BthEnum = @"SYSTEM\CurrentControlSet\Enum\BTHENUM";

	[SupportedOSPlatform("windows")]
	public IReadOnlyList<PairedController> Find()
	{
		Dictionary<string, bool> sony = SonyAddresses();
		List<PairedController> found = [];
		var search = new SearchParams
		{
			Size = Marshal.SizeOf<SearchParams>(),
			ReturnAuthenticated = true,
			ReturnRemembered = true,
			ReturnConnected = true,
			ReturnUnknown = false,
			IssueInquiry = false
		};
		var info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
		IntPtr find = BluetoothFindFirstDevice(ref search, ref info);
		if (find == IntPtr.Zero)
		{
			return found;
		}
		try
		{
			do
			{
				string address = info.Address.ToString("X12", System.Globalization.CultureInfo.InvariantCulture);
				string name = info.Name ?? string.Empty;
				bool byPid = sony.TryGetValue(address, out bool edge);
				if (byPid || name.Contains("DualSense", StringComparison.OrdinalIgnoreCase))
				{
					edge |= name.Contains("Edge", StringComparison.OrdinalIgnoreCase);
					found.Add(new PairedController(name, info.Address, info.Connected, edge));
				}
				info = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
			}
			while (BluetoothFindNextDevice(find, ref info));
		}
		finally
		{
			BluetoothFindDeviceClose(find);
		}
		return found;
	}

	// Bluetooth addresses (12 hex digits) of BTHENUM entries with VID 054C and a DualSense PID -> is Edge.
	[SupportedOSPlatform("windows")]
	public static Dictionary<string, bool> SonyAddresses()
	{
		Dictionary<string, bool> result = new(StringComparer.OrdinalIgnoreCase);
		try
		{
			using RegistryKey? root = Registry.LocalMachine.OpenSubKey(BthEnum);
			if (root == null)
			{
				return result;
			}
			foreach (string key in root.GetSubKeyNames())
			{
				if (MatchPid(key) is not { } edge)
				{
					continue;
				}
				using RegistryKey? entry = root.OpenSubKey(key);
				foreach (string instance in entry?.GetSubKeyNames() ?? [])
				{
					if (AddressFromInstance(instance) is { } address)
					{
						result[address] = result.GetValueOrDefault(address) || edge;
					}
				}
			}
		}
		catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
		{
			// Name match only.
		}
		return result;
	}

	// "{00001124-...}_VID&0002054c_PID&0ce6" -> false (DualSense), "..._PID&0df2" -> true (Edge), else null.
	public static bool? MatchPid(string key)
	{
		if (!key.Contains("054c", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		if (key.Contains("PID&0ce6", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		return key.Contains("PID&0df2", StringComparison.OrdinalIgnoreCase) ? true : null;
	}

	// Instance names look like "9&2d1b0b2a&0&A0B1C2D3E4F5_C00000000": the address is the 12 hex digits before "_".
	public static string? AddressFromInstance(string instance)
	{
		int underscore = instance.LastIndexOf('_');
		string head = underscore >= 0 ? instance[..underscore] : instance;
		int amp = head.LastIndexOf('&');
		string candidate = head[(amp + 1)..];
		return candidate.Length == 12 && candidate.All(Uri.IsHexDigit) ? candidate.ToUpperInvariant() : null;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SearchParams
	{
		public int Size;
		[MarshalAs(UnmanagedType.Bool)] public bool ReturnAuthenticated;
		[MarshalAs(UnmanagedType.Bool)] public bool ReturnRemembered;
		[MarshalAs(UnmanagedType.Bool)] public bool ReturnUnknown;
		[MarshalAs(UnmanagedType.Bool)] public bool ReturnConnected;
		[MarshalAs(UnmanagedType.Bool)] public bool IssueInquiry;
		public byte TimeoutMultiplier;
		public IntPtr Radio;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct SystemTime
	{
		public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct DeviceInfo
	{
		public int Size;
		public ulong Address;
		public uint ClassOfDevice;
		[MarshalAs(UnmanagedType.Bool)] public bool Connected;
		[MarshalAs(UnmanagedType.Bool)] public bool Remembered;
		[MarshalAs(UnmanagedType.Bool)] public bool Authenticated;
		public SystemTime LastSeen;
		public SystemTime LastUsed;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string? Name;
	}

	[DllImport("BluetoothApis.dll", SetLastError = true)]
	private static extern IntPtr BluetoothFindFirstDevice(ref SearchParams search, ref DeviceInfo info);

	[DllImport("BluetoothApis.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool BluetoothFindNextDevice(IntPtr find, ref DeviceInfo info);

	[DllImport("BluetoothApis.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool BluetoothFindDeviceClose(IntPtr find);
}
