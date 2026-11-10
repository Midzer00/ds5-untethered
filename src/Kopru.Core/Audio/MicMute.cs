// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Kopru.Core.Audio;

// The controller's mic (mute) button as the microphone switch: each press (the edge, not repeats while held, and not a
// second press within Debounce) raises Pressed; the button bit is always cleared, so games never see it. It is only
// given input while the bridge handles the button (see BridgeHostMic). Process runs on the input thread; Pressed is
// raised there (handlers must be short).
public sealed class MicButton(TimeProvider? time = null)
{
	// Body byte 9 (Buttons2) bit 2: the mute button (same in USB report 0x01 from byte 1 and BT 0x31 from byte 2).
	public const int ButtonByte = 9;
	public const byte ButtonBit = 0x04;

	public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

	private readonly TimeProvider time = time ?? TimeProvider.System;

	private bool wasDown;

	private long lastPress;

	private bool pressedBefore;

	public event Action? Pressed;

	public void Process(Span<byte> body)
	{
		if (body.Length <= ButtonByte)
		{
			return;
		}
		bool down = (body[ButtonByte] & ButtonBit) != 0;
		bool press = down && !wasDown;
		wasDown = down;
		body[ButtonByte] &= unchecked((byte)~ButtonBit);
		if (!press)
		{
			return;
		}
		long now = time.GetTimestamp();
		if (pressedBefore && time.GetElapsedTime(lastPress, now) < Debounce)
		{
			return;
		}
		pressedBefore = true;
		lastPress = now;
		Pressed?.Invoke();
	}
}
// Mutes the default Windows capture device (optional, "also mute the Windows microphone": the mic button turning the
// controller mic off mutes it, turning it on unmutes it).
public interface IWindowsMicMute
{
	void SetMuted(bool muted);
}

// Core Audio: IMMDeviceEnumerator -> default capture endpoint (communications role) -> IAudioEndpointVolume.SetMute.
public sealed class CoreAudioMicMute : IWindowsMicMute
{
	private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
	private static readonly Guid EndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
	private const int Capture = 1;          // eCapture
	private const int Communications = 2;   // eCommunications
	private const int ClsctxAll = 23;

	public void SetMuted(bool muted)
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}
		object? enumeratorObject = null;
		IMMDevice? device = null;
		IAudioEndpointVolume? volume = null;
		try
		{
			enumeratorObject = Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClsid)!);
			var enumerator = (IMMDeviceEnumerator)enumeratorObject!;
			Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(Capture, Communications, out device));
			Guid iid = EndpointVolumeIid;
			Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object o));
			volume = (IAudioEndpointVolume)o;
			Guid context = Guid.Empty;
			Marshal.ThrowExceptionForHR(volume.SetMute(muted, ref context));
		}
		finally
		{
			Release(volume);
			Release(device);
			Release(enumeratorObject);
		}
	}

	private static void Release(object? o)
	{
		if (o != null && OperatingSystem.IsWindows() && Marshal.IsComObject(o))
		{
			Marshal.ReleaseComObject(o);
		}
	}

	[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDeviceEnumerator
	{
		int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

		[PreserveSig]
		int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
	}

	[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDevice
	{
		[PreserveSig]
		int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
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
	}
}
