// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kopru.Probe;

// One active Windows audio endpoint: friendly name, default-device flag and endpoint volume (the endpoint id stays inside
// WasapiCapture and is never printed). Volume is 0..1 (null when it could not be read).
internal sealed record CaptureEndpoint(int Index, string Name, bool IsDefault = false, float? Volume = null, bool? Muted = null);

// The shared-mode mix format of an endpoint.
internal sealed record MixFormat(int Rate, int Channels, int Bits, bool Float, int BlockAlign);

// What a shared-mode capture returned: samples normalised to -1..1, interleaved. StartTimestamp: Stopwatch ticks when
// the stream was started (sample 0 is about then).
internal sealed record CaptureResult(int Rate, int Channels, int Bits, bool Float, float[] Samples, int Packets, int SilentPackets,
	long StartTimestamp = 0)
{
	public int Frames => Channels == 0 ? 0 : Samples.Length / Channels;
}

// What a shared-mode render played. StartTimestamp: Stopwatch ticks when the stream was started (schedule time 0).
internal sealed record RenderResult(MixFormat Format, long FramesWritten, long StartTimestamp, int Underruns);

// Core Audio (MMDevice + WASAPI shared mode) through COM interop, in the style of Kopru.Core's CoreAudioMicMute:
// IMMDeviceEnumerator -> active endpoints -> IAudioClient (mix format, shared, polled) -> IAudioCaptureClient /
// IAudioRenderClient; IAudioEndpointVolume for volume and mute.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WasapiCapture
{
	private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
	private static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
	private static readonly Guid CaptureClientIid = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
	private static readonly Guid RenderClientIid = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
	private static readonly Guid EndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
	private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00AA00389B71");
	private const int Render = 0;           // eRender
	private const int Capture = 1;          // eCapture
	private const int ConsoleRole = 0;      // eConsole
	private const int DeviceStateActive = 1;
	private const int ClsctxAll = 23;
	private const int SharedMode = 0;       // AUDCLNT_SHAREMODE_SHARED
	private const int BufferFlagSilent = 2; // AUDCLNT_BUFFERFLAGS_SILENT
	private const ushort VtLpwstr = 31;

	public static IReadOnlyList<CaptureEndpoint> ListEndpoints(bool render = false)
	{
		List<CaptureEndpoint> found = [];
		string? defaultId = DefaultId(render);
		WithEndpoints(render, (index, device) =>
		{
			string? id = device.GetId(out string i) == 0 ? i : null;
			(float? volume, bool? muted) = VolumeOf(device);
			found.Add(new CaptureEndpoint(index, FriendlyName(device) ?? $"(endpoint {index}, no name)",
				id != null && id == defaultId, volume, muted));
			return false;
		});
		return found;
	}

	// Records `duration` from the capture endpoint with this index (as ListEndpoints numbered it), in its mix format.
	public static CaptureResult Record(int endpointIndex, TimeSpan duration)
	{
		CaptureResult? result = null;
		WithEndpoints(false, (index, device) =>
		{
			if (index != endpointIndex)
			{
				return false;
			}
			result = RecordFrom(device, duration);
			return true;
		});
		return result ?? throw new InvalidOperationException("capture endpoint went away");
	}

	// Plays `duration` on the render endpoint with this index: source(t) gives the sample (-1..1) at t seconds after the
	// stream start, written to the first two channels only (any further channels, e.g. a DualSense's haptics pair, stay 0).
	public static RenderResult Play(int endpointIndex, TimeSpan duration, Func<double, float> source)
	{
		RenderResult? result = null;
		WithEndpoints(true, (index, device) =>
		{
			if (index != endpointIndex)
			{
				return false;
			}
			result = PlayTo(device, duration, source);
			return true;
		});
		return result ?? throw new InvalidOperationException("render endpoint went away");
	}

	private static MixFormat ReadFormat(IntPtr format)
	{
		ushort tag = (ushort)Marshal.ReadInt16(format, 0);
		int channels = (ushort)Marshal.ReadInt16(format, 2);
		int rate = Marshal.ReadInt32(format, 4);
		int blockAlign = (ushort)Marshal.ReadInt16(format, 12);
		int bits = (ushort)Marshal.ReadInt16(format, 14);
		bool isFloat = tag == 3;
		if (tag == 0xFFFE)
		{
			byte[] sub = new byte[16];
			Marshal.Copy(format + 24, sub, 0, 16);
			isFloat = new Guid(sub) == FloatSubFormat;
		}
		return new MixFormat(rate, channels, bits, isFloat, blockAlign);
	}

	private static CaptureResult RecordFrom(IMMDevice device, TimeSpan duration)
	{
		IAudioClient? client = null;
		IAudioCaptureClient? capture = null;
		IntPtr format = IntPtr.Zero;
		try
		{
			Guid iid = AudioClientIid;
			Check(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object o));
			client = (IAudioClient)o;
			Check(client.GetMixFormat(out format));
			MixFormat f = ReadFormat(format);
			// 1 s buffer, polled every 10 ms.
			Check(client.Initialize(SharedMode, 0, 10_000_000, 0, format, IntPtr.Zero));
			Guid captureIid = CaptureClientIid;
			Check(client.GetService(ref captureIid, out object c));
			capture = (IAudioCaptureClient)c;

			List<float> samples = [];
			byte[] bytes = [];
			int packets = 0, silent = 0;
			Check(client.Start());
			long started = Stopwatch.GetTimestamp();
			Stopwatch clock = Stopwatch.StartNew();
			while (clock.Elapsed < duration)
			{
				Thread.Sleep(10);
				Check(capture.GetNextPacketSize(out uint next));
				while (next > 0)
				{
					Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));
					int length = (int)frames * f.BlockAlign;
					packets++;
					if ((flags & BufferFlagSilent) != 0)
					{
						silent++;
						samples.AddRange(new float[(int)frames * f.Channels]);
					}
					else
					{
						if (bytes.Length < length)
						{
							bytes = new byte[length];
						}
						Marshal.Copy(data, bytes, 0, length);
						Decode(bytes.AsSpan(0, length), f.Bits, f.BlockAlign / Math.Max(1, f.Channels), f.Float, samples);
					}
					Check(capture.ReleaseBuffer(frames));
					Check(capture.GetNextPacketSize(out next));
				}
			}
			client.Stop();
			return new CaptureResult(f.Rate, f.Channels, f.Bits, f.Float, [.. samples], packets, silent, started);
		}
		finally
		{
			if (format != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(format);
			}
			Release(capture);
			Release(client);
		}
	}

	private static RenderResult PlayTo(IMMDevice device, TimeSpan duration, Func<double, float> source)
	{
		IAudioClient? client = null;
		IAudioRenderClient? render = null;
		IntPtr format = IntPtr.Zero;
		try
		{
			Guid iid = AudioClientIid;
			Check(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object o));
			client = (IAudioClient)o;
			Check(client.GetMixFormat(out format));
			MixFormat f = ReadFormat(format);
			int bytesPerSample = f.BlockAlign / Math.Max(1, f.Channels);
			// 200 ms buffer, topped up every 10 ms.
			Check(client.Initialize(SharedMode, 0, 2_000_000, 0, format, IntPtr.Zero));
			Check(client.GetBufferSize(out uint bufferFrames));
			Guid renderIid = RenderClientIid;
			Check(client.GetService(ref renderIid, out object r));
			render = (IAudioRenderClient)r;

			long total = (long)(duration.TotalSeconds * f.Rate);
			long written = 0;
			int underruns = 0;
			byte[] bytes = new byte[bufferFrames * f.BlockAlign];
			int Fill(uint frames)
			{
				Array.Clear(bytes, 0, (int)frames * f.BlockAlign);
				for (int i = 0; i < frames; i++)
				{
					long n = written + i;
					float v = n < total ? source(n / (double)f.Rate) : 0f;
					for (int ch = 0; ch < Math.Min(2, f.Channels); ch++)
					{
						Encode(v, bytes.AsSpan(i * f.BlockAlign + ch * bytesPerSample, bytesPerSample), f.Float);
					}
				}
				return (int)frames * f.BlockAlign;
			}
			// Prefill the whole buffer, then start.
			Check(render.GetBuffer(bufferFrames, out IntPtr first));
			Marshal.Copy(bytes, 0, first, Fill(bufferFrames));
			Check(render.ReleaseBuffer(bufferFrames, 0));
			written += bufferFrames;
			Check(client.Start());
			long started = Stopwatch.GetTimestamp();
			while (written < total + bufferFrames)
			{
				Thread.Sleep(10);
				Check(client.GetCurrentPadding(out uint padding));
				if (padding == 0)
				{
					underruns++;
				}
				uint free = bufferFrames - padding;
				if (free == 0)
				{
					continue;
				}
				Check(render.GetBuffer(free, out IntPtr data));
				Marshal.Copy(bytes, 0, data, Fill(free));
				Check(render.ReleaseBuffer(free, 0));
				written += free;
			}
			client.Stop();
			return new RenderResult(f, Math.Min(written, total), started, underruns);
		}
		finally
		{
			if (format != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(format);
			}
			Release(render);
			Release(client);
		}
	}

	private static void Encode(float v, Span<byte> into, bool isFloat)
	{
		v = Math.Clamp(v, -1f, 1f);
		switch (isFloat, into.Length)
		{
			case (true, 4):
				BitConverter.TryWriteBytes(into, v);
				break;
			case (true, 8):
				BitConverter.TryWriteBytes(into, (double)v);
				break;
			case (false, 2):
				BitConverter.TryWriteBytes(into, (short)Math.Round(v * 32767));
				break;
			case (false, 3):
				int s24 = (int)Math.Round(v * 8388607);
				into[0] = (byte)s24;
				into[1] = (byte)(s24 >> 8);
				into[2] = (byte)(s24 >> 16);
				break;
			case (false, 4):
				BitConverter.TryWriteBytes(into, (int)Math.Round(v * 2147483647.0));
				break;
			default:
				throw new NotSupportedException($"{into.Length * 8}-bit {(isFloat ? "float" : "PCM")} samples");
		}
	}

	private static void Decode(ReadOnlySpan<byte> data, int bits, int bytesPerSample, bool isFloat, List<float> into)
	{
		for (int i = 0; i + bytesPerSample <= data.Length; i += bytesPerSample)
		{
			ReadOnlySpan<byte> s = data.Slice(i, bytesPerSample);
			float v = (isFloat, bytesPerSample) switch
			{
				(true, 4) => BitConverter.ToSingle(s),
				(true, 8) => (float)BitConverter.ToDouble(s),
				(false, 2) => BitConverter.ToInt16(s) / 32768f,
				(false, 3) => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
				(false, 4) => BitConverter.ToInt32(s) / 2147483648f,
				(false, 1) => (s[0] - 128) / 128f,
				_ => throw new NotSupportedException($"{bits}-bit {(isFloat ? "float" : "PCM")} samples")
			};
			into.Add(v);
		}
	}

	private static string? DefaultId(bool render)
	{
		object? enumeratorObject = null;
		IMMDevice? device = null;
		try
		{
			enumeratorObject = Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClsid)!);
			var enumerator = (IMMDeviceEnumerator)enumeratorObject!;
			if (enumerator.GetDefaultAudioEndpoint(render ? Render : Capture, ConsoleRole, out device) != 0)
			{
				return null;
			}
			return device.GetId(out string id) == 0 ? id : null;
		}
		catch (Exception)
		{
			return null;
		}
		finally
		{
			Release(device);
			Release(enumeratorObject);
		}
	}

	private static (float? Volume, bool? Muted) VolumeOf(IMMDevice device)
	{
		IAudioEndpointVolume? volume = null;
		try
		{
			Guid iid = EndpointVolumeIid;
			if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object o) != 0)
			{
				return (null, null);
			}
			volume = (IAudioEndpointVolume)o;
			float? level = volume.GetMasterVolumeLevelScalar(out float l) == 0 ? l : null;
			bool? muted = volume.GetMute(out bool m) == 0 ? m : null;
			return (level, muted);
		}
		catch (Exception)
		{
			return (null, null);
		}
		finally
		{
			Release(volume);
		}
	}

	// Calls visit(index, device) for each active endpoint of the flow until it returns true.
	private static void WithEndpoints(bool render, Func<int, IMMDevice, bool> visit)
	{
		object? enumeratorObject = null;
		IMMDeviceCollection? collection = null;
		try
		{
			enumeratorObject = Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClsid)!);
			var enumerator = (IMMDeviceEnumerator)enumeratorObject!;
			Check(enumerator.EnumAudioEndpoints(render ? Render : Capture, DeviceStateActive, out collection));
			Check(collection.GetCount(out uint count));
			for (uint i = 0; i < count; i++)
			{
				IMMDevice? device = null;
				try
				{
					Check(collection.Item(i, out device));
					if (visit((int)i, device))
					{
						return;
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
			Release(enumeratorObject);
		}
	}

	private static string? FriendlyName(IMMDevice device)
	{
		IPropertyStore? store = null;
		PropVariant value = default;
		try
		{
			if (device.OpenPropertyStore(0, out store) != 0)
			{
				return null;
			}
			// PKEY_Device_FriendlyName
			PropertyKey key = new() { FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 14 };
			if (store.GetValue(ref key, out value) != 0 || value.Type != VtLpwstr)
			{
				return null;
			}
			return Marshal.PtrToStringUni(value.Pointer);
		}
		finally
		{
			PropVariantClear(ref value);
			Release(store);
		}
	}

	private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

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
	private struct PropertyKey
	{
		public Guid FormatId;
		public int PropertyId;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PropVariant
	{
		public ushort Type;
		public ushort Reserved1, Reserved2, Reserved3;
		public IntPtr Pointer;
		public IntPtr Pointer2;
	}

	[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDeviceEnumerator
	{
		[PreserveSig]
		int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);

		[PreserveSig]
		int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
	}

	[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDeviceCollection
	{
		[PreserveSig]
		int GetCount(out uint count);

		[PreserveSig]
		int Item(uint index, out IMMDevice device);
	}

	[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDevice
	{
		[PreserveSig]
		int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

		[PreserveSig]
		int OpenPropertyStore(int access, out IPropertyStore store);

		[PreserveSig]
		int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
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

	[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioEndpointVolume
	{
		[PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
		[PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
		[PreserveSig] int GetChannelCount(out uint count);
		[PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
		[PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
		[PreserveSig] int GetMasterVolumeLevel(out float level);
		[PreserveSig] int GetMasterVolumeLevelScalar(out float level);
		[PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
		[PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
		[PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
		[PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
		[PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
		[PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
	}

	[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioClient
	{
		[PreserveSig]
		int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);

		[PreserveSig]
		int GetBufferSize(out uint frames);

		[PreserveSig]
		int GetStreamLatency(out long latency);

		[PreserveSig]
		int GetCurrentPadding(out uint frames);

		[PreserveSig]
		int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);

		[PreserveSig]
		int GetMixFormat(out IntPtr format);

		[PreserveSig]
		int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

		[PreserveSig]
		int Start();

		[PreserveSig]
		int Stop();

		[PreserveSig]
		int Reset();

		[PreserveSig]
		int SetEventHandle(IntPtr handle);

		[PreserveSig]
		int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
	}

	[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioCaptureClient
	{
		[PreserveSig]
		int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);

		[PreserveSig]
		int ReleaseBuffer(uint frames);

		[PreserveSig]
		int GetNextPacketSize(out uint frames);
	}

	[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioRenderClient
	{
		[PreserveSig]
		int GetBuffer(uint frames, out IntPtr data);

		[PreserveSig]
		int ReleaseBuffer(uint frames, uint flags);
	}
}
