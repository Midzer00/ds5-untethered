// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Kopru.Core.Hid;

// Device instance IDs ("instance paths") of HID devices, as HidHide expects them.
//
// What HidHide hides: the HID collection device node (class HIDClass, enumerator "HID\"), not the Bluetooth
// transport node. For a Bluetooth DualSense the tree is:
//   BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\...   (BT HID service, parent)
//     HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\...     (HID collection, hide this one)
// Verified read-only on the dev machine: "HidHideCLI --dev-list" shows the HID\{00001124-...} path while another
// app hides the controller. The {00001124-...} GUID is the Bluetooth HID profile service class.
//
// Our virtual wired pad (usbip-win2) shares VID 054C / PID 0CE6 but enumerates under the USB bus:
//   USB\VID_054C&PID_0CE6\<serial>  ->  USB\VID_054C&PID_0CE6&MI_03\...  ->  HID\VID_054C&PID_0CE6&MI_03\...
// So its HID node starts with "HID\VID_" (USB-parented), never "HID\{00001124-". Only Bluetooth HID nodes are hidden.
public static class DeviceInstance
{
	public const string BluetoothHidServicePrefix = @"HID\{00001124-0000-1000-8000-00805F9B34FB}";

	// True when the instance ID is the HID collection of a Bluetooth HID device (the only kind we ever hide).
	public static bool IsBluetoothHid(string? instanceId) =>
		instanceId is not null && instanceId.StartsWith(BluetoothHidServicePrefix, StringComparison.OrdinalIgnoreCase);

