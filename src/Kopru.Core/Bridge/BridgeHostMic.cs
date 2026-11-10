// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Lighting;
using Kopru.Core.Protocol;

namespace Kopru.Core.Bridge;

// Controller microphone: while enabled the controller's Bluetooth mic audio is asked for, decoded and played on the
// virtual pad's mic endpoint (gain 0..4). The controller's mic button is the switch (Button: it may switch; Low Power
// turns that off): while the bridge runs with the virtual DualSense every press asks the app to turn the feature on or off
// (MicButtonPressed) and the button never reaches the game. The mute LED in FollowMic mode is lit while the mic is on.
// MuteWindows: a press that turns the mic off also mutes the default Windows capture device, one that turns it on unmutes it.
public sealed record MicSettings(bool Enabled = false, double Gain = 1, bool NoiseGate = true, bool MuteWindows = false, bool Button = true)
{
	public static MicSettings Off { get; } = new();
}

public sealed partial class BridgeHost
{
	private MicSettings micSettings = MicSettings.Off;

	private int micButtonHooked;

	public MicSettings Mic => micSettings;

	// The controller's mic button (see MicSettings). Created with the host's clock (tests drive the debounce).
	public MicButton MicButton => micButton ??= new MicButton(time);

	private MicButton? micButton;

	// A press of the mic button while the bridge handles it; true = the press turns the mic on. Raised on the input thread.
	public event Action<bool>? MicButtonPressed;

	// Tests replace these.
	public IWindowsMicMute WindowsMicMute { get; set; } = new CoreAudioMicMute();

	public Func<IMicDecoder> MicDecoderFactory { get; set; } = () => new ConcentusMicDecoder();

	// Optional replacement for the controller's audio (e.g. a test tone); applied on the next ApplyMic / connect.
	public IMicSource? MicTestSource { get; set; }

	// Mic level 0..1 after gain (0 while off or not running).
	public double MicLevel => current?.Mic?.Level ?? 0;

	// The running connection's mic pipeline (diagnostics and tests).
	public MicPipeline? MicPipeline => current?.Mic;

	public void ApplyMic(MicSettings settings)
	{
		Connection? c = current;
		Diagnostics.AppLog.Write(string.Create(CultureInfo.InvariantCulture,
			$"mic apply{LogSlot}: {(settings.Enabled ? "on" : "off")} gain {settings.Gain:F2} gate {settings.NoiseGate} windows mute {settings.MuteWindows} ({(c != null ? "running" : "not running, kept for the next connect")})"));
		if (Interlocked.Exchange(ref micButtonHooked, 1) == 0)
		{
			MicButton.Pressed += OnMicButtonPressed;
		}
		micSettings = settings;
		if (c != null)
		{
			bool ok = ApplyMicTo(c);
			if (!InvalidateLightSender())
			{
				ApplyLightTo(c.Session);
			}
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

	// FollowMic resolved to on/off: lit while the mic feature is on.
	private LightSettings EffectiveLight()
	{
		LightSettings l = light;
		if (l.Options.MuteLed != MuteLedMode.FollowMic)
		{
			return l;
		}
		bool on = micSettings.Enabled;
		return l with { Options = l.Options with { MuteLed = on ? MuteLedMode.On : MuteLedMode.Off } };
	}

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
			c.Bridge.KeepMuteLed = light.Options.MuteLed == MuteLedMode.FollowMic;
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

	// Input thread: the app decides (it flips the same setting as the tile); Windows' mic follows a press at once when
	// asked to (off the input thread: Core Audio calls can be slow).
	private void OnMicButtonPressed()
	{
		MicSettings s = micSettings;
		bool turnOn = !s.Enabled;
		Diagnostics.AppLog.Write($"mic button{LogSlot}: turning the mic {(turnOn ? "on" : "off")}");
		if (s.MuteWindows)
		{
			_ = Task.Run(() => SetWindowsMute(!turnOn));
		}
		MicButtonPressed?.Invoke(turnOn);
	}
	private void SetWindowsMute(bool muted)
	{
		try
		{
			WindowsMicMute.SetMuted(muted);
		}
		catch (Exception)
		{
			// No capture device, or Windows refused: our own mute still works.
		}
	}
}
