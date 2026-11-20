// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Audio;
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// EXPERIMENTAL controller speaker: the game's speaker channels to the controller (speaker, or the headphone jack when
// something is plugged in). Off by default. Volumes 0..1.
public sealed record SpeakerSettings(bool Enabled = false, double SpeakerVolume = 0.8, double HeadphoneVolume = 0.6)
{
	public static SpeakerSettings Off { get; } = new();
}

public sealed partial class BridgeHost
{
	private SpeakerSettings speakerSettings = SpeakerSettings.Off;

	public SpeakerSettings Speaker => speakerSettings;

	// Headphones in the running controller's jack (false while the bridge is not running).
	public bool HeadphonesPlugged => current?.Session.HeadphonesPlugged ?? false;

	// Makes the encoder for a new speaker sender (tests replace it).
	public Func<IOpusEncoder> SpeakerEncoderFactory { get; set; } = OpusEncoders.Create;

	// Remembers the speaker settings (applied on every connect) and applies them now when the bridge runs.
	public void ApplySpeaker(SpeakerSettings settings)
	{
		Connection? c = current;
		Diagnostics.AppLog.Write(string.Create(System.Globalization.CultureInfo.InvariantCulture,
			$"speaker apply{LogSlot}: {(settings.Enabled ? "on" : "off")} volume {settings.SpeakerVolume:F2} headphones {settings.HeadphoneVolume:F2} ({(c != null ? "running" : "not running, kept for the next connect")})"));
		speakerSettings = settings;
		if (c != null && ApplySpeakerTo(c))
		{
			Diagnostics.AppLog.Write($"speaker apply{LogSlot}: done");
		}
	}

	// One speaker / mic change at a time per host: the connect path, a pad swap and ApplySpeaker / ApplyMic from other
	// threads would otherwise race (two senders or two mic pipelines for one connection). Each reads the latest settings.
	private readonly object audioGate = new();

	// False when the controller refused the change (logged).
	private bool ApplySpeakerTo(Connection c)
	{
		lock (audioGate)
		{
			return ApplySpeakerLocked(c);
		}
	}

	private bool ApplySpeakerLocked(Connection c)
	{
		SpeakerSettings s = speakerSettings;
		try
		{
			SpeakerAudioSender? sender = c.Session.Speaker;
			// The speaker channels only exist on a virtual DualSense.
			if (!s.Enabled || c.PadType != VirtualPadType.DualSense)
			{
				c.Bridge.SpeakerEnabled = false;
				if (sender != null)
				{
					c.Session.Speaker = null;
				}
				return true;
			}
			if (sender == null)
			{
				sender = new SpeakerAudioSender(SpeakerEncoderFactory()) { Volume = s.SpeakerVolume, HeadphoneVolume = s.HeadphoneVolume };
				lock (c.SpeakerSenders)
				{
					c.SpeakerSenders.Add(sender);
				}
				c.Session.Speaker = sender;
			}
			else
			{
				sender.Volume = s.SpeakerVolume;
				sender.HeadphoneVolume = s.HeadphoneVolume;
				c.Session.ApplySpeakerRoute();
			}
			c.Bridge.SpeakerEnabled = true;
			return true;
		}
		catch (Exception e)
		{
			// The controller is going away; the settings are applied again on the next connect.
			Diagnostics.AppLog.Write($"speaker apply{LogSlot}: failed: {e.GetType().Name}: {e.Message}");
			return false;
		}
	}
}
