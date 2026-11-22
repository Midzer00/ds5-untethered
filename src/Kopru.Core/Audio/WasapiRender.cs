// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Kopru.Core.Audio;

// Plays the wired mode audio on the controller's own USB audio endpoint with documented Core Audio only
// (IMMDeviceEnumerator.GetDevice, IAudioClient, IAudioRenderClient): the endpoint's mix format must be 48 kHz with at
// least 4 channels, 32-bit float or 16-bit PCM. Shared mode, a 100 ms buffer; a haptics block is queued every
// 10.67 ms while the stream runs. Every call that can fail surfaces an exception (COMException via
// ThrowExceptionForHR), so the session drops the sink and keeps haptics on Bluetooth.
[SupportedOSPlatform("windows")]
internal sealed class WasapiRenderSink : IWiredAudioSink
{
	private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
	private static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
	private static readonly Guid RenderClientIid = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

	private const int ShareModeShared = 0;
	private const int ClsctxAll = 23;
	private const long BufferDuration = 1_000_000; // 100 ms in 100 ns units
	private const int CushionFrames = 960;         // 20 ms of silence queued before the first block
	private const int MaxQueuedFrames = 2880;      // 60 ms: more queued means the endpoint is not keeping up
	private const ushort FormatPcm = 1;
	private const ushort FormatFloat = 3;
	private const ushort FormatExtensible = 0xFFFE;
	private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");
	private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");

	private readonly WasapiCapture.IAudioClient client;
	private readonly IAudioRenderClient render;
	private readonly WiredAudioFrames frames = new();
	private readonly WiredAudioPeaks peaks = new();
	private readonly object gate = new();
	private readonly int channels;
	private readonly bool isFloat;
	private float[] mix = [];
	private bool started;
	private bool disposed;
	private long droppedBlocks;

	private WasapiRenderSink(WasapiCapture.IAudioClient client, IAudioRenderClient render, int channels, bool isFloat, uint? channelMask)
	{
		this.client = client;
		this.render = render;
		this.channels = channels;
		this.isFloat = isFloat;
		ChannelMask = channelMask;
	}

	public int Channels => channels;

	public string Format => isFloat ? "float" : "pcm16";

	// The endpoint's channel mask when its mix format is WAVE_FORMAT_EXTENSIBLE (diagnostics; null for a plain format).
	public uint? ChannelMask { get; }

	// Blocks dropped because too much audio was queued (the endpoint fell behind). Diagnostics.
	public long DroppedBlocks => Interlocked.Read(ref droppedBlocks);

	public bool HasSpeakerAudio => frames.HasSpeakerAudio;

