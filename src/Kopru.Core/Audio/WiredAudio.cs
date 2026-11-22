// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Kopru.Core.Hid;

namespace Kopru.Core.Audio;

// What the session's haptics stream writes to instead of Bluetooth reports while wired (the controller's own USB
// audio endpoint).
public interface IWiredAudioSink : IDisposable
{
	// One block of the haptics stream (3 kHz signed 8-bit; left/right equal length) for channels 3-4, on the haptics
	// thread every 10.67 ms while the stream runs. Throws when the endpoint is gone.
	void WriteHaptics(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right);

	// The game's 4-channel 48 kHz 16-bit audio; its channels 1-2 are played on channels 1-2. Any thread; never throws.
	void PushSpeaker(ReadOnlySpan<byte> audio);

	// Speaker audio still buffered.
	bool HasSpeakerAudio { get; }

	// The stream stopped: the endpoint may stop until the next block; never throws.
	void Idle();
}

// The controller's USB microphone capture while wired (WiredAudio.OpenMicDefault). Stopped turns true when the
// capture gave up by itself (its endpoint kept failing): the host then closes it and the mic goes back to Bluetooth
// until the next open. A plain IDisposable capture is never treated as stopped.
public interface IWiredMicCapture : IDisposable
{
	bool Stopped { get; }
}

// Turns one haptics block (3 kHz signed 8-bit stereo) plus the buffered speaker audio into one output block of the
// USB audio endpoint: channels 3-4 get the haptics (16x upsampled, linearly interpolated between the samples so the
// jump from one 3 kHz step to the next is a ramp, not a stair), channels 1-2 the game's sound. Thread-safe for
// PushSpeaker vs Build.
public sealed class WiredAudioFrames
{
	public const int Rate = 48000;

	// Output frames per 3 kHz input sample.
	public const int Upsample = 16;

	// Speaker audio kept at most (200 ms); beyond it the oldest frames are dropped (DroppedSpeakerFrames).
	public const int SpeakerMaxFrames = Rate / 5;

	// Speaker audio buffered before playback starts (40 ms cushion: the game feeds in bursts).
	public const int SpeakerStartFrames = Rate / 25;

	// Frames of output built without a PushSpeaker after which a left-over tail below the start threshold is dropped
	// (100 ms: a stale tail must not play when the game's audio comes back later).
	public const int SpeakerFlushFrames = Rate / 10;

	// The haptics samples' scale on the endpoint (full swing by default).
	public const float HapticsGain = 1.0f;

	private readonly object gate = new();
	private readonly Queue<(short Left, short Right)> speaker = new();
	private float prevLeft, prevRight;
	private bool speakerPlaying;
	// Output frames built since the last PushSpeaker: the flush timer for a stale tail.
	private int framesSincePush;
	private long speakerUnderruns, droppedSpeakerFrames;

	public int SpeakerBuffered
	{
		get
		{
			lock (gate)
			{
				return speaker.Count;
			}
		}
	}

	// Speaker audio that would still be played: only while playing, or with enough buffered to start again — a tail
	// below SpeakerStartFrames left by an underrun does not count.
	public bool HasSpeakerAudio
	{
		get
		{
			lock (gate)
			{
				return speakerPlaying || speaker.Count >= SpeakerStartFrames;
			}
		}
	}

	// Frames taken while the game was not sending (faded out); after one the cushion is wanted again.
	public long SpeakerUnderruns => Interlocked.Read(ref speakerUnderruns);

	// Frames dropped because more than SpeakerMaxFrames were buffered.
	public long DroppedSpeakerFrames => Interlocked.Read(ref droppedSpeakerFrames);

	// The game's 4-channel 48 kHz 16-bit audio: channels 1-2 of every complete 8-byte frame are kept.
	public void PushSpeaker(ReadOnlySpan<byte> fourChannel16)
	{
		lock (gate)
		{
			framesSincePush = 0;
			for (int i = 0; i + 8 <= fourChannel16.Length; i += 8)
			{
				short left = BinaryPrimitives.ReadInt16LittleEndian(fourChannel16[i..]);
				short right = BinaryPrimitives.ReadInt16LittleEndian(fourChannel16[(i + 2)..]);
				if (speaker.Count >= SpeakerMaxFrames)
				{
					speaker.Dequeue();
					droppedSpeakerFrames++;
				}
				speaker.Enqueue((left, right));
			}
		}
	}

