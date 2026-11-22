// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Kopru.Core.Audio;

// Captures sound with WASAPI (documented Core Audio APIs only):
//  - process loopback (Windows 10 2004 / build 19041 and later): ActivateAudioInterfaceAsync on
//    VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK with AUDIOCLIENT_ACTIVATION_PARAMS (PROCESS_LOOPBACK,
//    PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE) gives an IAudioClient that hears only the game's process tree, so
//    neither our own virtual pad's audio nor other programs are felt. It has no mix format: a 48 kHz 16-bit stereo PCM
//    format is asked for with AUTOCONVERTPCM. The completion handler is agile (IAgileObject) and the activation runs on an
//    MTA thread, as the documentation asks; the result is waited for with a timeout.
//  - the fallback, system loopback: the default render endpoint in loopback mode (everything it plays), refused when that
//    endpoint is the controller's own (a virtual pad or a DualSense endpoint), since its sound would feed back.
// Each capture runs one background thread that waits for the buffer event (or 10 ms) and drains the packets into a
// preallocated float buffer for the sink.
[SupportedOSPlatform("windows")]
public sealed class WasapiCaptureFactory : IAudioCaptureFactory
{
	// Windows 10 2004: the first build with process loopback capture.
	public const int ProcessLoopbackBuild = 19041;

	public static bool ProcessLoopbackSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, ProcessLoopbackBuild);

	// Tests can turn process loopback off to try the fallback.
	public bool UseProcessLoopback { get; init; } = true;

	// The audio objects are made on an MTA thread (the capture thread uses them directly); a caller on an STA thread (the
	// UI) waits for a pool thread to do it.
	public IAudioCapture? Start(int processId, IAudioCaptureSink sink, Action<string> log) =>
		Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
			? Task.Run(() => StartMta(processId, sink, log)).GetAwaiter().GetResult()
			: StartMta(processId, sink, log);

	private IAudioCapture? StartMta(int processId, IAudioCaptureSink sink, Action<string> log)
	{
		if (UseProcessLoopback && ProcessLoopbackSupported)
		{
			try
			{
				return WasapiCapture.Open(AudioCaptureMode.ProcessLoopback, processId, sink);
			}
			catch (Exception e)
			{
				log($"audio haptics: process loopback failed ({Describe(e)}), falling back to system loopback (other programs' sound is felt too)");
			}
		}
		else
		{
			log("audio haptics: process loopback not available on this Windows (needs Windows 10 2004), using system loopback (other programs' sound is felt too)");
		}
		try
		{
			string? why = WasapiCapture.DefaultRenderIsController();
			if (why != null)
			{
				log($"audio haptics: system loopback not used, the default output is the controller's own ({why})");
				return null;
			}
			return WasapiCapture.Open(AudioCaptureMode.SystemLoopback, 0, sink);
		}
		catch (Exception e)
		{
			log($"audio haptics: system loopback failed ({Describe(e)})");
			return null;
		}
	}

	private static string Describe(Exception e) => e is COMException c ? $"0x{c.HResult:X8}" : $"{e.GetType().Name}: {e.Message}";
}

[SupportedOSPlatform("windows")]
internal sealed class WasapiCapture : IAudioCapture, IWiredMicCapture
{
	private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
	private static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
	private static readonly Guid CaptureClientIid = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
	private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

	private const int ShareModeShared = 0;
	private const uint StreamFlagsLoopback = 0x00020000;
	private const uint StreamFlagsEventCallback = 0x00040000;
	private const uint StreamFlagsAutoConvertPcm = 0x80000000;
	private const uint StreamFlagsSrcDefaultQuality = 0x08000000;
	private const int BufferFlagsSilent = 0x2;
	private const long BufferDuration = 200_000; // 20 ms in 100 ns units
	private const int ClsctxAll = 23;
	private const ushort VtBlob = 65;
	private const ushort FormatPcm = 1;
	private const ushort FormatFloat = 3;
	private const ushort FormatExtensible = 0xFFFE;
	private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");

	public static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

	private readonly IAudioClient client;
	private readonly IAudioCaptureClient captureClient;
	private readonly AutoResetEvent ready = new(false);
	private readonly IAudioCaptureSink sink;
	private readonly Thread thread;
	private readonly int channels, rate, bits;
	private readonly bool isFloat;
	private float[] buffer;
	private volatile bool stopping;
	private volatile bool gaveUp;

	public AudioCaptureMode Mode { get; }

