// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Kopru.Core.Audio;

// Default audio endpoints on Windows. Reading and notifications use the documented MMDevice API (IMMDeviceEnumerator,
// IMMNotificationClient, IPropertyStore). Setting the default has no public API: it goes through the undocumented
// IPolicyConfig interface of the CPolicyConfigClient class (Windows 7 to 11), declared here from its publicly listed
// IID, CLSID and method order. Any failure surfaces as an exception the caller handles (DefaultAudioGuard turns itself
// off for the run).
[SupportedOSPlatform("windows")]
public sealed class WindowsAudioDefaults : IAudioDefaultsSystem
{
	private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
	private static readonly Guid PolicyConfigClsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

	private const int DeviceStateActive = 0x1;
	private const int StgmRead = 0;
	private const int ENotFound = unchecked((int)0x80070490);
	private const ushort VtLpwstr = 31;

	// PKEY_Device_FriendlyName
	private static readonly PropertyKey FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
	private static readonly PropertyKey AdapterInstanceKey = new(PadAudioIdentity.AdapterInstanceFormat, PadAudioIdentity.AdapterInstancePid);

	private static readonly Guid EndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
	private const int ClsctxAll = 23;

	private readonly object sync = new();
	private IMMDeviceEnumerator? enumerator;
	// Kept alive while registered (the enumerator holds only a COM reference to them); one per listener.
	private readonly List<NotificationClient> clients = [];

