// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// EXPERIMENTAL (the Xbox One pad; docs/research/xbox-one-impulse-triggers.md, sections 4 and 7): a game's trigger-motor
// rumble ("impulse triggers", from a pad that is an ITriggerRumbleSource) played on the DualSense's adaptive triggers as
// their vibration effect, emulating the small ERM motor of an Xbox trigger: the level spins a modelled motor up and down
// (ImpulseMotor), and the motor's level sets amplitude and frequency together (ImpulseTuning).
// - Layering per trigger: a mod's effect (DSX) > the trigger rumble > the game's own effect or the fallback effect. While
//   a trigger's rumble plays it replaces the fallback (Feedback / Weapon / Vibration) on that trigger; when it stops the
//   trigger gets back what it has without it (ControllerTriggers: the fallback, the game's effect, or off).
// - The trigger strength (TriggerIntensity) scales it like a game's effect; a strength change re-sends it (ApplyProfile).
// - It is not a native DualSense trigger effect: it does not set TriggersSeen (the fallback comes back after it) and does
//   not count for native support detection.
// - Writes: one output report only when the mapped (amplitude, Hz) of a trigger steps, at most one per
//   ImpulseTriggerPlayer.MinIntervalMs (both triggers share it). While a motor spins up or down a timer (Time.CreateTimer)
//   pumps the model every MinIntervalMs; it stops when both motors have settled.
// - Tuning: TriggerRumbleTuningSource (the experimental tuning file the WGI probe writes) when set, else TriggerRumbleTuning.
public sealed partial class ControllerBridge
{
	private readonly object impulseGate = new();

	// Guarded by impulseGate.
	private readonly ImpulseTriggerPlayer impulsePlayer = new();
	private ITimer? impulseTimer;
	private bool impulseTimerOn;
	private ImpulseTuningFile? impulseTuningSource;

	// The effect per trigger as last sent (unscaled; null: no rumble on it). Written under impulseGate, read lock-free (the
	// arrays are never changed after they are made).
	private byte[]? impulseRight, impulseLeft;

	// Guarded by impulseGate: the session's diagnostics.
	private long impulseChanges, impulseReports;
	private bool impulseLogged;
	private int impulseMaxLevelLeft, impulseMaxLevelRight, impulseMaxAmplitudeLeft, impulseMaxAmplitudeRight;

	// The tuning when no TriggerRumbleTuningSource is set (TriggerRumbleFeel sets it to that choice's preset).
	public ImpulseTuning TriggerRumbleTuning { get; set; } = ImpulseTuning.XboxLike;

	// Guarded by impulseGate: the user's Trigger Rumble choice, and the game's last levels (played again when it is turned
	// back on).
	private TriggerRumbleFeel impulseFeel = TriggerRumbleFeel.XboxLike;
	private TriggerRumble impulseGameLevels;
	private int sawTriggerRumble;

	// The profile's Trigger Rumble choice: a preset (Xbox-like, Soft, Sharp) re-maps a rumble that is playing; Off takes it
	// off the triggers at once (the game's levels are still followed, and still count as the game using trigger rumble).
	public TriggerRumbleFeel TriggerRumbleFeel
	{
		get
		{
			lock (impulseGate)
			{
				return impulseFeel;
			}
		}
		set
		{
			lock (impulseGate)
			{
				if (value == impulseFeel || disposed)
				{
					return;
				}
				bool wasOff = impulseFeel == TriggerRumbleFeel.Off;
				impulseFeel = value;
				if (ImpulseTriggers.TuningFor(value) is { } preset)
				{
					TriggerRumbleTuning = preset;
				}
				if (value == TriggerRumbleFeel.Off)
				{
					StopImpulse();
				}
				else
				{
					ImpulseTuning tuning = CurrentImpulseTuning;
					if (wasOff)
					{
						impulsePlayer.SetTarget(impulseGameLevels, ImpulseNowMs, tuning);
					}
					PumpImpulse(tuning);
				}
			}
		}
	}

	// EXPERIMENTAL: the live tuning file (ImpulseTuningFile.Shared, set by the host only while the Xbox One pad's developer
	// switch is on). A change re-maps a rumble that is playing.
	public ImpulseTuningFile? TriggerRumbleTuningSource
	{
		get => impulseTuningSource;
		init
		{
			impulseTuningSource = value;
			if (value != null)
			{
				value.Changed += OnImpulseTuningChanged;
			}
		}
	}

	private ImpulseTuning CurrentImpulseTuning => impulseTuningSource?.Current ?? TriggerRumbleTuning;

	// Trigger rumble level changes from the pad, and output reports they caused (diagnostics, tests).
	public long TriggerRumbleChanges
	{
		get
		{
			lock (impulseGate)
			{
				return impulseChanges;
			}
		}
	}

	public long TriggerRumbleReports
	{
		get
		{
			lock (impulseGate)
			{
				return impulseReports;
			}
		}
	}

