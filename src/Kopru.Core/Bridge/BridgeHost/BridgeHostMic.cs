// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Protocol;

namespace Kopru.Core.Bridge;

// Controller microphone: while enabled the controller's Bluetooth mic audio is asked for, decoded and played on the
// virtual pad's mic endpoint (gain 0..4); Muted feeds silence instead (the stream keeps running). While the connection
// is wired and the controller's own USB microphone could be opened, that capture is the source instead (it feeds the
// same pipeline through PushPcm and the Bluetooth mic stream is off). While the bridge runs
// with the virtual DualSense every press of the controller's mic button is reported (MicButtonPressed) and never reaches
// the game; the app decides what it does (Audio.MicButtonRules). Button: false passes the button to the game.
// Live: whether the microphone the button acts on is live (the mute LED, see SyncMuteLed); null = Enabled and not Muted.
public sealed record MicSettings(bool Enabled = false, double Gain = 1, bool NoiseGate = true, bool Muted = false, bool Button = true)
{
	public static MicSettings Off { get; } = new();

	public bool? Live { get; init; }

	public bool IsLive => Live ?? (Enabled && !Muted);
}

public sealed partial class BridgeHost
{
	// What feeds a connection's mic pipeline: nothing while the mic is off, else the Bluetooth mic stream or the
	// controller's own USB audio capture while wired.
	private enum MicSourceKind
	{
		None,
		Bluetooth,
		Usb
	}

	private MicSettings micSettings = MicSettings.Off;

	private int micButtonHooked;

	public MicSettings Mic => micSettings;

	// The controller's mic button (see MicSettings). Created with the host's clock (tests drive the debounce).
	public MicButton MicButton => micButton ??= new MicButton(time);

	private MicButton? micButton;

	// A press of the mic button while the bridge handles it. Raised on the input thread.
	public event Action? MicButtonPressed;

	// Tests replace this.
	public Func<IMicDecoder> MicDecoderFactory { get; set; } = () => new ConcentusMicDecoder();

	// Optional replacement for the controller's audio (e.g. a test tone); applied on the next ApplyMic / connect.
	public IMicSource? MicTestSource { get; set; }

	// Mic level 0..1 after gain, from the controller's packets whether or not anything records (0 while off, not running
	// or no packets for MicPipeline.LevelTimeout).
	public double MicLevel => current?.Mic?.Level ?? 0;

	// The running connection's mic pipeline (diagnostics and tests).
	public MicPipeline? MicPipeline => current?.Mic;

	// The running connection's virtual DualSense, or null (diagnostics; tests feed it a game's output).
	public Virtual.VirtualDualSense? VirtualDualSense => current?.Pad;

	public void ApplyMic(MicSettings settings)
	{
		Connection? c = current;
		Diagnostics.AppLog.Write(string.Create(CultureInfo.InvariantCulture,
			$"mic apply{LogSlot}: {(settings.Enabled ? "on" : "off")} gain {settings.Gain:F2} gate {settings.NoiseGate} muted {settings.Muted} live {settings.IsLive} ({(c != null ? "running" : "not running, kept for the next connect")})"));
		if (Interlocked.Exchange(ref micButtonHooked, 1) == 0)
		{
			MicButton.Pressed += OnMicButtonPressed;
		}
		micSettings = settings;
		if (c != null)
		{
			bool ok = ApplyMicTo(c);
			if (ok)
			{
				Diagnostics.AppLog.Write($"mic apply{LogSlot}: done");
			}
		}
	}

	// Logs what came back once MicCheckDelay after the mic was asked for: mic reports, gamepad inputs and rejected
	// reports in that time (the first hardware answer to "does the controller send its mic, and is input still fine").
	public static readonly TimeSpan MicCheckDelay = TimeSpan.FromSeconds(3);

	private void LogMicCheck(Connection c, DateTimeOffset now)
	{
		if (c.MicCheckAt is not { } at || now < at)
		{
			return;
		}
		c.MicCheckAt = null;
		BluetoothSession s = c.Session;
		Diagnostics.AppLog.Write(string.Create(CultureInfo.InvariantCulture,
			$"mic check{LogSlot}: {MicCheckDelay.TotalSeconds:F0} s after mic on: {s.MicReports - c.MicCheckMic} mic reports, {s.InputReports - c.MicCheckInputs} inputs, {s.RejectedReports - c.MicCheckRejected} rejected, decoded {c.Mic?.Packets ?? 0}, stream running {s.IsHapticsStreaming}"));
	}

