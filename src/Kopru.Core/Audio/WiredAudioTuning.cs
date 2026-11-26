// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Core.Audio;

// How the wired render stream makes up for the endpoint's queue drifting from its target.
public enum WiredSteering
{
	// The default: the block's 3 kHz haptics are interpolated onto a few frames more or fewer (a 0.8 % rate change at
	// most), so the waveform stays continuous.
	Smooth,

	// Fix 7 and earlier: the built block's last frames are dropped or its last frame repeated, a small step or flat
	// spot in the waveform on every nudged block (kept for A/B tests on hardware).
	Legacy
}

// Diagnostic switches of the wired (USB audio) haptics stream, read once from environment variables so the owner can
// A/B them on hardware without a settings page or a rebuild (docs/internal/tasks/usb-haptics/REPORT.md "Fix 8 test").
// Default: smooth steering and a 2 ms fade-in after a gap; everything else as before fix 8.
public sealed record WiredAudioTuning
{
	public const string SteerVariable = "KOPRU_WIRED_STEER";
	public const string FadeVariable = "KOPRU_WIRED_FADE_MS";
	public const string GainVariable = "KOPRU_WIRED_GAIN";
	public const string LimitVariable = "KOPRU_WIRED_LIMIT";
	public const string BufferVariable = "KOPRU_WIRED_BUFFER_MS";
	public const string SpeakerIdleVariable = "KOPRU_WIRED_SPEAKER_IDLE";
	public const string EventLogVariable = "KOPRU_WIRED_AUDIO_LOG";

	public static readonly string[] Variables =
		[SteerVariable, FadeVariable, GainVariable, LimitVariable, BufferVariable, SpeakerIdleVariable, EventLogVariable];

	// The fade-in after a gap (the endpoint ran dry and a silence cushion was queued) by default: 2 ms.
	public const int DefaultGapFadeFrames = WiredAudioFrames.Rate / 500;

	// The queue aimed at by default (20 ms) and the range KOPRU_WIRED_BUFFER_MS accepts: the most queued is the target
	// plus 40 ms, which must stay below the 100 ms endpoint buffer with a block on top.
	public const int DefaultTargetFrames = 960;
	public const int MinBufferMs = 10;
	public const int MaxBufferMs = 40;
	public const int MaxFadeMs = 20;

	// Queued frames above the target before a whole block is dropped (40 ms).
	public const int HeadroomFrames = 1920;

	public WiredSteering Steering { get; init; } = WiredSteering.Smooth;

	// Fade-in on all channels after a gap; 0 = none (the haptics jump back to where they were).
	public int GapFadeFrames { get; init; } = DefaultGapFadeFrames;

	// Fade-in on all channels when the stream starts; 0 = none (a 16-frame ramp from silence, as before).
	public int StartFadeFrames { get; init; }

	// Fade-out of the haptics when their input turns silent; 0 = none (a 16-frame ramp to silence, as before).
	public int ReleaseFadeFrames { get; init; }

	// Scale of the haptics samples (0..1).
	public float HapticsGain { get; init; } = 1f;

	// Soft limiter ceiling (0..1, y = c * tanh(x / c)); null = none.
	public float? Limit { get; init; }

	// Queued frames aimed at before each block; also the silence cushion queued before a start or after a gap.
	public int TargetFrames { get; init; } = DefaultTargetFrames;

	// The controller's speaker volume (0..1 of its range, 0 = muted) while channels 1-2 carry no sound; null = keep the
	// user's volume (the default).
	public double? SpeakerIdleVolume { get; init; }

	// One log line per second of stream with its gap, nudge, late and drop counts.
	public bool EventLog { get; init; }

	// Values that were not understood or were clamped, for the log line.
	public IReadOnlyList<string> Notes { get; init; } = [];

	public int CushionFrames => TargetFrames;

	public int MaxQueuedFrames => TargetFrames + HeadroomFrames;

	public static WiredAudioTuning Default { get; } = new();

	private static readonly Lazy<WiredAudioTuning> current = new(() => Parse(Environment.GetEnvironmentVariable));

	// The process's switches (environment variables read once).
	public static WiredAudioTuning Current => current.Value;

	// True when no variable changed anything.
	public bool IsDefault => this with { Notes = Default.Notes } == Default;

