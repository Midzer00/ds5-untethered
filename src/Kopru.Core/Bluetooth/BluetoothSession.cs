// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Kopru.Core.Audio;
using Kopru.Core.Haptics;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;

namespace Kopru.Core.Bluetooth;

// A DualSense over Bluetooth: switches it to full reports, reads its input, keeps one output state (triggers, lights,
// rumble) that goes out as report 0x31, and streams haptics as report 0x32 every 10.67 ms while there is something to
// play (then a short silence, so the end isn't cut off). While a game streams audio to the virtual pad (FeedHaptics),
// silent or not, the stream keeps running as it always did; it stops only once nothing feeds it at all. With
// PauseHapticsInSilence on (opt-in, and in Low Power mode) silence from the game does not keep it running either. While
// stopped, its one long-lived thread waits without a timer, sends nothing and holds no timer resolution.
//
// It can also move over to a second, wired (USB) link of the same controller (AttachWired, EXPERIMENTAL wired mode):
// report 0x01 in, 0x02 out, and with an IWiredAudioSink the haptics stream's blocks on the controller's own USB audio
// endpoint (channels 3-4) with the game's speaker audio (channels 1-2). The host then disconnects Bluetooth (owner
// decision 2026-11-24: no dual-link fallback): once Bluetooth is gone its reader ends quietly, and the session keeps
// running on USB alone, so the virtual pad never notices the switch. When the USB link fails after that (the cable
// was pulled) the controller counts as lost (Lost), as a Bluetooth controller switched off does. While Bluetooth is
// still connected (the hand-off has not happened, or the radio refused it) a failed USB link falls back to it.
public delegate void InputBodyHandler(ReadOnlySpan<byte> body);

public sealed class BluetoothSession : IDisposable
{
	// Reading the calibration feature report switches the controller from its short reports to full 0x31 reports.
	public const byte CalibrationFeature = 0x05;

	// About 1 s with nothing to play and nothing feeding the stream (no game audio packets, sound, rumble, speaker audio or
	// mic) before it stops: the game closed or released the pad's audio device. A stream fed by the game's audio never
	// stops on silence (stopping and starting again on every effect after a quiet moment would risk added latency).
	public const int SilentReportsBeforeStop = 94;

	// A game audio packet within this time counts as the game still feeding the stream (its packets come about every
	// 10 ms, sometimes held back by 20-30 ms).
	public static readonly TimeSpan FeedWindow = TimeSpan.FromMilliseconds(100);

	// A stream that never carried sound (the mic-off burst, a rumble start that stayed silent) stops after this many
	// silent reports instead (~250 ms).
	public const int ShortSilentReportsBeforeStop = 24;

	// Lateness beyond this many report intervals (a stalled thread, a suspended machine) restarts the schedule from now
	// instead of sending the missed reports back to back.
	public const int ResyncAfterIntervals = 3;

	private readonly IHidLink link;

	// The wired (USB) link of the same controller while attached (AttachWired); changed only with Interlocked.
	private IHidLink? wired;

	// The wired link's USB audio endpoint while attached (it lives exactly as long as the link); Interlocked only.
	private Audio.IWiredAudioSink? wiredAudio;

	// The wired link's input liveness: the Stopwatch timestamp of the last USB input published (set at attach) and
	// whether any USB input was published yet. Written under inputGate by the wired reader, reset in AttachWired;
	// meaningful only while `wired` is that link.
	private long lastWiredInput;
	private bool wiredInputLive;

	private Thread? wiredReader;

	// Guards publishing input so the Bluetooth and the wired reader threads never interleave a report.
	private readonly object inputGate = new();

	private readonly object outputGate = new();

	private readonly byte[] block = new byte[OutputBlock.Size];

	private readonly object hapticsGate = new();

	private Thread? reader;

	private Thread? hapticsThread;

	// The stream is running (guarded by hapticsGate); the thread waits on hapticsGate while it is not.
	private bool hapticsActive;

	private string startReason = string.Empty;

	private long hapticsStreams, hapticsThreadsStarted;

	private volatile bool stopping;

	private int sequence;

	// link: the controller's Bluetooth link, or (wired mode taking over a controller that is on the cable alone) its
	// USB link: the session is then wired on it from the start, with no Bluetooth at all (IsUsbOnly).
	public BluetoothSession(IHidLink link)
	{
		if (link.Device.Transport == Transport.Usb)
		{
			usbOnly = true;
			wired = link;
			bluetoothGone = true;
			lastWiredInput = Stopwatch.GetTimestamp();
		}
		else if (link.Device.Transport != Transport.Bluetooth)
		{
			throw new ArgumentException("Not a DualSense link", nameof(link));
		}
		this.link = link;
	}

	private readonly bool usbOnly;

	// Runs on the controller's USB link alone (taken over while on the cable, never on Bluetooth in this session):
	// input 0x01, output 0x02, haptics and speaker only through a wired audio sink.
	public bool IsUsbOnly => usbOnly;

	public HidDevice Device => link.Device;

	public HapticsMixer Haptics { get; } = new();

	// Report 0x31 keeps the actuators free for the haptics stream (no rumble bits, power controls at 0).
	public bool HapticsMode { get; set; } = true;

	// How much the controller buffers before playing: 0x20 is about 50 ms, 0x08 about 25 ms.
	public byte HapticsBufferDepth { get; set; } = BluetoothReports.DefaultBufferDepth;

	public InputState LastInput { get; private set; }

	// True when LastInput came over the USB link (report 0x01), false over Bluetooth (0x31).
	public bool LastInputOverUsb { get; private set; }