	// How often the running mic writes its summary line (packets, loss, underruns, buffer depth); also written when the
	// mic goes off and when the connection closes.
	public static readonly TimeSpan MicStatsInterval = TimeSpan.FromSeconds(60);

	private void LogMicStats(Connection c, DateTimeOffset now)
	{
		if (c.Mic is not { } p || c.MicSource == MicSourceKind.None)
		{
			c.MicStatsAt = null;
			return;
		}
		c.MicStatsAt ??= now + MicStatsInterval;
		if (now >= c.MicStatsAt)
		{
			c.MicStatsAt = now + MicStatsInterval;
			WriteMicStats(p, $"last {MicStatsInterval.TotalSeconds:F0} s");
		}
	}

	// One summary line of what the mic pipeline did since the last one (nothing when no packet came and nothing failed).
	private void WriteMicStats(MicPipeline p, string what)
	{
		if (p.HasNews)
		{
			Diagnostics.AppLog.Write($"mic stats{LogSlot} ({what}): {p.TakeSummary()}");
		}
	}

	// The mute LED (the orange light of the mic button) always shows the microphone the button acts on: lit while it is
	// live (MicSettings.IsLive: the controller microphone on and not muted, or the selected PC microphone not muted), dark
	// otherwise. It is not part of the light settings, so no light effect, profile or game light can hide it. It is written
	// on its own (not through the LightSender, which pauses while a game drives the light) when the value changes and on
	// every connect or pad swap, and every relayed game output carries it (ControllerBridge.AppMuteLed). Only when the
	// game owns the mic button (Button off on the virtual DualSense) does the game's own mute LED go through.
	private void SyncMuteLed(Connection c, string reason)
	{
		MicSettings s = micSettings;
		bool appOwnsButton = s.Button || c.PadType != Virtual.VirtualPadType.DualSense;
		if (!appOwnsButton)
		{
			if (c.MuteLedSent != ControllerBridge.NoAppMuteLed)
			{
				LogMuteLedOverrides(c);
			}
			c.Bridge.AppMuteLed = ControllerBridge.NoAppMuteLed;
			if (c.MuteLedSent != ControllerBridge.NoAppMuteLed)
			{
				c.MuteLedSent = ControllerBridge.NoAppMuteLed;
				Diagnostics.AppLog.Write($"mute led{LogSlot}: left to the game ({reason})");
			}
			return;
		}
		byte value = s.IsLive ? OutputBlock.MuteLedOn : OutputBlock.MuteLedOff;
		if (c.MuteLedSent == value)
		{
			c.Bridge.AppMuteLed = value;
			return;
		}
		// What the game asked for while the previous value showed (one line per kind, once per change).
		LogMuteLedOverrides(c);
		// Set before the write: game reports relayed from now on carry the new value (ControllerBridge reads it under the
		// session's output lock, so none built earlier can be sent after this write).
		c.Bridge.AppMuteLed = value;
		try
		{
			c.Session.UpdateOutput(b =>
			{
				OutputBlock.SetMuteLed(b, value);
				// The output state is the game's last report while a game drives the controller: a light bar setup or
				// "release LEDs" left in it would be repeated in this report and undo the value (DualSense Mode, game light).
				ControllerBridge.ClearOneShotLedCommands(b);
			});
		}
		catch (Exception e)
		{
			// The controller is going away; sent again on the next connect.
			Diagnostics.AppLog.Write($"mute led{LogSlot}: not sent: {e.GetType().Name}");
			return;
		}
		c.MuteLedSent = value;
		Diagnostics.AppLog.Write($"mute led{LogSlot}: {ControllerBridge.MuteLedName(value)} ({reason}: {MuteLedReason(s)}; {c.Bridge.MuteLedDelivery})");
	}

	private void LogMuteLedOverrides(Connection c)
	{
		foreach (string line in c.Bridge.TakeMuteLedOverrides())
		{
			Diagnostics.AppLog.Write($"mute led{LogSlot}: {line}");
		}
	}

	private static string MuteLedReason(MicSettings s) => s.Live is { } live
		? live ? "PC microphone live" : "PC microphone muted"
		: !s.Enabled ? "controller microphone off" : s.Muted ? "controller microphone muted" : "controller microphone live";

	// False when the controller refused the change (logged).
	private bool ApplyMicTo(Connection c)
	{
		lock (audioGate)
		{
			return ApplyMicLocked(c);
		}
	}