	// Fills output with left.Length * Upsample frames of `channels` channels each; returns the frames written.
	// Channels 2-3: the haptics ramp; channels 0-1: the speaker (silence until SpeakerStartFrames are buffered, then
	// in order until the buffer runs dry, after which the cushion is wanted again); channels beyond 4: silence.
	public int Build(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right, Span<float> output, int channels)
	{
		if (channels < 4 || output.Length < left.Length * Upsample * channels)
		{
			throw new ArgumentException("need at least 4 channels and left.Length * 16 frames of output");
		}
		lock (gate)
		{
			float prevL = prevLeft, prevR = prevRight;
			for (int i = 0; i < left.Length; i++)
			{
				float curL = Math.Clamp(left[i] / 127f * HapticsGain, -1f, 1f);
				float curR = Math.Clamp(right[i] / 127f * HapticsGain, -1f, 1f);
				for (int k = 0; k < Upsample; k++)
				{
					int at = (i * Upsample + k) * channels;
					float t = (k + 1) / (float)Upsample;
					output[at + 2] = prevL + (curL - prevL) * t;
					output[at + 3] = prevR + (curR - prevR) * t;
					if (speakerPlaying && speaker.Count == 0)
					{
						speakerPlaying = false;
						speakerUnderruns++;
					}
					if (!speakerPlaying && speaker.Count >= SpeakerStartFrames)
					{
						speakerPlaying = true;
					}
					if (speakerPlaying)
					{
						(short sl, short sr) = speaker.Dequeue();
						output[at] = sl / 32768f;
						output[at + 1] = sr / 32768f;
					}
					else
					{
						output[at] = 0;
						output[at + 1] = 0;
					}
					for (int c = 4; c < channels; c++)
					{
						output[at + c] = 0;
					}
				}
				prevL = curL;
				prevR = curR;
			}
			prevLeft = prevL;
			prevRight = prevR;
			framesSincePush += left.Length * Upsample;
			if (!speakerPlaying && speaker.Count > 0 && framesSincePush > SpeakerFlushFrames)
			{
				// A stale tail (left by an underrun, never enough to start again) is dropped, not played late.
				droppedSpeakerFrames += speaker.Count;
				speaker.Clear();
			}
			return left.Length * Upsample;
		}
	}

	// Back to a fresh start (the stream idled): no speaker audio, not playing, no haptics ramp left over.
	public void Reset()
	{
		lock (gate)
		{
			speaker.Clear();
			speakerPlaying = false;
			framesSincePush = 0;
			prevLeft = prevRight = 0;
		}
	}
}

// Keeps the endpoint's queued audio near a target without whole-block jumps: every written block is nudged a few
// frames shorter or longer so the queue drifts back instead of underrunning once or dropping a block outright.
public static class WiredAudioSteering
{
	// Queued frames aimed for before a block is written (20 ms).
	public const int TargetFrames = 960;

	// Within this of the target nothing is nudged (one block is ~512 frames).
	public const int DeadbandFrames = 96;

	// The most a block is ever shortened or stretched by.
	public const int MaxFramesPerBlock = 4;

	// The nudge for the queue size read before a block: 0 inside the deadband, else at least +-1 frame (positive =
	// repeat frames to stretch, negative = drop frames to shrink), at most MaxFramesPerBlock.
	public static int Adjust(int queuedFrames)
	{
		int diff = queuedFrames - TargetFrames;
		if (Math.Abs(diff) <= DeadbandFrames)
		{
			return 0;
		}
		int adjust = Math.Clamp(-diff / 32, -MaxFramesPerBlock, MaxFramesPerBlock);
		return adjust != 0 ? adjust : Math.Sign(-diff);
	}

	// Applies a nudge to a built block (must have room for frames + MaxFramesPerBlock): negative drops the last
	// frames, positive repeats the last frame on every channel. Returns the new frame count.
	public static int Apply(Span<float> block, int frames, int channels, int adjust)
	{
		if (adjust < 0)
		{
			return Math.Max(0, frames + adjust);
		}
		if (frames == 0 || adjust == 0)
		{
			return frames;
		}
		int last = (frames - 1) * channels;
		for (int r = 0; r < adjust; r++)
		{
			for (int c = 0; c < channels; c++)
			{
				block[(frames + r) * channels + c] = block[last + c];
			}
		}
		return frames + adjust;
	}
}

// Peak |sample| per channel pair (channels 1-2 = speaker, 3-4 = haptics) of the blocks queued since the last
// TryTake: the sink's diagnostic line says whether haptics and speaker audio actually reached the endpoint.
// Not thread-safe; the sink calls it under its gate.
public sealed class WiredAudioPeaks
{
	// The window a finished TryTake covers (~5 s of written audio).
	public const int WindowFrames = 5 * WiredAudioFrames.Rate;