	// EXPERIMENTAL Bluetooth speaker / headset audio (null = off, the default). While it has audio, the haptics stream
	// sends report 0x36 (haptics + one Opus frame) instead of 0x32; route and volume follow the headphone jack.
	public SpeakerAudioSender? Speaker
	{
		get => speaker;
		set
		{
			if (value != null)
			{
				value.Headset = HeadphonesPlugged;
			}
			SpeakerAudioSender? was = speaker;
			speaker = value;
			if ((was != null) != (value != null))
			{
				Diagnostics.AppLog.Write(value != null ? "bt: speaker stream attached"
					: $"bt: speaker stream detached ({SpeakerReports} speaker reports, {was!.Underruns} underruns, {was.DroppedSamples} samples dropped)");
			}
			ApplySpeakerRoute();
		}
	}

	private SpeakerAudioSender? speaker;

	public long SpeakerReports => Interlocked.Read(ref speakerReports);

	private long speakerReports;

	// Feeds the game's 4-channel audio (channels 1-2 used) to Speaker and wakes the stream; no-op when Speaker is off.
	// While the wired link's audio endpoint plays it, the Opus sender is not fed.
	public void PushSpeakerAudio(ReadOnlySpan<byte> audio)
	{
		SpeakerAudioSender? s = speaker;
		if (s == null)
		{
			return;
		}
		if (Volatile.Read(ref wiredAudio) is { } sink)
		{
			sink.PushSpeaker(audio);
			WakeHaptics();
			return;
		}
		s.Push(audio);
		WakeHaptics();
	}

	// Sends the current speaker volume and route (call after changing Speaker.Volume or HeadphoneVolume).
	public void ApplySpeakerRoute()
	{
		UpdateOutput(b =>
		{
			if (!WriteRoute(b))
			{
				AudioOutput.Clear(b);
			}
		});
	}

	// Puts the speaker route and volume into an output block about to be sent (e.g. the game's own output, which would
	// otherwise overwrite them); no change while Speaker is off (unless the speaker idle volume applies).
	public void WriteSpeakerRoute(Span<byte> block) => WriteRoute(block);

	// Fix 8 diagnostic (KOPRU_WIRED_SPEAKER_IDLE, WiredAudioTuning.SpeakerIdleVolume): while the wired audio endpoint
	// plays and its channels 1-2 carry no sound, the controller's speaker gets this volume (0 = muted, preamp 0) instead
	// of the user's, also while the speaker setting is off. Null (the default): the route is unchanged.
	public Audio.WiredAudioTuning WiredTuning { get; set; } = Audio.WiredAudioTuning.Current;

	// What the haptics thread last saw of the wired sink's SpeakerSounding (the route follows it).
	private volatile bool wiredSpeakerSounding;

	// The speaker idle volume in force now, or null.
	private double? SpeakerIdleVolumeNow() =>
		WiredTuning.SpeakerIdleVolume is { } idle && Volatile.Read(ref wiredAudio) != null && !wiredSpeakerSounding ? idle : null;

	// Writes the route (user volume, or the idle volume while it applies); false when nothing was written (speaker off).
	private bool WriteRoute(Span<byte> block)
	{
		SpeakerAudioSender? s = speaker;
		double? idle = SpeakerIdleVolumeNow();
		if (idle is { } v)
		{
			AudioOutput.SetRoute(block, s?.Headset ?? HeadphonesPlugged, v, 0);
			return true;
		}
		if (s == null)
		{
			return false;
		}
		AudioOutput.SetRoute(block, s.Headset, s.OutputVolume);
		return true;
	}

	// After the wired sink came or went: the route again when the speaker idle volume is in use.
	private void RefreshIdleRoute()
	{
		if (WiredTuning.SpeakerIdleVolume == null)
		{
			return;
		}
		try
		{
			ApplySpeakerRoute();
		}
		catch (Exception)
		{
			// The next output fixes the state.
		}
	}

	// On the haptics thread: the sink's speaker turned sounding or quiet, so the route changes; sent off the thread so
	// the HID write never delays a block.
	private void FollowWiredSpeaker(Audio.IWiredAudioSink sink)
	{
		if (WiredTuning.SpeakerIdleVolume == null)
		{
			return;
		}
		bool sounding = sink.SpeakerSounding;
		if (sounding == wiredSpeakerSounding)
		{
			return;
		}
		wiredSpeakerSounding = sounding;
		ThreadPool.QueueUserWorkItem(_ => RefreshIdleRoute());
	}

	// EXPERIMENTAL Bluetooth microphone (off by default): asks for the mic in the stream-control sub-packet of every
	// 0x32 / 0x36 report and keeps that stream running while on; reports marked as mic audio (MicPackets) go to
	// MicReportReceived. Mic reports (MicPackets: id 0x31, byte-1 type 2) are NEVER read as gamepad input, whether or
	// not the mic is on: measured on hardware, a controller asked once keeps sending them (about 100/s, valid input CRC)
	// in later sessions too, and read as input they move the left stick and press random buttons.
	// Switching off sends MicOffReports stream reports with the off value before the stream may stop (before, the stream
	// stopped at once and the controller never heard "off"). Mic reports arriving while the mic is off trigger the same
	// burst, at most once per StrayMicOffInterval (StopStrayMic).
	public bool MicStreamEnabled
	{
		get => micStreamEnabled;
		set
		{
			bool was = micStreamEnabled;
			micStreamEnabled = value;
			if (value != was)
			{
				Diagnostics.AppLog.Write(value
					? $"bt: mic stream on (streaming {IsHapticsStreaming})"
					: $"bt: mic stream off, sending mic off ({MicReports} mic reports, {StrayMicReports} while off, {InputReports} inputs, {RejectedReports} rejected)");
			}
			if (value)
			{
				WakeHaptics();
			}
			else if (was)
			{
				SendMicOff(MicOffReports);
			}
		}
	}

	// Stream reports carrying the mic-off value after the mic is switched off (about 250 ms).
	public const int MicOffReports = ShortSilentReportsBeforeStop;

	public static readonly TimeSpan StrayMicOffInterval = TimeSpan.FromSeconds(2);

	// Mic reports while the mic is off make the session send "mic off" (bounded, see MicStreamEnabled). The probe's
	// control run turns this off to watch the controller untouched.
	public bool StopStrayMic { get; set; } = true;