	private bool ApplyMicLocked(Connection c)
	{
		MicSettings s = micSettings;
		try
		{
			// While wired the controller's own USB microphone is the source when it could be opened (it is asked
			// for again every WiredAudioRetryInterval, slower after many misses; a failed open keeps the Bluetooth
			// mic meanwhile).
			MicSourceKind wanted = !s.Enabled ? MicSourceKind.None
				: c.Session.IsWired && EnsureWiredMic(c) ? MicSourceKind.Usb
				: MicSourceKind.Bluetooth;
			if (wanted == MicSourceKind.Bluetooth && c.MicSource != MicSourceKind.Bluetooth)
			{
				c.MicCheckAt = time.GetUtcNow() + MicCheckDelay;
				c.MicCheckMic = c.Session.MicReports;
				c.MicCheckInputs = c.Session.InputReports;
				c.MicCheckRejected = c.Session.RejectedReports;
			}
			MicPipeline? p = c.Mic;
			// The button is the switch only on the virtual DualSense (the other pad types get it as before).
			c.Bridge.MicButton = s.Button && c.PadType == Virtual.VirtualPadType.DualSense ? MicButton : null;
			SyncMuteLed(c, "mic settings");
			if (!s.Enabled)
			{
				if (c.Pad is { } off)
				{
					off.Mic = null;
				}
				c.Session.MicStreamEnabled = false;
				if (p != null)
				{
					c.Session.MicReportReceived -= p.PushReport;
					WriteMicStats(p, "mic off");
				}
				c.MicCheckAt = null;
				c.MicStatsAt = null;
				CloseWiredMic(c);
				c.MicSource = MicSourceKind.None;
				return true;
			}
			if (p == null)
			{
				p = new MicPipeline(MicDecoderFactory(), time: time);
				c.Mic = p;
			}
			else if (c.MicSource != wanted)
			{
				// Back on after off or another source: no loss or underrun counted for the pause.
				p.Restart();
			}
			p.Gain = s.Gain;
			p.NoiseGate = s.NoiseGate;
			p.Muted = s.Muted;
			p.Source = MicTestSource;
			if (wanted == MicSourceKind.Usb)
			{
				// The controller is told "mic off" over Bluetooth; its USB capture feeds the pipeline instead.
				c.Session.MicReportReceived -= p.PushReport;
				c.Session.MicStreamEnabled = false;
			}
			else
			{
				CloseWiredMic(c);
				c.Session.MicReportReceived -= p.PushReport;
				c.Session.MicReportReceived += p.PushReport;
				c.Session.MicStreamEnabled = true;
			}
			if (c.Pad is { } pad)
			{
				pad.Mic = p;
			}
			if (c.MicSource != wanted)
			{
				Diagnostics.AppLog.Write($"mic{LogSlot}: source {(wanted == MicSourceKind.Usb ? "USB audio" : "Bluetooth")}");
			}
			c.MicSource = wanted;
			return true;
		}
		catch (Exception e)
		{
			// The controller is going away; applied again on the next connect.
			Diagnostics.AppLog.Write($"mic apply{LogSlot}: failed: {e.GetType().Name}: {e.Message}");
			return false;
		}
	}

	// A transport change (wired entered, left or lost): the USB microphone of the old link is closed, an open may be
	// tried again, and the mic is applied for the new link. One step under audioGate.
	private bool ReapplyMicForTransport(Connection c)
	{
		lock (audioGate)
		{
			Volatile.Write(ref c.WiredMicRetryAt, 0);
			Volatile.Write(ref c.WiredMicMiss, null);
			Volatile.Write(ref c.WiredMicTries, 0);
			CloseWiredMic(c);
			return ApplyMicLocked(c);
		}
	}

	// The USB microphone asked for again by the monitor's wired retry task (Core Audio calls can be slow: never
	// called from the monitor thread itself): opens the capture and switches the mic to it when it is still this
	// connection's mic.
	private void RetryWiredMic(Connection c)
	{
		lock (audioGate)
		{
			if (ReferenceEquals(current, c) && c.MicSource == MicSourceKind.Bluetooth && c.Session.IsWired && EnsureWiredMic(c))
			{
				ApplyMicLocked(c);
			}
		}
	}

