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
// It can also carry input and output over a second, wired (USB) link of the same controller (AttachWired): report 0x01
// in, 0x02 out, while Bluetooth stays connected for the mic and (without a wired audio sink) the haptics stream and
// the speaker. With an IWiredAudioSink attached, the haptics stream's blocks go to the controller's own USB audio
// endpoint (channels 3-4) and the game's speaker audio with them (channels 1-2); the mic keeps riding Bluetooth.
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

	// The successful sink write of a stream run the output state is also resent over USB on (the first one always is).
	private const int WiredOutputResendBlock = 16;

	// The diagnostic line the next successful wired UpdateOutput write should log (0 none, 1 "first report",
	// 2 "haptics playing"), and whether the stream already armed it once since the attach.
	private int wiredOutputLogDue;
	private int wiredHapticsLogged;

	private int wiredOutputLogs;

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

	public BluetoothSession(IHidLink link)
	{
		if (link.Device.Transport != Transport.Bluetooth)
		{
			throw new ArgumentException("Not a Bluetooth controller", nameof(link));
		}
		this.link = link;
	}

	public HidDevice Device => link.Device;

	public HapticsMixer Haptics { get; } = new();

	// Report 0x31 keeps the actuators free for the haptics stream (no rumble bits, power controls at 0).
	public bool HapticsMode { get; set; } = true;

	// How much the controller buffers before playing: 0x20 is about 50 ms, 0x08 about 25 ms.
	public byte HapticsBufferDepth { get; set; } = BluetoothReports.DefaultBufferDepth;

	public InputState LastInput { get; private set; }

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
		SpeakerAudioSender? s = speaker;
		UpdateOutput(b =>
		{
			if (s == null)
			{
				AudioOutput.Clear(b);
			}
			else
			{
				AudioOutput.SetRoute(b, s.Headset, s.OutputVolume);
			}
		});
	}

	// Puts the speaker route and volume into an output block about to be sent (e.g. the game's own output, which would
	// otherwise overwrite them); no change while Speaker is off.
	public void WriteSpeakerRoute(Span<byte> block)
	{
		SpeakerAudioSender? s = speaker;
		if (s != null)
		{
			AudioOutput.SetRoute(block, s.Headset, s.OutputVolume);
		}
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

	// Raised (on the wired reader or the output writer) after the session fell back to Bluetooth by itself; the
	// argument is a short fixed reason.
	public event Action<string>? WiredLost;

	// Raised (on the haptics thread) when the wired audio sink failed and haptics/speaker moved back to Bluetooth.
	public event Action<string>? WiredAudioLost;

	// Consecutive failed USB reads (10 ms apart) before the session falls back to Bluetooth.
	public int WiredLostAfterFailures { get; set; } = 3;

	// A wired link that never delivered input or went silent for this long counts as dead on the next Bluetooth
	// report: the session falls back to Bluetooth then. Far beyond the ~4 ms report interval, short enough to feel
	// instant.
	public TimeSpan WiredSilentTimeout { get; set; } = TimeSpan.FromMilliseconds(150);

	// Diagnostic lines written about the USB output report since the last AttachWired (tests read it).
	public int WiredOutputLogs => Volatile.Read(ref wiredOutputLogs);

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
		reader = new Thread(ReadLoop) { Name = "Controller input", IsBackground = true, Priority = ThreadPriority.AboveNormal };
		reader.Start();
	}

	// The light as last written to the controller (whoever set it); read-only, any thread.
	public Lighting.LightOutput SentLight => Volatile.Read(ref sentLight);

	private Lighting.LightOutput sentLight = Lighting.LightOutput.None;

	// Changes the output state and sends it at once. While wired the update goes over the USB link instead and
	// nothing is written to Bluetooth; a failed USB write falls the session back and this update lands on Bluetooth.
	public void UpdateOutput(Action<byte[]> change)
	{
		string? lost = null;
		string? usbLog = null;
		lock (outputGate)
		{
			change(block);
			byte[] sent = (byte[])block.Clone();
			Volatile.Write(ref sentLight, sentLight.After(sent));
			bool sentOverUsb = false;
			if (Volatile.Read(ref wired) is { } usb)
			{
				// The same haptics-mode controls as over Bluetooth (haptics un-muted, no power save, full motor
				// power). With only the rumble bits cleared, USB audio haptics were silent on hardware while the
				// speaker, whose route flags were sent, played: the likely cause, not yet confirmed on hardware.
				if (HapticsMode)
				{
					OutputBlock.SetHapticsMode(sent);
				}
				byte[] report = new byte[Math.Max(usb.Device.OutputLength, UsbReports.OutputSize)];
				UsbReports.BuildOutput(sent, report);
				try
				{
					usb.Write(report);
					int why = Interlocked.Exchange(ref wiredOutputLogDue, 0);
					if (why != 0)
					{
						usbLog = string.Create(System.Globalization.CultureInfo.InvariantCulture,
							$"wired: usb output 0x02 ({(why == 1 ? "first report" : "haptics playing")}):" +
							$" flags 0x{sent[OutputBlock.Flag0]:X2} 0x{sent[OutputBlock.Flag1]:X2} 0x{sent[OutputBlock.Flag2]:X2}," +
							$" mute/power save 0x{sent[OutputBlock.PowerSave]:X2}, motor power 0x{sent[OutputBlock.MotorPowerReduction]:X2}," +
							$" audio control 0x{sent[AudioOutput.AudioControl]:X2} 0x{sent[AudioOutput.AudioControl2]:X2}," +
							$" volumes headphone {sent[AudioOutput.HeadphoneVolume]} speaker {sent[AudioOutput.SpeakerVolume]}," +
							$" low pass 0x{sent[39]:X2}");
					}
					sentOverUsb = true;
				}
				catch (Exception)
				{
					if (FallBack(usb, "usb write failed"))
					{
						lost = "usb write failed";
					}
					// Falls through: this update still goes out, over Bluetooth.
				}
			}
			if (!sentOverUsb)
			{
				if (HapticsMode)
				{
					OutputBlock.SetHapticsMode(sent);
				}
				byte[] btReport = new byte[Math.Max(link.Device.OutputLength, BluetoothReports.OutputSize)];
				BluetoothReports.BuildOutput(sent, NextSequence(), btReport);
				link.Write(btReport);
			}
		}
		// Outside the gate: a subscriber may call back into the session.
		if (lost != null)
		{
			WiredLost?.Invoke(lost);
		}
		if (usbLog != null)
		{
			Interlocked.Increment(ref wiredOutputLogs);
			Diagnostics.AppLog.Write(usbLog);
		}
	}

	// Sends the current output state over USB again (no state change): the controller gets the haptics-mode flags
	// again once its USB audio endpoint is playing — right at the first block and once more ~170 ms later (the
	// WiredOutputResendBlock-th write of a run) when the endpoint is surely streaming.
	private void ResendWiredOutput()
	{
		if (Volatile.Read(ref wired) == null)
		{
			return;
		}
		try
		{
			UpdateOutput(_ => { });
		}
		catch (Exception)
		{
			// The link is going away; the next output fixes the state.
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

	// Attaches the controller's own USB link: input and output move over the cable while Bluetooth stays connected
	// (the mic keeps using it). audio: its USB audio endpoint (WiredAudio.OpenDefault), which then carries the haptics
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
		}
		wiredReader = new Thread(() => WiredReadLoop(usb)) { Name = "Controller input (USB)", IsBackground = true, Priority = ThreadPriority.AboveNormal };
		wiredReader.Start();
		Diagnostics.AppLog.Write("wired: input and output over USB, Bluetooth stays connected");
		Volatile.Write(ref wiredOutputLogs, 0);
		Volatile.Write(ref wiredHapticsLogged, 0);
		Volatile.Write(ref wiredOutputLogDue, 1);
		try
		{
			UpdateOutput(_ => { });
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

	// Detaches the wired link (the cable went out, the mode changed): back to Bluetooth over everything, ending by
	// sending the current state over it. False when no wired link was attached.
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
		try
		{
			UpdateOutput(_ => { });
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
		WiredAudioLost?.Invoke(reason);
	}

	// The session falls back to Bluetooth by itself (a failed USB read or write) when usb is still the wired link.
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
		link.CancelRead();
		reader?.Join(1000);
		Thread? haptics;
		lock (hapticsGate)
		{
			haptics = hapticsThread;
			Monitor.PulseAll(hapticsGate);
		}
		haptics?.Join(1000);
		link.Dispose();
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
			string? silent = null;
			lock (inputGate)
			{
				// While wired the USB link carries input: a valid Bluetooth report still arriving is counted and
				// dropped — unless the link never started or went silent, which falls the session back to
				// Bluetooth (this report is published), or is up but has not delivered its first report yet
				// (Bluetooth keeps carrying input until then).
				if (Volatile.Read(ref wired) is not { } usb)
				{
					Publish(body, readTicks);
					continue;
				}
				if (Stopwatch.GetElapsedTime(lastWiredInput) > WiredSilentTimeout)
				{
					if (FallBack(usb, "usb silent"))
					{
						silent = "usb silent";
					}
					Publish(body, readTicks);
				}
				else if (!wiredInputLive)
				{
					Publish(body, readTicks);
				}
				else
				{
					Interlocked.Increment(ref ignoredBluetoothInputs);
				}
			}
			if (silent == null)
			{
				continue;
			}
			try
			{
				// Back on Bluetooth: the output state is sent over it again (outside any lock).
				UpdateOutput(_ => { });
			}
			catch (Exception)
			{
				// The next output fixes the state.
			}
			WiredLost?.Invoke(silent);
		}
	}

	// The wired reader: report 0x01 carries the same input body as Bluetooth's 0x31 (no CRC). Runs while this link is
	// the wired one (a detach cancels its read); enough failed reads in a row fall the session back to Bluetooth.
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
						try
						{
							// Back on Bluetooth: the output state is sent over it again (outside any lock).
							UpdateOutput(_ => { });
						}
						catch (Exception)
						{
							// The next output fixes the state.
						}
						WiredLost?.Invoke("usb reads failed");
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
					Publish(body, now);
				}
			}
		}
	}

	// The tail of a good input report on either link: state, headphone tracking and the events. inputGate held, so
	// LastReadTicks and LastInput can never be set by the two reader threads at once.
	private void Publish(ReadOnlySpan<byte> body, long readTicks)
	{
		LastReadTicks = readTicks;
		LastInput = InputState.Parse(body);
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
						if (++sinkWrites == 1 || sinkWrites == WiredOutputResendBlock)
						{
							if (sinkWrites == 1)
							{
								firstToUsb = true;
								if (Interlocked.Exchange(ref wiredHapticsLogged, 1) == 0)
								{
									Volatile.Write(ref wiredOutputLogDue, 2);
								}
							}
							ResendWiredOutput();
						}
					}
					catch (Exception)
					{
						// This block is lost; later ticks are Bluetooth reports again.
						DropWiredAudio(sink, "usb audio write failed");
					}
					// No Bluetooth report this tick except when the Bluetooth mic needs the stream: a 0x32 report
					// with all-zero samples (not the real frames) carries the control byte.
					if (micOn || sendingOff)
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