	// Sends this many stream reports with the mic-off value (the stream runs at least that long) while the mic is off.
	public void SendMicOff(int reports)
	{
		Volatile.Write(ref micOffToSend, Math.Max(0, reports));
		WakeHaptics();
	}

	// Stream reports with the mic-off value still to send.
	public int MicOffPending => Volatile.Read(ref micOffToSend);

	// Mic-off stream reports sent in all (diagnostics, tests).
	public long MicOffSent => Interlocked.Read(ref micOffSent);

	private long micOffSent;

	private volatile bool micStreamEnabled;

	private int micOffToSend;

	private long lastStrayOff = long.MinValue;

	private int strayLogged;

	// Raised on the reader thread with each raw mic report (only while MicStreamEnabled).
	public event InputBodyHandler? MicReportReceived;

	// Every report read, before any filtering (diagnostics: Kopru.Probe mic dumps the unknown ones).
	public event InputBodyHandler? RawReportReceived;

	public long MicReports => Interlocked.Read(ref micReports);

	private long micReports;

	// Mic reports read while the mic was off (kept out of the input path, not decoded).
	public long StrayMicReports => Interlocked.Read(ref strayMicReports);

	private long strayMicReports;

	// Reports that were neither mic audio nor gamepad input with a valid CRC (dropped).
	public long RejectedReports => Interlocked.Read(ref rejectedReports);

	private long rejectedReports;

	// Headphones in the controller's jack (input status bit), tracked whether or not the speaker is on.
	public bool HeadphonesPlugged { get; private set; }

	public long InputReports => Interlocked.Read(ref inputReports);

	private long inputReports;

	// Stopwatch timestamp of the last successful read (valid on the reader thread, e.g. inside InputBodyReceived).
	public long LastReadTicks { get; private set; }

	public long HapticsReports => Interlocked.Read(ref hapticsReports);

	private long hapticsReports;

	// True while the haptics stream runs (it stops itself after a silent stretch; the thread then waits, idle).
	public bool IsHapticsStreaming
	{
		get
		{
			lock (hapticsGate)
			{
				return hapticsActive;
			}
		}
	}

	// Times the stream started (diagnostics, tests).
	public long HapticsStreams => Interlocked.Read(ref hapticsStreams);

	// Haptics threads created: 1 once the stream first ran, whatever the number of streams (diagnostics, tests).
	public long HapticsThreadsStarted => Interlocked.Read(ref hapticsThreadsStarted);

	// Silent frames kept buffered while the stream is idle: a sound that starts plays behind this cushion (~21 ms, as much
	// as the rumble keeps ahead), so the game's audio arriving in ~10 ms bursts does not run the first reports dry.
	public const int IdleCushionFrames = 2 * BluetoothReports.HapticsFrames;

	// Called on the haptics thread before each report takes its frames, for a source that keeps frames buffered ahead
	// (the bridge's rumble while the game sends no audio). Must be quick and must not call back into the stream.
	public Action? HapticsRefill { get; set; }

	// Makes the wait the haptics thread paces its reports with (tests); null = PreciseWait.Create.
	public Func<IPreciseWait>? PreciseWaitFactory { get; set; }

	// Raised on the reader thread for every full input report.
	public event Action<InputState>? InputReceived;

	// Raised on the reader thread with the raw 63-byte input body (sticks, buttons, gyro, touch...) of every full input
	// report, before InputReceived. The span is only valid during the call.
	public event InputBodyHandler? InputBodyReceived;

	// Raised once (on the reader thread) when reads keep failing: the controller was switched off, went out of range or
	// was removed. Reading stops; dispose the session.
	public event Action? Lost;

	// Consecutive failed reads (10 ms apart) before the controller counts as lost.
	public int LostAfterFailures { get; set; } = 50;

	// True while a wired (USB) link carries input and output (AttachWired).
	public bool IsWired => Volatile.Read(ref wired) != null;

	public HidDevice? WiredDevice => Volatile.Read(ref wired)?.Device;

	// True while the wired link's USB audio endpoint carries haptics and the game's speaker.
	public bool IsWiredAudio => Volatile.Read(ref wiredAudio) != null;

	// Raised (on the wired reader) after the session fell back to Bluetooth by itself (only while Bluetooth is still
	// connected); the argument is a short fixed reason.
	public event Action<string>? WiredLost;

	// Raised (on the haptics thread) when the wired audio sink failed and haptics/speaker moved back to Bluetooth.
	public event Action<string>? WiredAudioLost;

	// Consecutive failed USB reads (10 ms apart) before the wired link counts as gone.
	public int WiredLostAfterFailures { get; set; } = 3;

	// True once the wired link delivered its first input report (the host disconnects Bluetooth only after that).
	public bool IsWiredInputLive
	{
		get
		{
			lock (inputGate)
			{
				return Volatile.Read(ref wired) != null && wiredInputLive;
			}
		}
	}

	// True once the Bluetooth link's reads failed while wired (it was disconnected on purpose): the session runs on
	// USB alone.
	public bool IsBluetoothGone => Volatile.Read(ref bluetoothGone);

	private bool bluetoothGone;

	// Input reports taken from the wired link.
	public long WiredInputReports => Interlocked.Read(ref wiredInputReports);

	private long wiredInputReports;

	// Valid Bluetooth input reports dropped while wired (the USB link carries input then).
	public long IgnoredBluetoothInputs => Interlocked.Read(ref ignoredBluetoothInputs);

	private long ignoredBluetoothInputs;