	private IMMDeviceEnumerator Enumerator
	{
		get
		{
			lock (sync)
			{
				return enumerator ??= (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClsid, true)!)!;
			}
		}
	}

	public void Watch(IAudioDefaultsListener listener)
	{
		var c = new NotificationClient(listener);
		Marshal.ThrowExceptionForHR(Enumerator.RegisterEndpointNotificationCallback(c));
		lock (sync)
		{
			clients.Add(c);
		}
	}

	public bool? GetMute(string id)
	{
		bool? muted = null;
		WithVolume(id, v => muted = v.GetMute(out bool m) == 0 ? m : null);
		return muted;
	}

	public void SetMute(string id, bool muted)
	{
		WithVolume(id, v =>
		{
			Guid context = Guid.Empty;
			Marshal.ThrowExceptionForHR(v.SetMute(muted, ref context));
		});
	}

	private void WithVolume(string id, Action<IAudioEndpointVolume> use)
	{
		Marshal.ThrowExceptionForHR(Enumerator.GetDevice(id, out IMMDevice? device));
		object? o = null;
		try
		{
			Guid iid = EndpointVolumeIid;
			Marshal.ThrowExceptionForHR(device!.Activate(ref iid, ClsctxAll, IntPtr.Zero, out o));
			use((IAudioEndpointVolume)o);
		}
		finally
		{
			Release(o);
			Release(device);
		}
	}

	public string? GetDefault(AudioFlow flow, AudioRole role)
	{
		int hr = Enumerator.GetDefaultAudioEndpoint((int)flow, (int)role, out IMMDevice? device);
		if (hr == ENotFound || device == null)
		{
			return null;
		}
		try
		{
			Marshal.ThrowExceptionForHR(hr);
			Marshal.ThrowExceptionForHR(device.GetId(out string id));
			return id;
		}
		finally
		{
			Release(device);
		}
	}

	public AudioEndpointInfo? Describe(string id)
	{
		if (Enumerator.GetDevice(id, out IMMDevice? device) != 0 || device == null)
		{
			return null;
		}
		try
		{
			return Describe(device, id);
		}
		finally
		{
			Release(device);
		}
	}

	public IReadOnlyList<AudioEndpointInfo> Endpoints(AudioFlow flow)
	{
		Marshal.ThrowExceptionForHR(Enumerator.EnumAudioEndpoints((int)flow, DeviceStateActive, out IMMDeviceCollection? collection));
		var list = new List<AudioEndpointInfo>();
		try
		{
			Marshal.ThrowExceptionForHR(collection.GetCount(out uint count));
			for (uint i = 0; i < count; i++)
			{
				if (collection.Item(i, out IMMDevice? device) != 0 || device == null)
				{
					continue;
				}
				try
				{
					if (device.GetId(out string id) == 0 && Describe(device, id) is { } info)
					{
						list.Add(info);
					}
				}
				finally
				{
					Release(device);
				}
			}
		}
		finally
		{
			Release(collection);
		}
		return list;
	}

	public void SetDefault(string id, AudioRole role)
	{
		object? instance = null;
		try
		{
			instance = Activator.CreateInstance(Type.GetTypeFromCLSID(PolicyConfigClsid, true)!);
			// InvalidCastException when this Windows build has no IPolicyConfig with this IID.
			var policy = (IPolicyConfig)instance!;
			Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, (int)role));
		}
		finally
		{
			Release(instance);
		}
	}

	private static AudioEndpointInfo? Describe(IMMDevice device, string id)
	{
		if (device is not IMMEndpoint endpoint || endpoint.GetDataFlow(out int flow) != 0 || device.GetState(out int state) != 0)
		{
			return null;
		}
		string name = string.Empty;
		string? adapter = null;
		if (device.OpenPropertyStore(StgmRead, out IPropertyStore? store) == 0 && store != null)
		{
			try
			{
				name = ReadString(store, FriendlyNameKey) ?? string.Empty;
				adapter = ReadString(store, AdapterInstanceKey);
			}
			finally
			{
				Release(store);
			}
		}
		return new AudioEndpointInfo(id, (AudioFlow)flow, (state & DeviceStateActive) != 0, name, Identify(adapter, name));
	}

	// The same identification for the guard and "Kopru.Probe audiodefaults".
	public static PadAudioMatch Identify(string? adapterInstance, string name)
	{
		if (!string.IsNullOrEmpty(adapterInstance) && DeviceTree.Chain(PadAudioIdentity.StripPrefix(adapterInstance)) is { Count: > 0 } chain)
		{
			return PadAudioIdentity.Classify(chain);
		}
		return PadAudioIdentity.NameMatch(name);
	}

	private static string? ReadString(IPropertyStore store, PropertyKey key)
	{
		PropertyKey k = key;
		if (store.GetValue(ref k, out PropVariant value) != 0)
		{
			return null;
		}
		try
		{
			return value.Type == VtLpwstr && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null;
		}
		finally
		{
			PropVariantClear(ref value);
		}
	}

	private static void Release(object? o)
	{
		if (o != null && Marshal.IsComObject(o))
		{
			Marshal.ReleaseComObject(o);
		}
	}

	[DllImport("ole32.dll")]
	private static extern int PropVariantClear(ref PropVariant value);

	[StructLayout(LayoutKind.Sequential)]
	private readonly struct PropertyKey(Guid format, int id)
	{
		public readonly Guid Format = format;
		public readonly int Id = id;
	}

	// PROPVARIANT: the type tag, then the value (a pointer for VT_LPWSTR); sized for 64-bit.
	[StructLayout(LayoutKind.Explicit)]
	private struct PropVariant
	{
		[FieldOffset(0)]
		public ushort Type;

		[FieldOffset(8)]
		public IntPtr Pointer;

		[FieldOffset(16)]
		public IntPtr Extra;
	}

	[ClassInterface(ClassInterfaceType.None)]
	private sealed class NotificationClient(IAudioDefaultsListener listener) : IMMNotificationClient
	{
		public void OnDeviceStateChanged(string id, int state) => Forward(() => listener.EndpointChanged(id));

		public void OnDeviceAdded(string id) => Forward(() => listener.EndpointChanged(id));

		public void OnDeviceRemoved(string id) => Forward(() => listener.EndpointChanged(id));

		public void OnDefaultDeviceChanged(int flow, int role, string? id)
		{
			if (flow is 0 or 1 && role is >= 0 and <= 2)
			{
				Forward(() => listener.DefaultChanged((AudioFlow)flow, (AudioRole)role, id));
			}
		}

		public void OnPropertyValueChanged(string id, PropertyKey key)
		{
		}

		// Never let an exception go back into the audio service.
		private static void Forward(Action action)
		{
			try
			{
				action();
			}
			catch (Exception)
			{
				// Dropped; the guard looks at every default again on the next arrival.
			}
		}
	}

	[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDeviceEnumerator
	{
		[PreserveSig]
		int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);

		[PreserveSig]
		int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);

		[PreserveSig]
		int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

		[PreserveSig]
		int RegisterEndpointNotificationCallback(IMMNotificationClient client);

		[PreserveSig]
		int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
	}

	[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDeviceCollection
	{
		[PreserveSig]
		int GetCount(out uint count);

		[PreserveSig]
		int Item(uint index, out IMMDevice? device);
	}

	[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDevice
	{
		[PreserveSig]
		int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

		[PreserveSig]
		int OpenPropertyStore(int access, out IPropertyStore? store);

		[PreserveSig]
		int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

		[PreserveSig]
		int GetState(out int state);
	}

	[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioEndpointVolume
	{
		int RegisterControlChangeNotify(IntPtr notify);
		int UnregisterControlChangeNotify(IntPtr notify);
		int GetChannelCount(out uint count);
		int SetMasterVolumeLevel(float level, ref Guid context);
		int SetMasterVolumeLevelScalar(float level, ref Guid context);
		int GetMasterVolumeLevel(out float level);
		int GetMasterVolumeLevelScalar(out float level);
		int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
		int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
		int GetChannelVolumeLevel(uint channel, out float level);
		int GetChannelVolumeLevelScalar(uint channel, out float level);

		[PreserveSig]
		int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);

		[PreserveSig]
		int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
	}

	[ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMEndpoint
	{
		[PreserveSig]
		int GetDataFlow(out int dataFlow);
	}

	[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPropertyStore
	{
		[PreserveSig]
		int GetCount(out uint count);

		[PreserveSig]
		int GetAt(uint index, out PropertyKey key);

		[PreserveSig]
		int GetValue(ref PropertyKey key, out PropVariant value);
	}

	[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMNotificationClient
	{
		void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);

		void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);

		void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);

		void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);

		void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
	}

	// Undocumented. Only SetDefaultEndpoint is called; the slots before it keep the method order (the arguments of those
	// are never used, so they are declared as plain pointers).
	[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IPolicyConfig
	{
		[PreserveSig]
		int GetMixFormat(IntPtr deviceId, IntPtr format);

		[PreserveSig]
		int GetDeviceFormat(IntPtr deviceId, int isDefault, IntPtr format);

		[PreserveSig]
		int ResetDeviceFormat(IntPtr deviceId);

		[PreserveSig]
		int SetDeviceFormat(IntPtr deviceId, IntPtr endpointFormat, IntPtr mixFormat);

		[PreserveSig]
		int GetProcessingPeriod(IntPtr deviceId, int isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);

		[PreserveSig]
		int SetProcessingPeriod(IntPtr deviceId, IntPtr period);

		[PreserveSig]
		int GetShareMode(IntPtr deviceId, IntPtr mode);

		[PreserveSig]
		int SetShareMode(IntPtr deviceId, IntPtr mode);

		[PreserveSig]
		int GetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);

		[PreserveSig]
		int SetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);

		[PreserveSig]
		int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

		[PreserveSig]
		int SetEndpointVisibility(IntPtr deviceId, int visible);
	}

	// The device tree above an audio device (cfgmgr32).
	private static class DeviceTree
	{
		private const int CrSuccess = 0;
		private const int MaxDeviceIdLength = 200;
		private const int MaxDepth = 8;
		private const int CmDrpHardwareId = 0x02;
		private const int CmDrpService = 0x05;
		private const int CmDrpLocationInformation = 0x0E;

		// Null when the device is not present or the tree cannot be read.
		public static IReadOnlyList<DeviceNodeInfo>? Chain(string instanceId)
		{
			try
			{
				if (CM_Locate_DevNodeW(out uint node, instanceId, 0) != CrSuccess)
				{
					return null;
				}
				var chain = new List<DeviceNodeInfo>();
				for (int depth = 0; depth < MaxDepth; depth++)
				{
					string? id = DeviceId(node);
					if (id == null)
					{
						return null;
					}
					chain.Add(new DeviceNodeInfo(id, MultiString(node, CmDrpHardwareId), String(node, CmDrpService), String(node, CmDrpLocationInformation)));
					if (CM_Get_Parent(out uint parent, node, 0) != CrSuccess)
					{
						break;
					}
					node = parent;
				}
				return chain;
			}
			catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
			{
				return null;
			}
		}

		private static string? DeviceId(uint node)
		{
			char[] buffer = new char[MaxDeviceIdLength + 1];
			return CM_Get_Device_IDW(node, buffer, buffer.Length, 0) == CrSuccess ? new string(buffer).TrimEnd('\0') : null;
		}

		private static byte[]? Raw(uint node, int property)
		{
			int size = 0;
			CM_Get_DevNode_Registry_PropertyW(node, property, out _, null, ref size, 0);
			if (size <= 0)
			{
				return null;
			}
			byte[] buffer = new byte[size];
			return CM_Get_DevNode_Registry_PropertyW(node, property, out _, buffer, ref size, 0) == CrSuccess ? buffer : null;
		}

		private static string? String(uint node, int property) =>
			Raw(node, property) is { } raw ? System.Text.Encoding.Unicode.GetString(raw).TrimEnd('\0') : null;

		private static IReadOnlyList<string> MultiString(uint node, int property) =>
			Raw(node, property) is { } raw
				? System.Text.Encoding.Unicode.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries)
				: [];

		[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
		private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

		[DllImport("cfgmgr32.dll")]
		private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

		[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
		private static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, int bufferLen, uint flags);

		[DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
		private static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, int property, out int regDataType, byte[]? buffer, ref int length, uint flags);
	}
}
