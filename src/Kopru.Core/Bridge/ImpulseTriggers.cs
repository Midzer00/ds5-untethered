// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// EXPERIMENTAL: how a trigger motor level from an Xbox One / Series game ("impulse triggers", 0-100 %) becomes a DualSense
// adaptive-trigger effect (docs/research/xbox-one-impulse-triggers.md, sections 4 and 7). The level goes through a model
// of the motor's inertia (ImpulseMotor), then ImpulseTuning maps the motor's level to an amplitude 1-8 and a frequency
// that rises with it, like an ERM's pitch. The effect is TriggerEffects.Vibration (mode 0x26) from zone Position to the
// end of the travel. The trigger strength setting (ControllerBridge.TriggerIntensity) scales it like a game's own effect.
public static class ImpulseTriggers
{
	// The zone the vibration starts in: 0 is the released trigger, so the effect covers zones 0-9, the whole travel, and
	// is felt wherever the finger rests (the earliest start the effect has).
	public const int Position = 0;

	// The tuning of a Trigger Rumble choice (the fixed presets of ImpulseTuning), null for Off.
	public static ImpulseTuning? TuningFor(TriggerRumbleFeel feel) => feel switch
	{
		TriggerRumbleFeel.Off => null,
		TriggerRumbleFeel.Soft => ImpulseTuning.Soft,
		TriggerRumbleFeel.Sharp => ImpulseTuning.Sharp,
		_ => ImpulseTuning.XboxLike
	};

	// The effect for a vibration (amplitude 1-8, Hz), null for none.
	public static byte[]? Effect((int Amplitude, int Hz) vibration) =>
		vibration.Amplitude == 0 ? null : TriggerEffects.Vibration(Position, vibration.Amplitude, vibration.Hz);

	// The steady-state effect for a level (no inertia), unscaled; null for level 0.
	public static byte[]? Effect(int level, ImpulseTuning tuning) => Effect(tuning.Map(level));

	// The amplitude (1-8) a vibration effect plays in zone Position, 0 for anything else (off, another mode).
	public static int AmplitudeOf(ReadOnlySpan<byte> effect)
	{
		if (effect.Length < TriggerEffects.Size || effect[0] != TriggerEffects.VibrationMode)
		{
			return 0;
		}
		ushort active = (ushort)(effect[1] | effect[2] << 8);
		if ((active & (1 << Position)) == 0)
		{
			return 0;
		}
		uint packed = (uint)(effect[3] | effect[4] << 8 | effect[5] << 16 | effect[6] << 24);
		return (int)((packed >> (3 * Position)) & 0x07) + 1;
	}

	// The frequency (Hz) of a vibration effect, 0 for anything else.
	public static int FrequencyOf(ReadOnlySpan<byte> effect) =>
		effect.Length >= TriggerEffects.Size && effect[0] == TriggerEffects.VibrationMode ? effect[9] : 0;
}

// One trigger motor's inertia: the level it actually turns at (percent) follows the game's level with a first-order lag,
// AttackMs while speeding up and DecayMs while slowing down (0 = at once). A motor slowing towards a stop stalls below
// ImpulseTuning.StallLevel instead of fading out for ever.
public struct ImpulseMotor
{
	public double Level { get; private set; }

	public void Advance(double target, double elapsedMs, ImpulseTuning tuning)
	{
		target = Math.Clamp(target, 0, 100);
		double tau = target > Level ? tuning.AttackMs : tuning.DecayMs;
		Level = tau <= 0 ? target : target + (Level - target) * Math.Exp(-Math.Max(0, elapsedMs) / tau);
		if (Math.Abs(Level - target) < 0.25 || target < Level && Level < ImpulseTuning.StallLevel && target < ImpulseTuning.OffLevel)
		{
			Level = target;
		}
	}

	public void Stop() => Level = 0;
}

// Both trigger motors of a pad and what was last written to the controller. Not thread-safe (ControllerBridge locks).
// Pump advances the motors to `nowMs` and says whether a new report is due: only when the mapped (amplitude, Hz) of
// either trigger changed, and never sooner than MinIntervalMs after the previous one (both triggers share one report).
// A change held back by the cap goes out on a later Pump. Busy: Pump has more to do (a motor still moving or a change
// held back), so the caller keeps pumping every MinIntervalMs.
public sealed class ImpulseTriggerPlayer
{
	public const int MinIntervalMs = 10;

	private ImpulseMotor left, right;
	private TriggerRumble target;
	private long lastAdvance = long.MinValue;
	private long lastSent = long.MinValue;

	public (int Amplitude, int Hz) SentLeft { get; private set; }

	public (int Amplitude, int Hz) SentRight { get; private set; }

	public TriggerRumble Target => target;

	public double LeftLevel => left.Level;

	public double RightLevel => right.Level;

	public bool Busy { get; private set; }

	// The game's new levels: the motors first run up to now under the old ones.
	public void SetTarget(TriggerRumble levels, long nowMs, ImpulseTuning tuning)
	{
		Advance(nowMs, tuning);
		target = levels;
	}

	// True when a report with SentLeft / SentRight is due now (they are already updated).
	public bool Pump(long nowMs, ImpulseTuning tuning)
	{
		Advance(nowMs, tuning);
		(int, int) l = tuning.Map(left.Level);
		(int, int) r = tuning.Map(right.Level);
		bool moving = left.Level != target.Left || right.Level != target.Right;
		if (l == SentLeft && r == SentRight)
		{
			Busy = moving;
			return false;
		}
		if (lastSent != long.MinValue && nowMs - lastSent < MinIntervalMs)
		{
			Busy = true;
			return false;
		}
		SentLeft = l;
		SentRight = r;
		lastSent = nowMs;
		Busy = moving;
		return true;
	}

	// Everything stops at once (the bridge ends).
	public void Reset()
	{
		left.Stop();
		right.Stop();
		target = default;
		SentLeft = SentRight = (0, 0);
		Busy = false;
	}

	private void Advance(long nowMs, ImpulseTuning tuning)
	{
		double elapsed = lastAdvance == long.MinValue ? 0 : Math.Max(0, nowMs - lastAdvance);
		lastAdvance = nowMs;
		left.Advance(target.Left, elapsed, tuning);
		right.Advance(target.Right, elapsed, tuning);
	}
}