	// True while a trigger rumble plays on either trigger.
	public bool TriggerRumbleOn => Volatile.Read(ref impulseRight) != null || Volatile.Read(ref impulseLeft) != null;

	// The vibration (amplitude, Hz) each trigger plays now, before the trigger strength (diagnostics, tests).
	public ((int Amplitude, int Hz) Left, (int Amplitude, int Hz) Right) TriggerRumbleNow
	{
		get
		{
			lock (impulseGate)
			{
				return (impulsePlayer.SentLeft, impulsePlayer.SentRight);
			}
		}
	}

	private long ImpulseNowMs => (long)(Time.GetTimestamp() * 1000.0 / Time.TimestampFrequency);

	// Plays the game's trigger motor levels (percent) on the controller's triggers: the modelled motors head for them,
	// and the triggers are written whenever their mapped vibration steps.
	public void PlayTriggerRumble(TriggerRumble levels)
	{
		lock (impulseGate)
		{
			if (disposed)
			{
				return;
			}
			impulseChanges++;
			impulseMaxLevelLeft = Math.Max(impulseMaxLevelLeft, levels.Left);
			impulseMaxLevelRight = Math.Max(impulseMaxLevelRight, levels.Right);
			impulseGameLevels = levels;
			if ((levels.Left | levels.Right) != 0)
			{
				// The game uses trigger rumble (learning, GameModeController), whatever the user's choice.
				Interlocked.Exchange(ref sawTriggerRumble, 1);
			}
			if (impulseFeel == TriggerRumbleFeel.Off)
			{
				return;
			}
			ImpulseTuning tuning = CurrentImpulseTuning;
			impulsePlayer.SetTarget(levels, ImpulseNowMs, tuning);
			PumpImpulse(tuning);
		}
	}

	// The game sent trigger rumble since the last call (GameSignals.TriggerRumble).
	private bool TakeTriggerRumbleSignal() => Interlocked.Exchange(ref sawTriggerRumble, 0) != 0;

	private void OnImpulseTuningChanged(ImpulseTuning tuning)
	{
		lock (impulseGate)
		{
			if (!disposed && impulseFeel != TriggerRumbleFeel.Off)
			{
				PumpImpulse(tuning);
			}
		}
	}

	private void OnImpulseTimer()
	{
		lock (impulseGate)
		{
			if (!disposed && impulseFeel != TriggerRumbleFeel.Off)
			{
				PumpImpulse(CurrentImpulseTuning);
			}
		}
	}

	// Takes a trigger rumble still playing off the triggers at once (the active game exited, the host released the pad),
	// the triggers getting back what they have without it (the fallback, the game's effect, or off). The game's levels
	// are dropped, and the pad forgets them, so its next trigger command plays again. One log line when one was playing.
	public bool StopTriggerRumble(string why)
	{
		bool playing;
		lock (impulseGate)
		{
			if (disposed)
			{
				return false;
			}
			playing = impulseRight != null || impulseLeft != null || impulsePlayer.Busy;
			impulseGameLevels = default;
			StopImpulse();
		}
		// Outside impulseGate: the pad raises its trigger levels under its own lock, then takes impulseGate.
		(pad as IChangeOnlyMotors)?.ForgetMotors(main: false, triggers: true);
		if (playing)
		{
			RumbleLog($"trigger rumble{LogSlot}: stopped ({why})");
		}
		return playing;
	}

	// Caller holds impulseGate. Trigger Rumble turned off: the timer stops and a rumble still on leaves the triggers.
	private void StopImpulse()
	{
		impulseTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		impulseTimerOn = false;
		impulsePlayer.Reset();
		if (impulseRight == null && impulseLeft == null)
		{
			return;
		}
		Volatile.Write(ref impulseRight, null);
		Volatile.Write(ref impulseLeft, null);
		(byte[] r, byte[] l) = ControllerTriggers();
		session.UpdateOutput(b => OutputBlock.SetTriggers(b, r, l));
		impulseReports++;
	}

