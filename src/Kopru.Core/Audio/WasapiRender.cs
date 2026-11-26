// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Kopru.Core.Audio;

// The ducking preference of one audio session (IAudioSessionControl2::SetDuckingPreference); a seam for tests.
internal interface IDuckingPreference
{
	// optOut true: the system's default communications ducking leaves this session alone. Returns the HRESULT.
	int SetDuckingPreference(bool optOut);
}

// Windows' default communications ducking ("reduce other sounds by 80 %" while a communication stream is open,
// Mmsys.cpl > Communications) lowers every other render session, including ours on the controller's USB endpoint:
// haptics on channels 3-4 at 20 % are not felt (found on hardware 2026-11-24; the speaker stayed audible). A stream
// opened on the default communications device counts as one (documented), e.g. a voice app using the controller's
// mic. A shared-mode render session opts out right after it is initialized, before its first Start; on hardware
// (2026-11-24) that opt-out alone did not bring the haptics back, so exclusive mode is the default (WiredAudioMode).
internal static class WiredAudioDucking
{
	public const string OptedOut = "opted out of Windows communications ducking";

	// What happened, for the open log line; never throws (a failure must not stop the stream).
	public static string OptOut(Func<IDuckingPreference?> session)
	{
		try
		{
			if (session() is not { } control)
			{
				return "not opted out of Windows communications ducking (no session control)";
			}
			int hr = control.SetDuckingPreference(true);
			return hr == 0 ? OptedOut
				: string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"not opted out of Windows communications ducking (0x{hr:X8})");
		}
		catch (Exception e)
		{
			return "not opted out of Windows communications ducking (" + WiredAudio.Describe(e) + ")";
		}
	}
}

// Plays the wired mode audio on the controller's own USB audio endpoint with documented Core Audio only
// (IMMDeviceEnumerator.GetDevice, IAudioClient, IAudioRenderClient, IAudioClock). Shared mode by default; exclusive
// (KOPRU_WIRED_AUDIO_MODE=exclusive, not proven to play on hardware):
// a 48 kHz WAVEFORMATEXTENSIBLE format with the endpoint's channels (at least 4) the device accepts
// (IsFormatSupported, 16-bit PCM first), timer-driven with a 100 ms buffer, so the haptics thread keeps pushing a
// block every 10.67 ms and steering on the queue exactly as in shared mode. Shared mode (the default, and the
// fallback) uses the endpoint's mix format (48 kHz, at least 4 channels, float or 16-bit PCM) and opts its session
// out of communications ducking. The feeding itself is WiredRenderPump (tested with a fake stream). Every call that can fail surfaces an exception
// (COMException via ThrowExceptionForHR), so the session drops the sink and keeps haptics on Bluetooth.
// Side effect of exclusive mode: no other program can play on the real controller's endpoint while wired (games use
// the virtual pad's endpoint).
[SupportedOSPlatform("windows")]
internal sealed class WasapiRenderSink : IWiredAudioSink
{
	private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
	private static readonly Guid AudioClientIid = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
	private static readonly Guid RenderClientIid = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
	private static readonly Guid SessionControlIid = new("F4B1A599-7266-4319-A8CA-E70ACB11E8CD");
	private static readonly Guid ClockIid = new("CD63314F-3FBA-4A1B-812C-EF96358728E7");

	private const int ShareModeShared = 0;
	private const int ShareModeExclusive = 1;
	private const int ClsctxAll = 23;
	private const int AudclntEBufferSizeNotAligned = unchecked((int)0x88890019);
	private const long BufferDuration = 1_000_000; // 100 ms in 100 ns units
	private const ushort FormatPcm = 1;
	private const ushort FormatFloat = 3;
	private const ushort FormatExtensible = 0xFFFE;
	private const uint QuadMask = 0x33;            // front left/right, back left/right
	private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");
	private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");

	private readonly IRenderAudioClient client;
	private readonly IAudioRenderClient render;
	private readonly IAudioClock? clock;
	private readonly WiredRenderPump pump;
	private readonly object gate = new();
	private readonly int channels;
	private bool disposed;

	private WasapiRenderSink(IRenderAudioClient client, IAudioRenderClient render, int channels, WiredSampleFormat sample, string format,
		uint? channelMask, string mode)
	{
		this.client = client;
		this.render = render;
		this.channels = channels;
		Format = format;
		ChannelMask = channelMask;
		Mode = mode;
		clock = OpenClock(client);
		pump = new WiredRenderPump(new ComStream(this), channels, sample, () => Mode, tuning: WiredAudioTuning.Current);
	}

	public int Channels => channels;

	public string Format { get; }

	// The channel mask of the format in use (diagnostics; null for a plain shared mix format).
	public uint? ChannelMask { get; }