	// The capture thread gave up after repeated failures (endpoint captures only; IWiredMicCapture).
	public bool Stopped => gaveUp;

	private WasapiCapture(AudioCaptureMode mode, IAudioClient client, IntPtr format, IAudioCaptureSink sink)
	{
		Mode = mode;
		this.client = client;
		this.sink = sink;
		WaveFormat f = Marshal.PtrToStructure<WaveFormat>(format);
		channels = f.Channels;
		rate = (int)f.SamplesPerSec;
		bits = f.BitsPerSample;
		isFloat = f.Tag == FormatFloat || (f.Tag == FormatExtensible && f.Size >= 22 && Marshal.PtrToStructure<Guid>(format + 24) == SubtypeFloat);
		if (channels < 1 || rate < 8000 || !(isFloat ? bits == 32 : bits is 16 or 24 or 32))
		{
			throw new NotSupportedException($"capture format {f.Tag:X4} {channels} ch {rate} Hz {bits} bit");
		}
		uint flags = StreamFlagsEventCallback;
		if (mode != AudioCaptureMode.Endpoint)
		{
			flags |= StreamFlagsLoopback;
		}
		if (mode == AudioCaptureMode.ProcessLoopback)
		{
			flags |= StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality;
		}
		Marshal.ThrowExceptionForHR(client.Initialize(ShareModeShared, flags, BufferDuration, 0, format, IntPtr.Zero));
		Marshal.ThrowExceptionForHR(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
		Guid iid = CaptureClientIid;
		Marshal.ThrowExceptionForHR(client.GetService(ref iid, out object service));
		captureClient = (IAudioCaptureClient)service;
		// Process loopback reports no buffer size until started on some builds: a second of audio is plenty either way.
		buffer = new float[Math.Max(rate, client.GetBufferSize(out uint size) == 0 ? (int)size : 0) * channels];
		Marshal.ThrowExceptionForHR(client.Start());
		thread = new Thread(Run)
		{
			Name = mode == AudioCaptureMode.Endpoint ? "Controller microphone (USB)" : "Audio haptics capture",
			IsBackground = true,
			Priority = ThreadPriority.AboveNormal,
		};
		thread.SetApartmentState(ApartmentState.MTA);
		thread.Start();
	}

	public static WasapiCapture Open(AudioCaptureMode mode, int processId, IAudioCaptureSink sink)
	{
		IAudioClient? client = null;
		IntPtr format = IntPtr.Zero;
		bool ownFormat = false;
		try
		{
			if (mode == AudioCaptureMode.ProcessLoopback)
			{
				client = ActivateProcessLoopback(processId);
				format = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormat>());
				ownFormat = true;
				Marshal.StructureToPtr(new WaveFormat
				{
					Tag = FormatPcm,
					Channels = 2,
					SamplesPerSec = 48000,
					AvgBytesPerSec = 48000 * 4,
					BlockAlign = 4,
					BitsPerSample = 16,
					Size = 0
				}, format, false);
			}
			else
			{
				client = ActivateDefaultRender();
				Marshal.ThrowExceptionForHR(client.GetMixFormat(out format));
			}
			return new WasapiCapture(mode, client, format, sink);
		}
		catch
		{
			Release(client);
			throw;
		}
		finally
		{
			if (format != IntPtr.Zero)
			{
				if (ownFormat)
				{
					Marshal.FreeHGlobal(format);
				}
				else
				{
					Marshal.FreeCoTaskMem(format);
				}
			}
		}
	}

	// One capture endpoint by id on its mix format, without loopback (the controller's USB microphone while wired).
	public static WasapiCapture OpenEndpoint(string endpointId, IAudioCaptureSink sink)
	{
		IAudioClient? client = null;
		IntPtr format = IntPtr.Zero;
		try
		{
			client = ActivateEndpoint(endpointId);
			Marshal.ThrowExceptionForHR(client.GetMixFormat(out format));
			return new WasapiCapture(AudioCaptureMode.Endpoint, client, format, sink);
		}
		catch
		{
			Release(client);
			throw;
		}
		finally
		{
			if (format != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(format);
			}
		}
	}

