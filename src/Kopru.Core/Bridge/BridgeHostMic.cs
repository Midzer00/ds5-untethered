// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Protocol;

namespace Kopru.Core.Bridge;

// Controller microphone: while enabled the controller's Bluetooth mic audio is asked for, decoded and played on the
// virtual pad's mic endpoint (gain 0..4); Muted feeds silence instead (the stream keeps running). While the bridge runs
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

	// Mic level 0..1 after gain (0 while off or not running).
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
			if (s.Enabled && !c.Session.MicStreamEnabled)
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
				}
				c.MicCheckAt = null;
				return true;
			}
			if (p == null)
			{
				p = new MicPipeline(MicDecoderFactory());
				c.Mic = p;
			}
			p.Gain = s.Gain;
			p.NoiseGate = s.NoiseGate;
			p.Muted = s.Muted;
			p.Source = MicTestSource;
			c.Session.MicReportReceived -= p.PushReport;
			c.Session.MicReportReceived += p.PushReport;
			if (c.Pad is { } pad)
			{
				pad.Mic = p;
			}
			c.Session.MicStreamEnabled = true;
			return true;
		}
		catch (Exception e)
		{
			// The controller is going away; applied again on the next connect.
			Diagnostics.AppLog.Write($"mic apply{LogSlot}: failed: {e.GetType().Name}: {e.Message}");
			return false;
		}
	}

	private sealed partial class Connection
	{
		// The mute LED value last written to this controller (ControllerBridge.NoAppMuteLed: none yet / the game's).
		public int MuteLedSent = ControllerBridge.NoAppMuteLed;
	}

	// Input thread: the app decides what the press does (Core Audio calls it may need are slow: never here).
	private void OnMicButtonPressed()
	{
		Diagnostics.AppLog.Write($"mic button{LogSlot}: pressed");
		MicButtonPressed?.Invoke();
	}
}