	public void Start()
	{
		try
		{
			link.GetFeature(CalibrationFeature);
		}
		catch (Exception)
		{
			// Some controllers answer late right after connecting; the first 0x31 output switches them too.
		}
		// Stop the blue start-up glow, so the lightbar takes the colour that is set.
		UpdateOutput(b =>
		{
			b[OutputBlock.Flag2] |= OutputBlock.LightbarSetupControl;
			b[OutputBlock.LightbarSetup] = OutputBlock.LightOut;
		});
		UpdateOutput(b =>
		{
			b[OutputBlock.Flag2] &= unchecked((byte)~OutputBlock.LightbarSetupControl);
			b[OutputBlock.LightbarSetup] = 0;
		});
		if (usbOnly)
		{
			Diagnostics.AppLog.Write("wired: controller on the cable alone, input and output over USB (no Bluetooth)");
			wiredReader = reader = new Thread(() => WiredReadLoop(link)) { Name = "Controller input (USB)", IsBackground = true, Priority = ThreadPriority.AboveNormal };
		}
		else
		{
			reader = new Thread(ReadLoop) { Name = "Controller input", IsBackground = true, Priority = ThreadPriority.AboveNormal };
		}
		reader.Start();
	}

	// The light as last written to the controller (whoever set it); read-only, any thread.
	public Lighting.LightOutput SentLight => Volatile.Read(ref sentLight);

	private Lighting.LightOutput sentLight = Lighting.LightOutput.None;

	// Changes the output state and sends it at once: over USB (0x02) while wired, else over Bluetooth (0x31). A failed
	// USB write is left to the wired reader (a pulled cable fails its reads too).
	public void UpdateOutput(Action<byte[]> change)
	{
		lock (outputGate)
		{
			change(block);
			byte[] sent = (byte[])block.Clone();
			Volatile.Write(ref sentLight, sentLight.After(sent));
			if (Volatile.Read(ref wired) is { } usb)
			{
				if (HapticsMode)
				{
					OutputBlock.SetUsbHapticsMode(sent);
				}
				byte[] report = new byte[Math.Max(usb.Device.OutputLength, UsbReports.OutputSize)];
				UsbReports.BuildOutput(sent, report);
				try
				{
					usb.Write(report);
				}
				catch (Exception)
				{
					// The cable is going; the wired reader notices.
				}
				return;
			}
			if (usbOnly)
			{
				// The USB link is gone and there is no Bluetooth: nothing to send to.
				return;
			}
			if (HapticsMode)
			{
				OutputBlock.SetHapticsMode(sent);
			}
			byte[] btReport = new byte[Math.Max(link.Device.OutputLength, BluetoothReports.OutputSize)];
			BluetoothReports.BuildOutput(sent, NextSequence(), btReport);
			link.Write(btReport);
		}
	}

	// A source that streams continuously (the game's audio to the virtual pad) is still sending, sound or silence: the
	// stream starts if it is stopped and keeps running while the source keeps feeding it (call after pushing its frames).
	// With PauseHapticsInSilence on, only its sound counts (WakeHaptics).
	public void FeedHaptics()
	{
		Volatile.Write(ref lastFed, Time.GetTimestamp());
		WakeHaptics();
	}

	// The clock FeedWindow is measured on (tests simulate time with it).
	public TimeProvider Time { get; init; } = TimeProvider.System;

	private long lastFed = long.MinValue / 2;

	private bool Fed => !pauseInSilence && Time.GetElapsedTime(Volatile.Read(ref lastFed)) <= FeedWindow;

	// Opt-in (off by default; Low Power turns it on): the stream stops ~1 s after the last sound even while the game keeps
	// streaming silence, and starts again on the next sound behind IdleCushionFrames. Safe to switch at any time: turning
	// it on lets a running stream count its silence from then on; turning it off restarts a stopped stream at once if the
	// game is still feeding it.
	public bool PauseHapticsInSilence
	{
		get => pauseInSilence;
		set
		{
			pauseInSilence = value;
			if (!value)
			{
				WakeHaptics();
			}
		}
	}

	private volatile bool pauseInSilence;

	// Starts the haptics stream if it is idle and there is something to play (call after pushing frames into Haptics):
	// frames that are not silent, speaker audio, the mic, or mic-off reports. Silence alone does not start it; while idle
	// only the newest IdleCushionFrames of it are kept.
	public void WakeHaptics()
	{
		lock (hapticsGate)
		{
			if (hapticsActive || stopping)
			{
				return;
			}
			string? reason = StreamWork();
			if (reason == null)
			{
				Haptics.TrimSilence(IdleCushionFrames);
				return;
			}
			Activate(reason);
		}
	}

	// Starts the haptics stream even with nothing buffered yet (a source about to fill it, e.g. rumble); it stops again
	// after the silent tail if nothing comes.
	public void StartHaptics(string reason)
	{
		lock (hapticsGate)
		{
			if (!hapticsActive && !stopping)
			{
				Activate(reason);
			}
		}
	}

	// Caller holds hapticsGate.
	private void Activate(string reason)
	{
		hapticsActive = true;
		startReason = reason;
		if (hapticsThread == null)
		{
			Interlocked.Increment(ref hapticsThreadsStarted);
			hapticsThread = new Thread(HapticsLoop) { Name = "Controller haptics", IsBackground = true, Priority = ThreadPriority.Highest };
			hapticsThread.Start();
		}
		else
		{
			Monitor.Pulse(hapticsGate);
		}
	}