	// True for USB-parented nodes, which includes our virtual pad. These must never be hidden.
	public static bool IsUsbDevice(string? instanceId) =>
		instanceId is not null &&
		(instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) || IsUsbHidCollection(instanceId));

	// True when the instance ID is the HID collection of a USB device ("HID\VID_..."). A real wired DualSense and our
	// own virtual pad both look like this: the shape alone never tells them apart, so callers must never pass ours
	// (its 0x09 answer, PairingInfo.VirtualAddress, is how the cable code knows it from the real wired pad).
	public static bool IsUsbHidCollection(string? instanceId) =>
		instanceId is not null && instanceId.StartsWith(@"HID\VID_", StringComparison.OrdinalIgnoreCase);

	// The instance ID spelled inside an interface path, without asking Windows: "\\?\HID#<hardware id>#<instance>#{class}"
	// is the instance ID "HID\<hardware id>\<instance>" with '\' written as '#' and the interface class GUID added. Case
	// may differ from what Windows reports (ids are compared ignoring case). Null when the path is not in that form.
	public static string? FromInterfacePathText(string interfacePath)
	{
		if (string.IsNullOrEmpty(interfacePath))
		{
			return null;
		}
		string rest = interfacePath.StartsWith(@"\\?\", StringComparison.Ordinal) || interfacePath.StartsWith(@"\??\", StringComparison.Ordinal)
			? interfacePath[4..]
			: interfacePath;
		int classGuid = rest.LastIndexOf("#{", StringComparison.Ordinal);
		if (classGuid <= 0 || !rest.EndsWith('}'))
		{
			return null;
		}
		string[] parts = rest[..classGuid].Split('#');
		return parts.Length == 3 && parts.All(p => p.Length > 0) ? string.Join('\\', parts) : null;
	}

	// Instance ID of the device that owns a device interface path (e.g. the path HidConnection opens).
	public static string? FromInterfacePath(string interfacePath)
	{
		if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(interfacePath))
		{
			return null;
		}
		DevPropKey key = InstanceIdKey;
		int size = 0;
		int cr = CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, null, ref size, 0);
		if (cr != CrBufferSmall || size <= 0)
		{
			return null;
		}
		byte[] buffer = new byte[size];
		cr = CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out uint type, buffer, ref size, 0);
		if (cr != CrSuccess || type != DevPropTypeString)
		{
			return null;
		}
		return System.Text.Encoding.Unicode.GetString(buffer, 0, size).TrimEnd('\0');
	}

	// Instance ID of the parent node (for a BT DualSense: the BTHENUM\{00001124-...} node). Diagnostic use only.
	public static string? Parent(string instanceId)
	{
		if (!OperatingSystem.IsWindows() || CM_Locate_DevNodeW(out uint node, instanceId, 0) != CrSuccess ||
			CM_Get_Parent(out uint parent, node, 0) != CrSuccess)
		{
			return null;
		}
		char[] buffer = new char[MaxDeviceIdLength + 1];
		return CM_Get_Device_IDW(parent, buffer, buffer.Length, 0) == CrSuccess ? new string(buffer).TrimEnd('\0') : null;
	}

	// Instance ID of the parent node as Windows last knew it, also for a device that is not present now (a paired
	// Bluetooth controller that is off keeps its HID node as a non-present "phantom" node, with its parent recorded).
	// Read-only: DEVPKEY_Device_Parent, then DEVPKEY_Device_LastKnownParent, then CM_Get_Parent. Device IDs are
	// case-insensitive, but CM_Locate_DevNode refuses an all-lowercase one, so the upper-case spelling is tried too.
	public static string? KnownParent(string instanceId)
	{
		if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(instanceId) || instanceId.Length > MaxDeviceIdLength)
		{
			return null;
		}
		if (CM_Locate_DevNodeW(out uint node, instanceId, LocatePhantom) != CrSuccess &&
			CM_Locate_DevNodeW(out node, instanceId.ToUpperInvariant(), LocatePhantom) != CrSuccess)
		{
			return null;
		}
		if (StringProperty(node, ParentKey) is { Length: > 0 } parent)
		{
			return parent;
		}
		if (StringProperty(node, LastKnownParentKey) is { Length: > 0 } lastKnown)
		{
			return lastKnown;
		}
		char[] buffer = new char[MaxDeviceIdLength + 1];
		return CM_Get_Parent(out uint p, node, 0) == CrSuccess && CM_Get_Device_IDW(p, buffer, buffer.Length, 0) == CrSuccess
			? new string(buffer).TrimEnd('\0')
			: null;
	}

	private static string? StringProperty(uint node, DevPropKey key)
	{
		int size = 0;
		if (CM_Get_DevNode_PropertyW(node, ref key, out _, null, ref size, 0) != CrBufferSmall || size <= 0 || size > 4096)
		{
			return null;
		}
		byte[] buffer = new byte[size];
		if (CM_Get_DevNode_PropertyW(node, ref key, out uint type, buffer, ref size, 0) != CrSuccess || type != DevPropTypeString)
		{
			return null;
		}
		return System.Text.Encoding.Unicode.GetString(buffer, 0, size).TrimEnd('\0');
	}

	private const int CrSuccess = 0;
	private const int CrBufferSmall = 0x1A;
	private const uint DevPropTypeString = 0x12;
	private const int MaxDeviceIdLength = 200;
	// CM_LOCATE_DEVNODE_PHANTOM: also a node whose device is not present now.
	private const uint LocatePhantom = 0x1;

	// DEVPKEY_Device_Parent
	private static readonly DevPropKey ParentKey = new()
	{
		Fmtid = new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"),
		Pid = 8
	};

	// DEVPKEY_Device_LastKnownParent
	private static readonly DevPropKey LastKnownParentKey = new()
	{
		Fmtid = new Guid("83da6326-97a6-4088-9453-a1923f573b29"),
		Pid = 10
	};

	// DEVPKEY_Device_InstanceId
	private static readonly DevPropKey InstanceIdKey = new()
	{
		Fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"),
		Pid = 256
	};

	[StructLayout(LayoutKind.Sequential)]
	private struct DevPropKey
	{
		public Guid Fmtid;
		public uint Pid;
	}

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Get_Device_Interface_PropertyW(string pszDeviceInterface, ref DevPropKey propertyKey,
		out uint propertyType, byte[]? propertyBuffer, ref int propertyBufferSize, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

	[DllImport("cfgmgr32.dll")]
	private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey propertyKey, out uint propertyType, byte[]? propertyBuffer,
		ref int propertyBufferSize, uint flags);

	[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
	private static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, int bufferLen, uint flags);
}