	// "exclusive mode" / "shared mode, ..." with the ducking opt-out's outcome in shared mode, for the open log line.
	public string Mode { get; private set; }

	// Blocks dropped because too much audio was queued (the endpoint fell behind). Diagnostics.
	public long DroppedBlocks => pump.DroppedBlocks;

	public bool HasSpeakerAudio => pump.Frames.HasSpeakerAudio;

	public bool SpeakerSounding => pump.Frames.SpeakerSounding;

	// Opens in the wanted mode; exclusive falls back to shared (Mode says which is in use and why).
	public static WasapiRenderSink Open(string endpointId, WiredAudioMode wanted, string? note)
	{
		WasapiRenderSink sink = WiredAudioModeChoice.Open(wanted, note, () => OpenExclusive(endpointId), () => OpenShared(endpointId), out string how);
		sink.Mode = sink.Mode.Length == 0 ? how : how + "; " + sink.Mode;
		return sink;
	}

	private static WasapiRenderSink OpenShared(string endpointId)
	{
		IRenderAudioClient? client = null;
		IntPtr mixFormat = IntPtr.Zero;
		try
		{
			client = Activate(endpointId);
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
			string ducking = OptOutOfDucking(client);
			return new WasapiRenderSink(client, (IAudioRenderClient)service, format.Channels,
				isFloat ? WiredSampleFormat.Float32 : WiredSampleFormat.Pcm16, isFloat ? "float" : "pcm16", mask, ducking);
		}
		catch
		{
			Release(client);
			throw;
		}
		finally
		{
			if (mixFormat != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(mixFormat);
			}
		}
	}

	// Exclusive mode, timer-driven: the first candidate format the device supports, a 100 ms buffer and the device's
	// default period. An unaligned buffer (documented for event-driven streams only) is retried once at the aligned
	// size on a fresh client.
	private static WasapiRenderSink OpenExclusive(string endpointId)
	{
		IRenderAudioClient? client = null;
		IntPtr mixFormat = IntPtr.Zero;
		IntPtr chosen = IntPtr.Zero;
		try
		{
			client = Activate(endpointId);
			Marshal.ThrowExceptionForHR(client.GetMixFormat(out mixFormat));
			WaveFormat mixInfo = Marshal.PtrToStructure<WaveFormat>(mixFormat);
			int channelCount = mixInfo.Channels;
			if (channelCount < 4)
			{
				throw new NotSupportedException($"{channelCount} channels");
			}
			uint mask = mixInfo.Tag == FormatExtensible && mixInfo.Size >= 22 ? (uint)Marshal.ReadInt32(mixFormat + 20) : QuadMask;
			WiredExclusiveFormat? pick = null;
			chosen = Marshal.AllocCoTaskMem(WiredExclusiveFormat.Size);
			foreach (WiredExclusiveFormat candidate in WiredExclusiveFormat.Candidates(channelCount, mask))
			{
				Marshal.Copy(candidate.ToBytes(), 0, chosen, WiredExclusiveFormat.Size);
				if (client.IsFormatSupported(ShareModeExclusive, chosen, IntPtr.Zero) == 0)
				{
					pick = candidate;
					break;
				}
			}
			if (pick is not { } format)
			{
				throw new NotSupportedException("no exclusive 48 kHz format");
			}
			Marshal.ThrowExceptionForHR(client.GetDevicePeriod(out long defaultPeriod, out _));
			long period = Math.Max(defaultPeriod, 1);
			long buffer = Math.Max(BufferDuration, period);
			int hr = client.Initialize(ShareModeExclusive, 0, buffer, period, chosen, IntPtr.Zero);
			if (hr == AudclntEBufferSizeNotAligned)
			{
				Marshal.ThrowExceptionForHR(client.GetBufferSize(out uint alignedFrames));
				buffer = WiredExclusiveFormat.Duration(alignedFrames);
				period = Math.Min(period, buffer);
				Release(client);
				client = null;
				client = Activate(endpointId);
				hr = client.Initialize(ShareModeExclusive, 0, buffer, period, chosen, IntPtr.Zero);
			}
			Marshal.ThrowExceptionForHR(hr);
			Guid renderIid = RenderClientIid;
			Marshal.ThrowExceptionForHR(client.GetService(ref renderIid, out object service));
			return new WasapiRenderSink(client, (IAudioRenderClient)service, channelCount, format.Sample, format.Name, mask, string.Empty);
		}
		catch
		{
			Release(client);
			throw;
		}
		finally
		{
			if (mixFormat != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(mixFormat);
			}
			if (chosen != IntPtr.Zero)
			{
				Marshal.FreeCoTaskMem(chosen);
			}
		}
	}