	// Null when the default render endpoint may be captured; otherwise why not.
	public static string? DefaultRenderIsController()
	{
		WindowsAudioDefaults defaults = new();
		string? id = defaults.GetDefault(AudioFlow.Render, AudioRole.Console);
		if (id == null)
		{
			throw new InvalidOperationException("no default output");
		}
		AudioEndpointInfo? info = defaults.Describe(id);
		if (info == null)
		{
			return null;
		}
		if (info.Pad.IsVirtualPad)
		{
			return "virtual pad";
		}
		return info.Name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) || info.Name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase)
			? "controller endpoint" : null;
	}

	public void Dispose()
	{
		if (stopping)
		{
			return;
		}
		stopping = true;
		ready.Set();
		if (Thread.CurrentThread != thread)
		{
			thread.Join(TimeSpan.FromSeconds(2));
		}
		try
		{
			client.Stop();
		}
		catch (Exception)
		{
			// Already gone (device removed).
		}
		Release(captureClient);
		Release(client);
		ready.Dispose();
	}

	private void Run()
	{
		int failures = 0;
		while (!stopping)
		{
			ready.WaitOne(10);
			try
			{
				Drain();
				failures = 0;
			}
			catch (Exception e)
			{
				// The device went away (or the game's audio ended): wait for the runner to stop the capture.
				// Endpoint only: a microphone that keeps failing is given up on after 20 in a row.
				if (Mode == AudioCaptureMode.Endpoint && ++failures >= 20)
				{
					Diagnostics.AppLog.Write($"wired: USB microphone stopped ({(e is System.Runtime.InteropServices.COMException c ? $"0x{c.HResult:X8}" : e.GetType().Name)})");
					gaveUp = true;
					return;
				}
				Thread.Sleep(50);
			}
		}
	}

	private void Drain()
	{
		while (!stopping && captureClient.GetNextPacketSize(out uint packet) == 0 && packet > 0)
		{
			if (captureClient.GetBuffer(out IntPtr data, out uint frames, out int flags, out _, out _) != 0)
			{
				return;
			}
			try
			{
				int n = (int)frames * channels;
				if (n > buffer.Length)
				{
					// Bigger than a second: a format change; rare enough to grow once.
					buffer = new float[n];
				}
				Span<float> samples = buffer.AsSpan(0, n);
				if ((flags & BufferFlagsSilent) != 0 || data == IntPtr.Zero)
				{
					samples.Clear();
				}
				else
				{
					Convert(data, samples);
				}
				sink.OnAudio(samples, channels, rate);
			}
			finally
			{
				captureClient.ReleaseBuffer(frames);
			}
		}
	}

	private unsafe void Convert(IntPtr data, Span<float> samples)
	{
		if (isFloat)
		{
			new ReadOnlySpan<float>((void*)data, samples.Length).CopyTo(samples);
			return;
		}
		switch (bits)
		{
			case 16:
			{
				ReadOnlySpan<short> s = new((void*)data, samples.Length);
				for (int i = 0; i < s.Length; i++)
				{
					samples[i] = s[i] / 32768f;
				}
				break;
			}
			case 24:
			{
				byte* p = (byte*)data;
				for (int i = 0; i < samples.Length; i++, p += 3)
				{
					samples[i] = ((p[0] << 8) | (p[1] << 16) | (p[2] << 24)) / 2147483648f;
				}
				break;
			}
			default:
			{
				ReadOnlySpan<int> s = new((void*)data, samples.Length);
				for (int i = 0; i < s.Length; i++)
				{
					samples[i] = s[i] / 2147483648f;
				}
				break;
			}
		}
	}

	private static IAudioClient ActivateDefaultRender()
	{
		var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClsid, true)!)!;
		IMMDevice? device = null;
		try
		{
			Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint((int)AudioFlow.Render, (int)AudioRole.Console, out device));
			Guid iid = AudioClientIid;
			Marshal.ThrowExceptionForHR(device!.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object o));
			return (IAudioClient)o;
		}
		finally
		{
			Release(device);
			Release(enumerator);
		}
	}

	private static IAudioClient ActivateEndpoint(string endpointId)
	{
		IMMDeviceEnumerator? enumerator = null;
		IMMDevice? device = null;
		try
		{
			enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClsid, true)!)!;
			Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpointId, out device));
			Guid iid = AudioClientIid;
			Marshal.ThrowExceptionForHR(device!.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object o));
			return (IAudioClient)o;
		}
		finally
		{
			Release(device);
			Release(enumerator);
		}
	}

	private static IAudioClient ActivateProcessLoopback(int processId)
	{
		// AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = PROCESS_LOOPBACK (1), { TargetProcessId, ProcessLoopbackMode =
		// INCLUDE_TARGET_PROCESS_TREE (0) } }, passed as a VT_BLOB PROPVARIANT.
		IntPtr blob = Marshal.AllocHGlobal(12);
		IntPtr variant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobVariant>());
		Completion handler = new();
		IActivateAudioInterfaceAsyncOperation? operation = null;
		try
		{
			Marshal.WriteInt32(blob, 0, 1);
			Marshal.WriteInt32(blob, 4, processId);
			Marshal.WriteInt32(blob, 8, 0);
			Marshal.StructureToPtr(new BlobVariant { Type = VtBlob, Size = 12, Data = blob }, variant, false);
			Guid iid = AudioClientIid;
			Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(ProcessLoopbackDevice, ref iid, variant, handler, out operation));
			if (!handler.Done.Wait(ActivationTimeout))
			{
				throw new TimeoutException("process loopback activation did not complete");
			}
			Marshal.ThrowExceptionForHR(handler.Result);
			return handler.Client ?? throw new InvalidOperationException("no audio client");
		}
		finally
		{
			Release(operation);
			// Only after the completion ran (or the wait gave up: then Windows may still read it, so it leaks).
			if (handler.Done.IsSet)
			{
				Marshal.FreeHGlobal(variant);
				Marshal.FreeHGlobal(blob);
			}
		}
	}

	private static void Release(object? o)
	{
		if (o != null && Marshal.IsComObject(o))
		{
			Marshal.ReleaseComObject(o);
		}
	}

	[DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
	private static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath, ref Guid riid, IntPtr activationParams,
		IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation activationOperation);

	// Called by Windows on an MTA worker thread; agile so it is never marshalled.
	[ClassInterface(ClassInterfaceType.None)]
	private sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
	{
		public ManualResetEventSlim Done { get; } = new(false);

		public int Result { get; private set; }

		public IAudioClient? Client { get; private set; }

		public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
		{
			try
			{
				int hr = operation.GetActivateResult(out int result, out object? o);
				Result = hr != 0 ? hr : result;
				if (Result == 0 && o is IAudioClient c)
				{
					Client = c;
				}
				else
				{
					Release(o);
					if (Result == 0)
					{
						Result = unchecked((int)0x80004002); // E_NOINTERFACE
					}
				}
			}
			catch (Exception e)
			{
				Result = e.HResult != 0 ? e.HResult : unchecked((int)0x80004005);
			}
			finally
			{
				Done.Set();
			}
			return 0;
		}
	}

	[StructLayout(LayoutKind.Sequential, Pack = 2)]
	private struct WaveFormat
	{
		public ushort Tag;
		public ushort Channels;
		public uint SamplesPerSec;
		public uint AvgBytesPerSec;
		public ushort BlockAlign;
		public ushort BitsPerSample;
		public ushort Size;
	}

	// PROPVARIANT holding a BLOB: vt, three reserved words, then { ULONG cbSize; BYTE* pBlobData }.
	[StructLayout(LayoutKind.Explicit, Size = 24)]
	private struct BlobVariant
	{
		[FieldOffset(0)]
		public ushort Type;

		[FieldOffset(8)]
		public uint Size;

		[FieldOffset(16)]
		public IntPtr Data;
	}

	[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IActivateAudioInterfaceCompletionHandler
	{
		[PreserveSig]
		int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
	}

	[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IActivateAudioInterfaceAsyncOperation
	{
		[PreserveSig]
		int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
	}

	[ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAgileObject
	{
	}

	[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IAudioClient
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
		int GetBuffer(out IntPtr data, out uint frames, out int flags, out long devicePosition, out long qpcPosition);

		[PreserveSig]
		int ReleaseBuffer(uint frames);

		[PreserveSig]
		int GetNextPacketSize(out uint frames);
	}

	[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDeviceEnumerator
	{
		[PreserveSig]
		int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

		[PreserveSig]
		int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);

		[PreserveSig]
		int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
	}

	[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDevice
	{
		[PreserveSig]
		int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
	}
}

// Whether Windows' default output is the virtual controller's own endpoint (then any program's sound reaches it). Throws on
// an audio API failure; false off Windows or with no default output.
public static class DefaultOutputCheck
{
	public static bool IsVirtualPad()
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}
		try
		{
			return WasapiCapture.DefaultRenderIsController() == "virtual pad";
		}
		catch (InvalidOperationException)
		{
			// No default output.
			return false;
		}
	}
}