	// A USB microphone capture that gave up by itself (IWiredMicCapture.Stopped) is dropped by the monitor's wired
	// retry task: the Bluetooth mic takes over now, and the USB one is asked for again after the retry interval.
	private void DropStoppedWiredMic(Connection c)
	{
		lock (audioGate)
		{
			if (!ReferenceEquals(current, c) || c.WiredMic is not Audio.IWiredMicCapture { Stopped: true })
			{
				return;
			}
			CloseWiredMic(c);
			Volatile.Write(ref c.WiredMicRetryAt, WiredAudioRetryDue());
			Volatile.Write(ref c.WiredMicMiss, null);
			Volatile.Write(ref c.WiredMicTries, 0);
			Diagnostics.AppLog.Write($"wired{LogSlot}: USB microphone stopped by itself; the microphone is back on Bluetooth{WiredAudioRetryNote()}");
			ApplyMicLocked(c);
		}
	}

	// The controller's USB microphone capture, opened lazily while wired: once per wired enter, and a missing or
	// failed open is asked for again every WiredAudioRetryInterval, slower after WiredAudioBackoffTries misses (the
	// reason is logged once while it stays).
	private bool EnsureWiredMic(Connection c)
	{
		if (c.WiredMic != null)
		{
			return true;
		}
		long now = time.GetUtcNow().UtcTicks;
		if (now < Volatile.Read(ref c.WiredMicRetryAt))
		{
			return false;
		}
		string miss;
		try
		{
			c.WiredMic = OpenWiredMic(c.Session.WiredDevice!, samples => c.Mic?.PushPcm(samples));
			miss = "no USB microphone found for this controller";
		}
		catch (Exception e)
		{
			miss = "USB microphone not opened (" + Audio.WiredAudio.Describe(e) + ")";
		}
		if (c.WiredMic != null)
		{
			if (Volatile.Read(ref c.WiredMicTries) > 0)
			{
				Diagnostics.AppLog.Write($"wired{LogSlot}: USB microphone opened on try {Volatile.Read(ref c.WiredMicTries) + 1}");
			}
			Volatile.Write(ref c.WiredMicMiss, null);
			Volatile.Write(ref c.WiredMicTries, 0);
			return true;
		}
		int tries = Volatile.Read(ref c.WiredMicTries) + 1;
		Volatile.Write(ref c.WiredMicTries, tries);
		Volatile.Write(ref c.WiredMicRetryAt, WiredAudioRetryDue(tries));
		if (tries == WiredAudioBackoffTries)
		{
			Diagnostics.AppLog.Write(string.Create(CultureInfo.InvariantCulture,
				$"wired{LogSlot}: USB microphone still missing after {WiredAudioBackoffTries} tries; trying every {WiredAudioSlowRetryInterval.TotalSeconds:0.#} s from now"));
		}
		if (miss != Volatile.Read(ref c.WiredMicMiss))
		{
			Volatile.Write(ref c.WiredMicMiss, miss);
			Diagnostics.AppLog.Write($"wired{LogSlot}: {miss} (the microphone stays on Bluetooth{WiredAudioRetryNote()})");
		}
		return false;
	}

	private void CloseWiredMic(Connection c)
	{
		try
		{
			c.WiredMic?.Dispose();
		}
		catch (Exception)
		{
			// The capture is going away either way.
		}
		c.WiredMic = null;
	}

	private sealed partial class Connection
	{
		// The mute LED value last written to this controller (ControllerBridge.NoAppMuteLed: none yet / the game's).
		public int MuteLedSent = ControllerBridge.NoAppMuteLed;

		// When the next mic summary line is due (null: the mic is not running).
		public DateTimeOffset? MicStatsAt;

		// Which source feeds Mic today (None while the mic is off; a wired connection can switch between the two).
		public MicSourceKind MicSource;

		// The USB microphone capture while MicSource is Usb (opened lazily by EnsureWiredMic).
		public IDisposable? WiredMic;

		// When the next open of a missing USB microphone is due (GetUtcNow ticks).
		public long WiredMicRetryAt;

		// Why the last USB microphone open gave nothing: the line is logged once per wired enter while the reason stays.
		public string? WiredMicMiss;

		// USB microphone opens in a row that gave nothing.
		public int WiredMicTries;
	}

	// Input thread: the app decides what the press does (Core Audio calls it may need are slow: never here).
	private void OnMicButtonPressed()
	{
		Diagnostics.AppLog.Write($"mic button{LogSlot}: pressed");
		MicButtonPressed?.Invoke();
	}
}