	// The least a finished idle TryTake covers (~1 s).
	public const int MinIdleFrames = WiredAudioFrames.Rate;

	private float haptics;
	private float speaker;
	private int frames;

	// One more written block: frames frames of channels samples each; only channels 1-4 count.
	public void Add(ReadOnlySpan<float> block, int frames, int channels)
	{
		for (int f = 0; f < frames; f++)
		{
			int at = f * channels;
			if (channels > 1)
			{
				speaker = Math.Max(speaker, Math.Max(Math.Abs(block[at]), Math.Abs(block[at + 1])));
			}
			if (channels > 3)
			{
				haptics = Math.Max(haptics, Math.Max(Math.Abs(block[at + 2]), Math.Abs(block[at + 3])));
			}
		}
		this.frames += frames;
	}

	// True when the window is full (or, on idle, holds at least MinIdleFrames): the peaks and the frame count
	// since the last take, with the window reset. Below that it keeps accumulating across stream runs.
	public bool TryTake(bool idle, out float haptics, out float speaker, out int frames)
	{
		bool take = this.frames >= WindowFrames || idle && this.frames >= MinIdleFrames;
		haptics = this.haptics;
		speaker = this.speaker;
		frames = this.frames;
		if (!take)
		{
			return false;
		}
		this.haptics = this.speaker = 0;
		this.frames = 0;
		return true;
	}
}

// Which of Windows' audio endpoints is the controller's own USB audio device, found read-only: an endpoint's USB
// device id is compared with the one above the controller's HID interface (HID\VID_054C&PID_0CE6&MI_03\inst ->
// USB\VID_054C&PID_0CE6&MI_03\inst -> USB\VID_054C&PID_0CE6\inst). The virtual pad's own endpoints are refused.
public static class WiredAudioEndpoints
{
	// The plain USB device in a device chain (the first node "USB\VID_<id>" without an interface "&MI_<n>").
	public static string? UsbDeviceOf(IReadOnlyList<DeviceNodeInfo> chain) =>
		chain.FirstOrDefault(n => IsUsbDevice(n.InstanceId))?.InstanceId;