	// Attaches the controller's own USB link: input and output move over the cable (the host disconnects Bluetooth
	// once USB input is live). audio: its USB audio endpoint (WiredAudio.OpenDefault), which then carries the haptics
	// stream's blocks and the game's speaker audio; it lives exactly as long as the link. Ends by sending the current
	// state over USB.
	public void AttachWired(IHidLink usb, Audio.IWiredAudioSink? audio = null)
	{
		if (usb.Device.Transport != Transport.Usb)
		{
			throw new ArgumentException("Not a USB link", nameof(usb));
		}
		if (stopping)
		{
			throw new InvalidOperationException("The session is disposed");
		}
		lock (inputGate)
		{
			if (Interlocked.CompareExchange(ref wired, usb, null) != null)
			{
				throw new InvalidOperationException("A wired link is already attached");
			}
			lastWiredInput = Stopwatch.GetTimestamp();
			wiredInputLive = false;
		}
		if (audio != null)
		{
			if (Interlocked.Exchange(ref wiredAudio, audio) is { } displaced)
			{
				// A retry attached its sink between the link CAS and this line.
				try
				{
					displaced.Dispose();
				}
				catch (Exception)
				{
					// A sink that fails to close is gone either way.
				}
			}
			if (Volatile.Read(ref wired) != usb)
			{
				// The link left during this attach: the sink never outlives it.
				Interlocked.Exchange(ref wiredAudio, null);
				audio.Dispose();
				return;
			}
			Diagnostics.AppLog.Write("wired: haptics and speaker over USB audio");
			wiredSpeakerSounding = false;
		}
		wiredReader = new Thread(() => WiredReadLoop(usb)) { Name = "Controller input (USB)", IsBackground = true, Priority = ThreadPriority.AboveNormal };
		wiredReader.Start();
		Diagnostics.AppLog.Write(audio != null
			? "wired: input and output over USB, haptics/speaker over USB audio"
			: "wired: input and output over USB");
		try
		{
			UpdateOutput(b =>
			{
				if (WiredTuning.SpeakerIdleVolume != null && !WriteRoute(b))
				{
					AudioOutput.Clear(b);
				}
			});
		}
		catch (Exception)
		{
			// The next output fixes the state.
		}
	}

	// The wired link's USB audio endpoint, attached after the link itself (it appeared late, or an earlier sink was
	// lost): the haptics stream and the game's speaker go over it from the next tick/push on. True when the sink now
	// carries them; false — with the sink disposed — when the session is stopping, the wired link's device is not
	// usb, or a sink is already attached.
	public bool AttachWiredAudio(HidDevice usb, Audio.IWiredAudioSink audio)
	{
		bool attached = !stopping && Volatile.Read(ref wired)?.Device.Path is { } path &&
			string.Equals(path, usb.Path, StringComparison.OrdinalIgnoreCase);
		if (attached && Interlocked.CompareExchange(ref wiredAudio, audio, null) == null)
		{
			attached = !stopping && Volatile.Read(ref wired)?.Device.Path is { } again &&
				string.Equals(again, usb.Path, StringComparison.OrdinalIgnoreCase);
			if (attached)
			{
				Diagnostics.AppLog.Write("wired: haptics and speaker over USB audio");
				wiredSpeakerSounding = false;
				RefreshIdleRoute();
				return true;
			}
			// The link went away during this attach: take the sink back when this call still owns it — whoever
			// otherwise took it out of the field (FallBack/DetachWired/Dispose) already disposes it.
			if (Interlocked.CompareExchange(ref wiredAudio, null, audio) != audio)
			{
				return false;
			}
		}
		try
		{
			audio.Dispose();
		}
		catch (Exception)
		{
			// A sink that fails to close is gone either way.
		}
		return false;
	}