	private static IRenderAudioClient Activate(string endpointId)
	{
		var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClsid, true)!)!;
		IMMDevice? device = null;
		try
		{
			Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpointId, out device));
			Guid iid = AudioClientIid;
			Marshal.ThrowExceptionForHR(device!.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object activated));
			return (IRenderAudioClient)activated;
		}
		finally
		{
			Release(device);
			Release(enumerator);
		}
	}

	// Opts this stream's session out of the default communications ducking (before the first Start, as documented);
	// the session control is only held for the call. Shared mode only (exclusive bypasses the engine).
	private static string OptOutOfDucking(IRenderAudioClient client)
	{
		object? service = null;
		try
		{
			return WiredAudioDucking.OptOut(() =>
			{
				Guid iid = SessionControlIid;
				Marshal.ThrowExceptionForHR(client.GetService(ref iid, out service));
				return service is IAudioSessionControl2 control ? new SessionDucking(control) : null;
			});
		}
		finally
		{
			Release(service);
		}
	}

	private sealed class SessionDucking(IAudioSessionControl2 control) : IDuckingPreference
	{
		public int SetDuckingPreference(bool optOut) => control.SetDuckingPreference(optOut);
	}

	// The haptics thread, every 10.67 ms: one block in, one block out (WiredRenderPump). Throws when the endpoint is
	// gone or the device clock did not move after a second of feeding (WiredAudioStalledException).
	public void WriteHaptics(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right)
	{
		lock (gate)
		{
			if (!disposed)
			{
				pump.WriteHaptics(left, right);
			}
		}
	}

	public void PushSpeaker(ReadOnlySpan<byte> audio) => pump.Frames.PushSpeaker(audio);

	// The stream stopped: stop and reset the client so the endpoint may sleep until the next block. Never throws.
	public void Idle()
	{
		lock (gate)
		{
			if (!disposed)
			{
				pump.Idle();
			}
		}
	}

	private static IAudioClock? OpenClock(IRenderAudioClient client)
	{
		try
		{
			Guid iid = ClockIid;
			return client.GetService(ref iid, out object service) == 0 ? service as IAudioClock : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	// The COM calls behind the pump.
	private sealed class ComStream(WasapiRenderSink sink) : IRenderStream
	{
		public int GetCurrentPadding(out uint frames) => sink.client.GetCurrentPadding(out frames);

		public int GetBuffer(uint frames, out IntPtr data) => sink.render.GetBuffer(frames, out data);

		public int ReleaseBuffer(uint frames) => sink.render.ReleaseBuffer(frames, 0);

		public int Start() => sink.client.Start();

		public int Stop() => sink.client.Stop();

		public int Reset() => sink.client.Reset();

		public double? PlayedSeconds()
		{
			if (sink.clock is not { } c || c.GetFrequency(out ulong frequency) != 0 || frequency == 0 ||
				c.GetPosition(out ulong position, out _) != 0)
			{
				return null;
			}
			return position / (double)frequency;
		}
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
			Release(clock);
			Release(render);
			Release(client);
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

	// IAudioClient (1CB9AD4C-DBFA-4C32-B178-C2F568A703B2) as the render sink uses it: IsFormatSupported takes a raw
	// closest-match pointer so exclusive mode can pass NULL (it has no closest match).
	[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IRenderAudioClient
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
		int IsFormatSupported(int shareMode, IntPtr format, IntPtr closest);

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

	// IAudioSessionControl2 (BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D), reached by QueryInterface on the IAudioSessionControl
	// that IAudioClient::GetService returns. Only SetDuckingPreference is called; the slots before it (the nine
	// IAudioSessionControl methods, then four of its own) keep the method order.
	[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioSessionControl2
	{
		[PreserveSig] int GetState(out int state);
		[PreserveSig] int GetDisplayName(out IntPtr name);
		[PreserveSig] int SetDisplayName(IntPtr name, IntPtr eventContext);
		[PreserveSig] int GetIconPath(out IntPtr path);
		[PreserveSig] int SetIconPath(IntPtr path, IntPtr eventContext);
		[PreserveSig] int GetGroupingParam(out Guid groupingParam);
		[PreserveSig] int SetGroupingParam(IntPtr groupingParam, IntPtr eventContext);
		[PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
		[PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
		[PreserveSig] int GetSessionIdentifier(out IntPtr id);
		[PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
		[PreserveSig] int GetProcessId(out uint processId);
		[PreserveSig] int IsSystemSoundsSession();
		[PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
	}

	// IAudioClock (CD63314F-3FBA-4A1B-812C-EF96358728E7): the device's play position, for the stall check and the log.
	[ComImport, Guid("CD63314F-3FBA-4A1B-812C-EF96358728E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IAudioClock
	{
		[PreserveSig] int GetFrequency(out ulong frequency);
		[PreserveSig] int GetPosition(out ulong position, out ulong qpcPosition);
		[PreserveSig] int GetCharacteristics(out uint characteristics);
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