	// Reads the switches; a value not understood is ignored and one out of range clamped (both noted).
	public static WiredAudioTuning Parse(Func<string, string?> env)
	{
		List<string> notes = [];
		WiredAudioTuning t = new();
		if (Value(env, SteerVariable) is { } steer)
		{
			if (steer.Equals("smooth", StringComparison.OrdinalIgnoreCase))
			{
				t = t with { Steering = WiredSteering.Smooth };
			}
			else if (steer.Equals("legacy", StringComparison.OrdinalIgnoreCase))
			{
				t = t with { Steering = WiredSteering.Legacy };
			}
			else
			{
				notes.Add(SteerVariable + " not understood");
			}
		}
		if (Number(env, FadeVariable, 0, MaxFadeMs, notes) is { } fadeMs)
		{
			int frames = (int)Math.Round(fadeMs * WiredAudioFrames.Rate / 1000);
			t = t with { GapFadeFrames = frames, StartFadeFrames = frames, ReleaseFadeFrames = frames };
		}
		if (Number(env, GainVariable, 0, 1, notes) is { } gain)
		{
			t = t with { HapticsGain = (float)gain };
		}
		if (Value(env, LimitVariable) is { } limitText && (limitText.Equals("off", StringComparison.OrdinalIgnoreCase) || limitText == "0"))
		{
			t = t with { Limit = null };
		}
		else if (Number(env, LimitVariable, 0.05, 1, notes) is { } limit)
		{
			t = t with { Limit = (float)limit };
		}
		if (Number(env, BufferVariable, MinBufferMs, MaxBufferMs, notes) is { } bufferMs)
		{
			t = t with { TargetFrames = (int)Math.Round(bufferMs * WiredAudioFrames.Rate / 1000) };
		}
		if (Value(env, SpeakerIdleVariable) is { } idle)
		{
			if (idle.Equals("mute", StringComparison.OrdinalIgnoreCase))
			{
				t = t with { SpeakerIdleVolume = 0 };
			}
			else if (!idle.Equals("keep", StringComparison.OrdinalIgnoreCase) &&
				Number(env, SpeakerIdleVariable, 0, 100, notes) is { } percent)
			{
				t = t with { SpeakerIdleVolume = percent / 100 };
			}
		}
		if (Value(env, EventLogVariable) is { } log)
		{
			t = t with { EventLog = log is "1" || log.Equals("on", StringComparison.OrdinalIgnoreCase) || log.Equals("true", StringComparison.OrdinalIgnoreCase) };
		}
		return t with { Notes = notes };
	}

	// For the open log line: every value in use, then the notes.
	public string Describe()
	{
		string text = string.Create(CultureInfo.InvariantCulture,
			$"steer {(Steering == WiredSteering.Smooth ? "smooth" : "legacy")}, gap fade {Ms(GapFadeFrames)}, start fade {Ms(StartFadeFrames)}," +
			$" release fade {Ms(ReleaseFadeFrames)}, gain {HapticsGain:0.00}, limiter {(Limit is { } l ? l.ToString("0.00", CultureInfo.InvariantCulture) : "off")}," +
			$" buffer {Ms(TargetFrames)}, speaker idle {(SpeakerIdleVolume is { } v ? (v == 0 ? "mute" : (v * 100).ToString("0", CultureInfo.InvariantCulture) + " %") : "keep")}," +
			$" event log {(EventLog ? "on" : "off")}");
		return Notes.Count == 0 ? text : text + " (" + string.Join("; ", Notes) + ")";
	}

	private static string Ms(int frames) => (frames * 1000.0 / WiredAudioFrames.Rate).ToString("0.#", CultureInfo.InvariantCulture) + " ms";

	private static string? Value(Func<string, string?> env, string name) =>
		env(name)?.Trim() is { Length: > 0 } v ? v : null;

	// A decimal ("0.5" or "0,5"), clamped to min..max; null when unset or not a number.
	private static double? Number(Func<string, string?> env, string name, double min, double max, List<string> notes)
	{
		if (Value(env, name) is not { } text)
		{
			return null;
		}
		if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || double.IsNaN(v))
		{
			notes.Add(name + " not understood");
			return null;
		}
		if (v < min || v > max)
		{
			notes.Add(name + " clamped");
			v = Math.Clamp(v, min, max);
		}
		return v;
	}
}

// The haptics samples' soft limiter: y = c * tanh(x / c) never exceeds the ceiling c and is nearly linear well below it.
public static class WiredHapticsLimiter
{
	public static float Apply(float x, float ceiling) => ceiling * MathF.Tanh(x / ceiling);
}