	private static bool IsUsbDevice(string? instanceId) =>
		instanceId is { Length: > 0 } && instanceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) &&
		!instanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase);

	// instanceOf: the HID interface's instance id; parentOf: a node's parent id. At most 4 parents are walked
	// (HID interface -> USB function -> USB device is 2); null when anything is missing or no USB device is found.
	public static string? UsbDeviceOfHid(string interfacePath, Func<string, string?> instanceOf, Func<string, string?> parentOf)
	{
		string? id = instanceOf(interfacePath);
		for (int step = 0; id != null; step++)
		{
			if (IsUsbDevice(id))
			{
				return id;
			}
			if (step >= 4)
			{
				return null;
			}
			id = parentOf(id);
		}
		return null;
	}

	// The active endpoint of a flow on exactly that USB device (never the virtual pad's); null when there is none or
	// more than one.
	public static AudioEndpointInfo? Pick(IEnumerable<AudioEndpointInfo> endpoints, AudioFlow flow, string? usbDevice)
	{
		AudioEndpointInfo? found = null;
		foreach (AudioEndpointInfo endpoint in endpoints)
		{
			if (!endpoint.IsActive || endpoint.Flow != flow || endpoint.Pad.IsVirtualPad ||
				string.IsNullOrEmpty(usbDevice) || !string.Equals(endpoint.UsbDevice, usbDevice, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (found != null)
			{
				return null;
			}
			found = endpoint;
		}
		return found;
	}

	// Pick over the system's endpoints; reads only (never SetDefault / SetMute).
	public static AudioEndpointInfo? Find(IAudioDefaultsSystem system, AudioFlow flow, string? usbDevice) =>
		Pick(system.Endpoints(flow), flow, usbDevice);
}

// Mono 48 kHz 16-bit blocks of the controller's USB microphone (pushed to MicPipeline.PushPcm while wired).
public delegate void MicPcmHandler(ReadOnlySpan<short> mono48k);

// The capture side of the USB microphone: each interleaved float block becomes one 16-bit mono stream at 48 kHz (all
// channels averaged; another input rate goes through a kept MonoResampler, recreated when the rate changes).
public sealed class WiredMicFeed(MicPcmHandler push) : IAudioCaptureSink
{
	private short[] pcm = [];
	private MonoResampler? resampler;
	private int resampleRate;
	private readonly List<short> resampled = [];

	public void OnAudio(ReadOnlySpan<float> samples, int channels, int sampleRate)
	{
		if (samples.IsEmpty || channels < 1)
		{
			return;
		}
		int frames = samples.Length / channels;
		if (pcm.Length < frames)
		{
			pcm = new short[frames];
		}
		for (int i = 0; i < frames; i++)
		{
			float sum = 0;
			for (int channel = 0; channel < channels; channel++)
			{
				sum += samples[i * channels + channel];
			}
			pcm[i] = (short)Math.Clamp(sum / channels * 32768f, short.MinValue, short.MaxValue);
		}
		if (sampleRate == WiredAudioFrames.Rate)
		{
			push(pcm.AsSpan(0, frames));
			return;
		}
		if (resampler == null || resampleRate != sampleRate)
		{
			resampler = new MonoResampler(sampleRate, WiredAudioFrames.Rate);
			resampleRate = sampleRate;
		}
		resampled.Clear();
		resampler.Process(pcm.AsSpan(0, frames), resampled);
		if (resampled.Count > 0)
		{
			push(CollectionsMarshal.AsSpan(resampled));
		}
	}
}

// Opens the controller's own USB audio endpoint for the wired link (haptics on channels 3-4, the game's speaker on
// channels 1-2) and its USB microphone (OpenMicDefault). Null when there is no such endpoint (yet); an exception
// means it could not be opened — the caller logs (never endpoint ids, names or paths).
public static class WiredAudio
{
	public static IWiredAudioSink? OpenDefault(HidDevice usb) =>
		OperatingSystem.IsWindows() ? OpenDefaultMtaGuarded(usb) : null;

	// The Core Audio objects are made on an MTA thread (a caller may be on the UI's STA thread).
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static IWiredAudioSink? OpenDefaultMtaGuarded(HidDevice usb) =>
		Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
			? Task.Run(() => OpenDefaultMta(usb)).GetAwaiter().GetResult()
			: OpenDefaultMta(usb);

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static IWiredAudioSink? OpenDefaultMta(HidDevice usb)
	{
		string? usbDevice = WiredAudioEndpoints.UsbDeviceOfHid(usb.Path, DeviceInstance.FromInterfacePath, DeviceInstance.Parent);
		AudioEndpointInfo? endpoint = WiredAudioEndpoints.Find(new WindowsAudioDefaults(), AudioFlow.Render, usbDevice);
		if (endpoint == null)
		{
			return null;
		}
		WasapiRenderSink sink = WasapiRenderSink.Open(endpoint.Id);
		Diagnostics.AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
			$"wired: USB audio output opened ({sink.Channels} ch, {sink.Format}, {(sink.ChannelMask is { } m ? $"mask 0x{m:X}" : "no mask")})"));
		return sink;
	}

	// The controller's own USB microphone while wired; null when there is none, an exception when it cannot be
	// opened (the mic then stays on Bluetooth).
	public static IDisposable? OpenMicDefault(HidDevice usb, MicPcmHandler push) =>
		OperatingSystem.IsWindows() ? OpenMicDefaultMtaGuarded(usb, push) : null;

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static IDisposable? OpenMicDefaultMtaGuarded(HidDevice usb, MicPcmHandler push) =>
		Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
			? Task.Run(() => OpenMicDefaultMta(usb, push)).GetAwaiter().GetResult()
			: OpenMicDefaultMta(usb, push);

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static IDisposable? OpenMicDefaultMta(HidDevice usb, MicPcmHandler push)
	{
		string? usbDevice = WiredAudioEndpoints.UsbDeviceOfHid(usb.Path, DeviceInstance.FromInterfacePath, DeviceInstance.Parent);
		AudioEndpointInfo? endpoint = WiredAudioEndpoints.Find(new WindowsAudioDefaults(), AudioFlow.Capture, usbDevice);
		if (endpoint == null)
		{
			return null;
		}
		IDisposable capture = WasapiCapture.OpenEndpoint(endpoint.Id, new WiredMicFeed(push));
		Diagnostics.AppLog.Write("wired: USB microphone opened");
		return capture;
	}

	// An open failure for a log line: the HRESULT for COM, else the exception's type name — never the message.
	internal static string Describe(Exception e) => e is COMException c ? $"0x{c.HResult:X8}" : e.GetType().Name;
}