	public static WasapiRenderSink Open(string endpointId)
	{
		var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClsid, true)!)!;
		IMMDevice? device = null;
		WasapiCapture.IAudioClient? client = null;
		IntPtr mixFormat = IntPtr.Zero;
		try
		{
			Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpointId, out device));
			Guid iid = AudioClientIid;
			Marshal.ThrowExceptionForHR(device!.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object activated));
			client = (WasapiCapture.IAudioClient)activated;
			Marshal.ThrowExceptionForHR(client.GetMixFormat(out mixFormat));
			WaveFormat format = Marshal.PtrToStructure<WaveFormat>(mixFormat);
			bool isFloat = IsFloat(format, mixFormat);
			bool isPcm16 = format.Tag == FormatPcm && format.BitsPerSample == 16 ||
				format.Tag == FormatExtensible && format.Size >= 22 && SubType(mixFormat) == SubtypePcm && format.BitsPerSample == 16;
			if (format.SamplesPerSec != WiredAudioFrames.Rate || format.Channels < 4 || !(isFloat || isPcm16))
			{
				throw new NotSupportedException($"mix format {format.Tag:X4} {format.Channels} ch {format.SamplesPerSec} Hz {format.BitsPerSample} bit");
			}
			Marshal.ThrowExceptionForHR(client.Initialize(ShareModeShared, 0, BufferDuration, 0, mixFormat, IntPtr.Zero));
			Guid renderIid = RenderClientIid;
			Marshal.ThrowExceptionForHR(client.GetService(ref renderIid, out object service));
			uint? mask = format.Tag == FormatExtensible && format.Size >= 22 ? (uint)Marshal.ReadInt32(mixFormat + 20) : null;
			return new WasapiRenderSink(client, (IAudioRenderClient)service, format.Channels, isFloat, mask);
		}
		catch
		{
			Release(client);
			throw;
		}
		finally
		{
			Release(device);
			Release(enumerator);
			if (mixFormat != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(mixFormat);
			}
		}
	}

	// The haptics thread, every 10.67 ms: one block in, one block out. Throws when the endpoint is gone.
	public void WriteHaptics(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right)
	{
		lock (gate)
		{
			if (disposed)
			{
				return;
			}
			if (mix.Length < (left.Length * WiredAudioFrames.Upsample + WiredAudioSteering.MaxFramesPerBlock) * channels)
			{
				mix = new float[(left.Length * WiredAudioFrames.Upsample + WiredAudioSteering.MaxFramesPerBlock) * channels];
			}
			int count = frames.Build(left, right, mix, channels);
			Marshal.ThrowExceptionForHR(client.GetCurrentPadding(out uint padding));
			if (!started || padding == 0)
			{
				// Not playing yet (first block, or a just restarted stream): a short silence cushion first.
				Queue(CushionFrames, null);
			}
			if (padding > MaxQueuedFrames)
			{
				droppedBlocks++;
				return;
			}
			// The block is nudged a few frames so the queue drifts back to the target; the silence cushion above and
			// the whole-block drop for MaxQueuedFrames stay as the last resorts.
			count = WiredAudioSteering.Apply(mix, count, channels, WiredAudioSteering.Adjust((int)padding));
			Queue(count, mix);
			// Only what was really queued counts (the cushion above is silence; a dropped block never arrived).
			peaks.Add(mix, count, channels);
			if (peaks.TryTake(false, out float hapticsPeak, out float speakerPeak, out int window))
			{
				LogPeaks(window, hapticsPeak, speakerPeak);
			}
			if (!started)
			{
				Marshal.ThrowExceptionForHR(client.Start());
				started = true;
			}
		}
	}

	public void PushSpeaker(ReadOnlySpan<byte> audio) => frames.PushSpeaker(audio);

	// The stream stopped: stop and reset the client so the endpoint may sleep until the next block. Never throws.
	public void Idle()
	{
		lock (gate)
		{
			try
			{
				client.Stop();
				client.Reset();
				started = false;
				// Nothing left over plays when the stream starts again.
				frames.Reset();
				if (peaks.TryTake(true, out float hapticsPeak, out float speakerPeak, out int window))
				{
					LogPeaks(window, hapticsPeak, speakerPeak);
				}
			}
			catch (Exception)
			{
				// Already gone (device removed); the next write makes it fail anyway.
			}
		}
	}

	// What the last window of queued audio held, logged when WiredAudioPeaks lets one go (a full window while
	// running, whatever is buffered on Idle). Written from the thread pool: the log's file write never delays the
	// haptics thread.
	private void LogPeaks(int framesWritten, float haptics, float speaker)
	{
		string line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
			$"wired: usb audio {framesWritten / (double)WiredAudioFrames.Rate:0.0} s written:" +
			$" peak ch3-4 (haptics) {haptics:0.000}, peak ch1-2 (speaker) {speaker:0.000}," +
			$" {DroppedBlocks} blocks dropped");
		_ = Task.Run(() => Diagnostics.AppLog.Write(line));
	}

	public void Dispose()
	{
		lock (gate)
		{
			if (disposed)
			{
				return;
			}
			disposed = true;
			try
			{
				client.Stop();
			}
			catch (Exception)
			{
				// Already gone.
			}
			Release(render);
			Release(client);
		}
	}

	// count frames of `data` (null = silence) into the endpoint buffer.
	private void Queue(int count, float[]? data)
	{
		Marshal.ThrowExceptionForHR(render.GetBuffer((uint)count, out IntPtr buffer));
		try
		{
			if (data == null)
			{
				Clear(buffer, count);
			}
			else
			{
				Copy(buffer, data, count);
			}
		}
		finally
		{
			Marshal.ThrowExceptionForHR(render.ReleaseBuffer((uint)count, 0));
		}
	}

	private unsafe void Clear(IntPtr buffer, int count) =>
		new Span<byte>((void*)buffer, count * channels * (isFloat ? 4 : 2)).Clear();

	private unsafe void Copy(IntPtr buffer, float[] data, int count)
	{
		int samples = count * channels;
		if (isFloat)
		{
			data.AsSpan(0, samples).CopyTo(new Span<float>((void*)buffer, samples));
			return;
		}
		Span<short> pcm = new((void*)buffer, samples);
		for (int i = 0; i < samples; i++)
		{
			pcm[i] = (short)Math.Clamp(data[i] * 32767f, -32768f, 32767f);
		}
	}

	private static bool IsFloat(WaveFormat format, IntPtr mixFormat) =>
		format.Tag == FormatFloat && format.BitsPerSample == 32 ||
		format.Tag == FormatExtensible && format.Size >= 22 && SubType(mixFormat) == SubtypeFloat && format.BitsPerSample == 32;

	private static Guid SubType(IntPtr mixFormat) => Marshal.PtrToStructure<Guid>(mixFormat + 24);

	private static void Release(object? o)
	{
		if (o != null && Marshal.IsComObject(o))
		{
			Marshal.ReleaseComObject(o);
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

	// IMMDeviceEnumerator: only GetDevice is called; the slots before it keep the method order.
	[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDeviceEnumerator
	{
		[PreserveSig]
		int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

		[PreserveSig]
		int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr device);

		[PreserveSig]
		int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
	}

	// IMMDevice: only Activate is called (slot 0).
	[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMMDevice
	{
		[PreserveSig]
		int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
	}

	// IAudioRenderClient (F294ACFC-3146-4483-A7BF-ADDCA7C260E2).
	[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioRenderClient
	{
		[PreserveSig]
		int GetBuffer(uint frames, out IntPtr data);

		[PreserveSig]
		int ReleaseBuffer(uint frames, uint flags);
	}
}