	// Caller holds impulseGate. Advances the motors, sends a report when one is due, and keeps the timer running while
	// the player is busy.
	private void PumpImpulse(ImpulseTuning tuning)
	{
		if (impulsePlayer.Pump(ImpulseNowMs, tuning))
		{
			SendImpulse(tuning);
		}
		bool busy = impulsePlayer.Busy;
		if (busy == impulseTimerOn)
		{
			return;
		}
		impulseTimerOn = busy;
		TimeSpan period = busy ? TimeSpan.FromMilliseconds(ImpulseTriggerPlayer.MinIntervalMs) : Timeout.InfiniteTimeSpan;
		impulseTimer ??= Time.CreateTimer(_ => OnImpulseTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		impulseTimer.Change(period, period);
	}

	// Caller holds impulseGate, so the controller gets the changes in the order they were made.
	private void SendImpulse(ImpulseTuning tuning)
	{
		byte[]? left = ImpulseTriggers.Effect(impulsePlayer.SentLeft);
		byte[]? right = ImpulseTriggers.Effect(impulsePlayer.SentRight);
		Volatile.Write(ref impulseRight, right);
		Volatile.Write(ref impulseLeft, left);
		(byte[] r, byte[] l) = ControllerTriggers();
		session.UpdateOutput(b => OutputBlock.SetTriggers(b, r, l));
		impulseReports++;
		if (left != null)
		{
			impulseMaxAmplitudeLeft = Math.Max(impulseMaxAmplitudeLeft, ImpulseTriggers.AmplitudeOf(l));
		}
		if (right != null)
		{
			impulseMaxAmplitudeRight = Math.Max(impulseMaxAmplitudeRight, ImpulseTriggers.AmplitudeOf(r));
		}
		if (!impulseLogged && (right != null || left != null))
		{
			impulseLogged = true;
			TriggerRumble target = impulsePlayer.Target;
			Diagnostics.AppLog.Write(string.Create(CultureInfo.InvariantCulture,
				$"trigger rumble{LogSlot}: first trigger rumble played on the DualSense: {Describe("LT", target.Left, left, l)}, " +
				$"{Describe("RT", target.Right, right, r)} (spinning up; trigger strength {TriggerIntensity:0.##}, from zone {ImpulseTriggers.Position}; " +
				$"tuning {tuning.Describe()}; steady state LT {Steady(tuning, target.Left)}, RT {Steady(tuning, target.Right)})"));
		}
	}

	// "LT 75 -> amplitude 3 at 52 Hz (sent 2)", "LT 0 -> off" (sent: after the strength and any mod effect on that trigger).
	private static string Describe(string name, int level, byte[]? mapped, byte[] sent)
	{
		if (mapped == null)
		{
			return $"{name} {level} -> off";
		}
		int amplitude = ImpulseTriggers.AmplitudeOf(mapped);
		int played = ImpulseTriggers.AmplitudeOf(sent);
		string text = $"{name} {level} -> amplitude {amplitude} at {ImpulseTriggers.FrequencyOf(mapped)} Hz";
		return played == amplitude ? text : $"{text} (sent {played})";
	}

	private static string Steady(ImpulseTuning tuning, int level) =>
		tuning.Map(level) is (int amplitude, int hz) && amplitude > 0 ? $"amplitude {amplitude} at {hz} Hz" : "off";

	// Puts the trigger rumble over the block's triggers (caller: ApplyTriggers, after the game's effects and the fallback,
	// before a mod's), scaled by k.
	private void ApplyImpulseTriggers(byte[] block, double k)
	{
		byte[]? right = Volatile.Read(ref impulseRight);
		byte[]? left = Volatile.Read(ref impulseLeft);
		if (right != null)
		{
			(k < 1 ? TriggerEffects.Scale(right, k) : right).CopyTo(block, OutputBlock.RightTrigger);
			block[OutputBlock.Flag0] |= OutputBlock.RightTriggerEffect;
		}
		if (left != null)
		{
			(k < 1 ? TriggerEffects.Scale(left, k) : left).CopyTo(block, OutputBlock.LeftTrigger);
			block[OutputBlock.Flag0] |= OutputBlock.LeftTriggerEffect;
		}
	}

	// The bridge goes away (disconnect, pad swap): the timer stops, a rumble still on is taken off the triggers at once,
	// the pad forgets its trigger levels, and one line sums up the session.
	private void EndTriggerRumble()
	{
		if (impulseTuningSource != null)
		{
			impulseTuningSource.Changed -= OnImpulseTuningChanged;
		}
		(pad as IChangeOnlyMotors)?.ForgetMotors(main: false, triggers: true);
		lock (impulseGate)
		{
			impulseTimer?.Dispose();
			impulseTimer = null;
			impulseTimerOn = false;
			impulsePlayer.Reset();
			if (impulseRight != null || impulseLeft != null)
			{
				Volatile.Write(ref impulseRight, null);
				Volatile.Write(ref impulseLeft, null);
				try
				{
					(byte[] r, byte[] l) = ControllerTriggers();
					session.UpdateOutput(b => OutputBlock.SetTriggers(b, r, l));
					impulseReports++;
				}
				catch (Exception)
				{
					// The controller may already be gone.
				}
			}
			if (impulseChanges == 0)
			{
				return;
			}
			Diagnostics.AppLog.Write(string.Create(CultureInfo.InvariantCulture,
				$"trigger rumble{LogSlot} summary: {impulseChanges} level changes from the game, {impulseReports} trigger writes to the controller, " +
				$"max level LT {impulseMaxLevelLeft} RT {impulseMaxLevelRight}, max amplitude sent LT {impulseMaxAmplitudeLeft} RT {impulseMaxAmplitudeRight}, " +
				$"trigger strength {TriggerIntensity:0.##}, tuning {CurrentImpulseTuning.Describe()}"));
		}
	}
}