	// Detaches the wired link (the mode changed, the host leaves wired): back to Bluetooth over everything, ending by
	// sending the current state over it. With Bluetooth already disconnected nothing carries the controller any more:
	// the session raises Lost (the host waits for Bluetooth again). False when no wired link was attached.
	public bool DetachWired(string reason)
	{
		IHidLink? usb = Interlocked.Exchange(ref wired, null);
		if (usb == null)
		{
			return false;
		}
		usb.CancelRead();
		IWiredAudioSink? audio = Interlocked.Exchange(ref wiredAudio, null);
		Thread? reader = wiredReader;
		if (reader != null && reader != Thread.CurrentThread)
		{
			// 500 ms, called holding no gate: the reader can be inside Publish (inputGate -> outputGate).
			reader.Join(500);
		}
		usb.Dispose();
		if (audio != null)
		{
			try
			{
				audio.Dispose();
			}
			catch (Exception)
			{
				// A sink that fails to close is gone either way.
			}
		}
		Diagnostics.AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
			$"wired: back on Bluetooth ({reason}; {WiredInputReports} usb inputs, {IgnoredBluetoothInputs} bluetooth inputs ignored)"));
		if (Volatile.Read(ref bluetoothGone))
		{
			Diagnostics.AppLog.Write("wired: Bluetooth was disconnected, so the controller is gone until it reconnects");
			Lost?.Invoke();
			return true;
		}
		try
		{
			// The user's speaker route again if the speaker idle volume was in force.
			UpdateOutput(b =>
			{
				if (WiredTuning.SpeakerIdleVolume != null && !WriteRoute(b))
				{
					AudioOutput.Clear(b);
				}
			});
		}
		catch (Exception)
		{
			// The next output fixes the state.
		}
		return true;
	}

	// The wired audio sink failed (a write threw): drop just it — the wired link stays, haptics and speaker are back
	// on Bluetooth. True-style guard via CompareExchange so only the first caller disposes, logs and raises.
	private void DropWiredAudio(Audio.IWiredAudioSink sink, string reason)
	{
		if (Interlocked.CompareExchange(ref wiredAudio, null, sink) != sink)
		{
			return;
		}
		try
		{
			sink.Dispose();
		}
		catch (Exception)
		{
			// A sink that fails to close is gone either way.
		}
		Diagnostics.AppLog.Write($"wired: audio back on Bluetooth ({reason})");
		RefreshIdleRoute();
		WiredAudioLost?.Invoke(reason);
	}

	// The session falls back to Bluetooth by itself (failed USB reads while Bluetooth is still connected) when usb is
	// still the wired link.
	// True when this call did it; never joins the wired reader (it can be the caller).
	private bool FallBack(IHidLink usb, string reason)
	{
		if (Interlocked.CompareExchange(ref wired, null, usb) != usb)
		{
			return false;
		}
		usb.CancelRead();
		if (Interlocked.Exchange(ref wiredAudio, null) is { } audio)
		{
			try
			{
				audio.Dispose();
			}
			catch (Exception)
			{
				// A sink that fails to close is gone either way.
			}
		}
		usb.Dispose();
		Diagnostics.AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
			$"wired: back on Bluetooth ({reason}; {WiredInputReports} usb inputs, {IgnoredBluetoothInputs} bluetooth inputs ignored)"));
		RefreshIdleRoute();
		return true;
	}

	public void Dispose()
	{
		stopping = true;
		// A wired link is dropped quietly first (no resend): its reader leaves once the read is cancelled.
		if (Interlocked.Exchange(ref wired, null) is { } usb)
		{
			usb.CancelRead();
			if (Interlocked.Exchange(ref wiredAudio, null) is { } audio)
			{
				try
				{
					audio.Dispose();
				}
				catch (Exception)
				{
					// A sink that fails to close is gone either way.
				}
			}
			if (wiredReader is { } wr && wr != Thread.CurrentThread)
			{
				wr.Join(1000);
			}
			usb.Dispose();
		}
		if (!usbOnly)
		{
			link.CancelRead();
			reader?.Join(1000);
		}
		Thread? haptics;
		lock (hapticsGate)
		{
			haptics = hapticsThread;
			Monitor.PulseAll(hapticsGate);
		}
		haptics?.Join(1000);
		if (!usbOnly)
		{
			link.Dispose();
		}
	}

	private byte NextSequence() => (byte)(Interlocked.Increment(ref sequence) & 0x0F);

	private void ReadLoop()
	{
		byte[] buffer = new byte[Math.Max(link.Device.InputLength, BluetoothReports.InputSize)];
		int failures = 0;
		while (!stopping)
		{
			int length;
			try
			{
				length = link.Read(buffer);
			}
			catch (Exception e)
			{
				if (stopping)
				{
					return;
				}
				if (Volatile.Read(ref wired) != null)
				{
					// Wired: the host disconnected Bluetooth on purpose (or it dropped); the session runs on USB alone.
					Volatile.Write(ref bluetoothGone, true);
					Diagnostics.AppLog.Write($"bt: link gone while wired ({e.GetType().Name}), the controller runs over USB only");
					return;
				}
				if (++failures >= LostAfterFailures)
				{
					Diagnostics.AppLog.Write($"bt: {failures} reads failed in a row, controller lost ({e.GetType().Name}: {e.Message})");
					Lost?.Invoke();
					return;
				}
				Thread.Sleep(10);
				continue;
			}
			failures = 0;
			ReadOnlySpan<byte> report = buffer.AsSpan(0, length);
			RawReportReceived?.Invoke(report);
			if (MicPackets.IsMicReport(report))
			{
				if (micStreamEnabled)
				{
					Interlocked.Increment(ref micReports);
					MicReportReceived?.Invoke(report);
				}
				else
				{
					OnStrayMicReport();
				}
				continue;
			}
			if (!BluetoothReports.InputCrcValid(report))
			{
				Interlocked.Increment(ref rejectedReports);
				continue;
			}
			ReadOnlySpan<byte> body = InputState.Body(report);
			long readTicks = Stopwatch.GetTimestamp();
			lock (inputGate)
			{
				// While wired the USB link carries input: a valid Bluetooth report still arriving (before the host
				// disconnected Bluetooth) is counted and dropped, except until the USB link delivered its first report.
				if (Volatile.Read(ref wired) is null || !wiredInputLive)
				{
					Publish(body, readTicks, false);
				}
				else
				{
					Interlocked.Increment(ref ignoredBluetoothInputs);
				}
			}
		}
	}

	// The wired reader: report 0x01 carries the same input body as Bluetooth's 0x31 (no CRC). Runs while this link is
	// the wired one (a detach cancels its read). Enough failed reads in a row: with Bluetooth disconnected the
	// controller is lost (the cable was pulled); with Bluetooth still connected the session falls back to it.
	private void WiredReadLoop(IHidLink usb)
	{
		byte[] buffer = new byte[Math.Max(usb.Device.InputLength, UsbReports.InputSize)];
		int failures = 0;
		while (!stopping && Volatile.Read(ref wired) == usb)
		{
			int length;
			try
			{
				length = usb.Read(buffer);
			}
			catch (Exception)
			{
				if (stopping || Volatile.Read(ref wired) != usb)
				{
					return;
				}
				if (++failures >= WiredLostAfterFailures)
				{
					if (FallBack(usb, "usb reads failed"))
					{
						if (Volatile.Read(ref bluetoothGone))
						{
							Diagnostics.AppLog.Write("wired: USB link gone with Bluetooth disconnected, controller lost");
							Lost?.Invoke();
						}
						else
						{
							WiredLost?.Invoke("usb reads failed");
						}
					}
					return;
				}
				Thread.Sleep(10);
				continue;
			}
			failures = 0;
			ReadOnlySpan<byte> report = buffer.AsSpan(0, length);
			RawReportReceived?.Invoke(report);
			ReadOnlySpan<byte> body = InputState.Body(report);
			if (body.IsEmpty)
			{
				continue;
			}
			lock (inputGate)
			{
				if (Volatile.Read(ref wired) == usb)
				{
					long now = Stopwatch.GetTimestamp();
					lastWiredInput = now;
					wiredInputLive = true;
					Interlocked.Increment(ref wiredInputReports);
					Publish(body, now, true);
				}
			}
		}
	}

	// The tail of a good input report on either link: state, headphone tracking and the events. inputGate held, so
	// LastReadTicks and LastInput can never be set by the two reader threads at once.
	private void Publish(ReadOnlySpan<byte> body, long readTicks, bool overUsb)
	{
		LastReadTicks = readTicks;
		LastInput = InputState.Parse(body);
		LastInputOverUsb = overUsb;
		bool plugged = AudioOutput.HeadphonesPlugged(body);
		HeadphonesPlugged = plugged;
		SpeakerAudioSender? s = speaker;
		if (s != null && plugged != s.Headset)
		{
			s.Headset = !s.Headset;
			ApplySpeakerRoute();
		}
		Interlocked.Increment(ref inputReports);
		InputBodyReceived?.Invoke(body);
		InputReceived?.Invoke(LastInput);
	}

	// The haptics thread: started once, then waits (no timer, no spinning, nothing sent) until WakeHaptics or StartHaptics
	// makes it active, streams while there is something to play, and goes back to waiting after the silent tail.
	private void HapticsLoop()
	{
		sbyte[] left = new sbyte[BluetoothReports.HapticsFrames];
		sbyte[] right = new sbyte[BluetoothReports.HapticsFrames];
		byte[] report = new byte[Math.Max(link.Device.OutputLength, Audio.SpeakerReports.Size)];
		byte[] opus = new byte[Audio.SpeakerReports.OpusFrameBytes];
		IPreciseWait wait;
		try
		{
			wait = (PreciseWaitFactory ?? PreciseWait.Create)();
		}
		catch (Exception)
		{
			wait = PreciseWait.CreateFallback();
		}
		StreamTotals totals = new();
		try
		{
			while (true)
			{
				string reason;
				lock (hapticsGate)
				{
					while (!hapticsActive && !stopping)
					{
						Monitor.Wait(hapticsGate);
					}
					if (stopping)
					{
						break;
					}
					reason = startReason;
				}
				Stream(wait, reason, left, right, report, opus, ref totals);
			}
		}
		finally
		{
			wait.Dispose();
			lock (hapticsGate)
			{
				hapticsActive = false;
			}
			if (totals.Streams > 0)
			{
				Diagnostics.AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"bt: haptics stream: totals {totals.Streams} streams, {totals.ActiveMs / 1000.0:F1} s active, {totals.Reports} reports, {totals.Timing.Describe(Stopwatch.Frequency)}, {totals.Resyncs} resyncs, timer {PreciseWait.Name(wait)}"));
			}
		}
	}

	// Reports more than this late (after the deadline) count as late in the stream log lines.
	public static readonly TimeSpan LateReport = TimeSpan.FromMilliseconds(1);

	// A link.Write taking longer than this counts as slow in the stream log lines.
	public static readonly TimeSpan SlowWrite = TimeSpan.FromMilliseconds(2);

	// Start/stop lines written per session at most (the totals line at the end is always written).
	public const int StreamLogLimit = 100;

	private struct StreamTotals
	{
		public long Streams, Reports, Resyncs;
		public double ActiveMs;
		public HapticsTiming Timing;
	}

	// One active stretch: a report every 10.67 ms until SilentReportsBeforeStop silent reports in a row (or
	// ShortSilentReportsBeforeStop when nothing audible was sent) with nothing left to play (and the mic off), then idle.
	private void Stream(IPreciseWait wait, string reason, sbyte[] left, sbyte[] right, byte[] report, byte[] opus, ref StreamTotals totals)
	{
		long interval = (long)(BluetoothReports.HapticsInterval.TotalSeconds * Stopwatch.Frequency);
		long lateTicks = (long)(LateReport.TotalSeconds * Stopwatch.Frequency);
		long slowTicks = (long)(SlowWrite.TotalSeconds * Stopwatch.Frequency);
		long started = Stopwatch.GetTimestamp();
		long next = started;
		long reports = 0, resyncs = 0;
		HapticsTiming timing = default;
		int silent = 0;
		bool hadSound = false;
		byte counter = 0;
		int sinkWrites = 0;
		bool firstToUsb = false;
		long stream = ++totals.Streams;
		Interlocked.Increment(ref hapticsStreams);
		try
		{
			while (!stopping)
			{
				try
				{
					HapticsRefill?.Invoke();
				}
				catch (Exception)
				{
					// A refill source failing must not stop the stream.
				}
				bool sound = Haptics.Take(left, right);
				Audio.IWiredAudioSink? sink = Volatile.Read(ref wiredAudio);
				SpeakerAudioSender? s = speaker;
				// While the wired endpoint plays the speaker, no Opus frame is taken from its Bluetooth sender.
				bool speech = sink == null && s != null && s.TakeFrame(opus);
				sound |= speech;
				if (sink != null)
				{
					sound |= sink.HasSpeakerAudio;
				}
				// The game's audio still arriving (even silent) counts like sound: the stream keeps running.
				bool fed = sound || Fed;
				silent = fed ? 0 : silent + 1;
				hadSound |= fed;
				bool micOn = micStreamEnabled;
				bool sendingOff = !micOn && Volatile.Read(ref micOffToSend) > 0;
				if (silent > (hadSound ? SilentReportsBeforeStop : ShortSilentReportsBeforeStop) && !micOn && !sendingOff)
				{
					lock (hapticsGate)
					{
						if (StreamWork() == null)
						{
							hapticsActive = false;
							break;
						}
					}
					// Something arrived in the last moment (as before, when the stream started again at once).
					silent = 0;
				}
				long writeStarted = Stopwatch.GetTimestamp();
				bool wrote = false;
				if (sink != null)
				{
					try
					{
						sink.WriteHaptics(left, right);
						wrote = true;
						FollowWiredSpeaker(sink);
						if (++sinkWrites == 1)
						{
							firstToUsb = true;
						}
					}
					catch (Exception e)
					{
						// This block is lost; later ticks are Bluetooth reports again. A stream whose device clock never
						// moved is said loudly: it played nothing at all.
						if (e is Audio.WiredAudioStalledException stalled)
						{
							Diagnostics.AppLog.Write("wired: USB AUDIO NOT PLAYING: " + stalled.Message + "; haptics and speaker back on Bluetooth");
							DropWiredAudio(sink, "usb audio not playing");
						}
						else
						{
							DropWiredAudio(sink, "usb audio write failed");
						}
					}
					// No Bluetooth report this tick except when the Bluetooth mic needs the stream: a 0x32 report
					// with all-zero samples (not the real frames) carries the control byte. A USB-only session has
					// no Bluetooth: a pending mic-off just runs out.
					if (usbOnly)
					{
						if (sendingOff)
						{
							Interlocked.Decrement(ref micOffToSend);
						}
					}
					else if (micOn || sendingOff)
					{
						Array.Clear(left);
						Array.Clear(right);
						report.AsSpan(BluetoothReports.HapticsSize).Clear();
						BluetoothReports.BuildHaptics(left, right, NextSequence(), counter++, HapticsBufferDepth, report);
						if (sendingOff)
						{
							Interlocked.Decrement(ref micOffToSend);
							Interlocked.Increment(ref micOffSent);
						}
						if (micOn)
						{
							MicPackets.SetStreamControl(report, true);
							int micCrc = BluetoothReports.HapticsSize - 4;
							System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(micCrc),
								Crc.Compute(Crc.OutputSeed, report.AsSpan(0, micCrc)));
						}
						try
						{
							link.Write(report);
							wrote = true;
						}
						catch (Exception)
						{
							// A lost report is a few ms of haptics; keep the stream going.
						}
					}
				}
				else if (usbOnly)
				{
					// No wired audio sink and no Bluetooth: nothing carries haptics (the host asks for the sink again).
					// The tick still counts, so the watchdog does not take a running stream for a stalled one.
					if (sendingOff)
					{
						Interlocked.Decrement(ref micOffToSend);
					}
					wrote = true;
				}
				else
				{
					if (speech)
					{
						Audio.SpeakerReports.Build(left, right, opus, s!.Headset, NextSequence(), counter++, HapticsBufferDepth, report);
						Interlocked.Increment(ref speakerReports);
					}
					else
					{
						report.AsSpan(BluetoothReports.HapticsSize).Clear();
						BluetoothReports.BuildHaptics(left, right, NextSequence(), counter++, HapticsBufferDepth, report);
					}
					if (sendingOff)
					{
						// Both builders write the off value (MicPackets.StreamMicOff) into the stream-control byte.
						Interlocked.Decrement(ref micOffToSend);
						Interlocked.Increment(ref micOffSent);
					}
					if (micOn)
					{
						MicPackets.SetStreamControl(report, true);
						int crcAt = speech ? Audio.SpeakerReports.CrcOffset : BluetoothReports.HapticsSize - 4;
						System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(crcAt),
							Crc.Compute(Crc.OutputSeed, report.AsSpan(0, crcAt)));
					}
					try
					{
						link.Write(report);
						wrote = true;
					}
					catch (Exception)
					{
						// A lost report is a few ms of haptics; keep the stream going.
					}
				}
				// One count per stream tick that wrote something (a sink write and a Bluetooth mic report in the same
				// tick count as one; a tick whose writes all failed counts nothing, as before).
				if (wrote)
				{
					Interlocked.Increment(ref hapticsReports);
				}
				timing.Wrote(writeStarted, Stopwatch.GetTimestamp(), slowTicks);
				if (reports++ == 0 && stream <= StreamLogLimit)
				{
					// After the first report, so the log's file write never delays the first hit after silence.
					Diagnostics.AppLog.Write($"bt: haptics stream: started ({reason}{(firstToUsb ? ", over USB audio" : "")})");
				}
				next += interval;
				long waitStarted = Stopwatch.GetTimestamp();
				wait.WaitUntil(next, true);
				long now = Stopwatch.GetTimestamp();
				long lateBy = timing.Woke(waitStarted, next, now, lateTicks);
				if (lateBy > ResyncAfterIntervals * interval)
				{
					next = now;
					resyncs++;
				}
			}
		}
		finally
		{
			wait.Idle();
			try
			{
				// The endpoint may stop until the stream's next block.
				Volatile.Read(ref wiredAudio)?.Idle();
			}
			catch (Exception)
			{
				// Idle never throws by contract; a broken sink must not stop the loop.
			}
			if (stopping)
			{
				lock (hapticsGate)
				{
					hapticsActive = false;
				}
			}
		}
		double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
		totals.Reports += reports;
		totals.Timing.Add(timing);
		totals.Resyncs += resyncs;
		totals.ActiveMs += ms;
		if (stream <= StreamLogLimit)
		{
			Diagnostics.AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
				$"bt: haptics stream: stopped after {ms:F0} ms ({reports} reports, {timing.Describe(Stopwatch.Frequency)}, {resyncs} resyncs, timer {PreciseWait.Name(wait)})"));
		}
		if (stream == StreamLogLimit)
		{
			Diagnostics.AppLog.Write($"bt: haptics stream: start/stop lines stop here ({StreamLogLimit} streams), totals follow when the session ends");
		}
	}

	// Why the stream should run now (a log word), or null when there is nothing to play: frames that are not silent,
	// speaker audio, the mic stream, or mic-off reports still to send. Caller holds hapticsGate.
	private string? StreamWork()
	{
		if (micStreamEnabled)
		{
			return "mic";
		}
		if (Volatile.Read(ref micOffToSend) > 0)
		{
			return "mic off";
		}
		if (Fed)
		{
			return "game audio";
		}
		if (Haptics.Audible > 0)
		{
			return "haptics";
		}
		return speaker?.Buffered > 0 || Volatile.Read(ref wiredAudio)?.HasSpeakerAudio == true ? "speaker" : null;
	}

	// A mic report while the mic is off: counted, never input; asks the controller to stop (bounded, see MicStreamEnabled).
	private void OnStrayMicReport()
	{
		Interlocked.Increment(ref strayMicReports);
		if (!StopStrayMic)
		{
			return;
		}
		long now = Stopwatch.GetTimestamp();
		long last = Interlocked.Read(ref lastStrayOff);
		if (last != long.MinValue && Stopwatch.GetElapsedTime(last, now) < StrayMicOffInterval)
		{
			return;
		}
		Interlocked.Exchange(ref lastStrayOff, now);
		if (Interlocked.Exchange(ref strayLogged, 1) == 0)
		{
			Diagnostics.AppLog.Write($"bt: mic reports arriving while the mic is off, sending mic off (at most every {StrayMicOffInterval.TotalSeconds:F0} s)");
		}
		SendMicOff(MicOffReports);
	}
}
