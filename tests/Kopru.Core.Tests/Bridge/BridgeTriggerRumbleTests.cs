// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// A clock in whole milliseconds whose timers fire only when the test advances it.
internal sealed class TimerClock : TimeProvider
{
	private readonly List<ClockTimer> timers = [];

	public long NowMs { get; private set; } = 1_000_000;

	public override long TimestampFrequency => 1000;

	public override long GetTimestamp() => NowMs;

	public int ActiveTimers => timers.Count(t => t.Due != null);

	public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
	{
		ClockTimer timer = new(this, callback, state);
		timer.Change(dueTime, period);
		timers.Add(timer);
		return timer;
	}

	// Moves the clock on, firing each timer at its due times on the way.
	public void Advance(long ms)
	{
		long end = NowMs + ms;
		while (timers.Where(t => t.Due <= end).OrderBy(t => t.Due).FirstOrDefault() is { } next)
		{
			NowMs = next.Due!.Value;
			next.Due = next.Period > 0 ? next.Due + next.Period : null;
			next.Fire();
		}
		NowMs = end;
	}

	private sealed class ClockTimer(TimerClock clock, TimerCallback callback, object? state) : ITimer
	{
		public long? Due { get; set; }

		public long Period { get; private set; }

		public void Fire() => callback(state);

		public bool Change(TimeSpan dueTime, TimeSpan period)
		{
			Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.NowMs + (long)dueTime.TotalMilliseconds;
			Period = period == Timeout.InfiniteTimeSpan ? 0 : (long)period.TotalMilliseconds;
			return true;
		}

		public void Dispose() => Due = null;

		public ValueTask DisposeAsync()
		{
			Dispose();
			return ValueTask.CompletedTask;
		}
	}
}

// EXPERIMENTAL: the impulse trigger curve (an ERM's pitch and strength rise with its drive level).
public class ImpulseTuningTests
{
	[Theory]
	[InlineData(0, 0, 0)]
	[InlineData(0.4, 0, 0)]
	[InlineData(1, 1, 30)]
	[InlineData(10, 1, 38)]
	[InlineData(25, 3, 45)]
	[InlineData(50, 5, 60)]
	[InlineData(75, 6, 75)]
	[InlineData(100, 8, 90)]
	[InlineData(255, 8, 90)]
	public void XboxLike_MapsLevelToAmplitudeAndFrequency(double level, int amplitude, int hz) =>
		Assert.Equal((amplitude, hz), ImpulseTuning.XboxLike.Map(level));

	[Fact]
	public void EveryPreset_RisesInStrengthAndPitchWithTheLevel_WithinItsRange()
	{
		foreach ((string name, ImpulseTuning tuning) in ImpulseTuning.Presets)
		{
			(int Amplitude, int Hz) previous = (0, 0);
			HashSet<int> pitches = [];
			for (int level = 1; level <= 100; level++)
			{
				(int a, int hz) = tuning.Map(level);
				Assert.InRange(a, 1, 8);
				Assert.InRange(hz, tuning.MinHz, tuning.MaxHz);
				Assert.True(a >= previous.Amplitude && hz >= previous.Hz, $"{name} level {level}");
				previous = (a, hz);
				pitches.Add(hz);
			}
			Assert.Equal((8, tuning.MaxHz), tuning.Map(100));
			Assert.Equal(tuning.MinHz, tuning.Map(1).Hz);
			// The pitch moves in steps, so small level changes write nothing new.
			Assert.InRange(pitches.Count, 2, ImpulseTuning.FrequencySteps + 1);
		}
	}

	[Fact]
	public void Presets_DifferInTheDirectionTheirNamesSay()
	{
		ImpulseTuning x = ImpulseTuning.XboxLike, soft = ImpulseTuning.Soft, sharp = ImpulseTuning.Sharp;
		Assert.True(sharp.AttackMs < x.AttackMs && x.AttackMs < soft.AttackMs);
		Assert.True(sharp.DecayMs < x.DecayMs && x.DecayMs < soft.DecayMs);
		Assert.True(sharp.MaxHz > x.MaxHz && x.MaxHz > soft.MaxHz);
		Assert.True(soft.Map(20).Amplitude < x.Map(20).Amplitude && x.Map(20).Amplitude < sharp.Map(20).Amplitude);
	}

	[Fact]
	public void Normalized_KeepsEveryValueInRange()
	{
		ImpulseTuning t = new ImpulseTuning { MinHz = 300, MaxHz = 0, FrequencyCurve = double.NaN, AmplitudeCurve = 9, AttackMs = -5, DecayMs = 9000 }.Normalized();
		Assert.Equal(1, t.MinHz);
		Assert.Equal(255, t.MaxHz);
		Assert.Equal(ImpulseTuning.XboxLike.FrequencyCurve, t.FrequencyCurve);
		Assert.Equal(3, t.AmplitudeCurve);
		Assert.Equal(0, t.AttackMs);
		Assert.Equal(500, t.DecayMs);
	}

	[Fact]
	public void Json_RoundTrips_AndMissingValuesKeepTheirDefaults()
	{
		Assert.Equal(ImpulseTuning.Sharp, ImpulseTuning.FromJson(ImpulseTuning.Sharp.ToJson()));
		ImpulseTuning partial = ImpulseTuning.FromJson("""{ "MaxHz": 120 }""");
		Assert.Equal(ImpulseTuning.XboxLike with { MaxHz = 120 }, partial);
		Assert.Throws<System.Text.Json.JsonException>(() => ImpulseTuning.FromJson("{ not json"));
	}

	[Fact]
	public void DefaultPath_IsInTheAppsDataFolder() =>
		Assert.Equal(Path.Combine(AppPaths.DataDirectory, ImpulseTuning.FileName), ImpulseTuning.DefaultPath);
}

// EXPERIMENTAL: the trigger motor's spin-up and spin-down.
public class ImpulseMotorTests
{
	private static readonly ImpulseTuning Tuning = ImpulseTuning.XboxLike with { AttackMs = 25, DecayMs = 40 };

	[Fact]
	public void SpinUp_FollowsTheAttackTimeConstant_HoweverItIsStepped()
	{
		ImpulseMotor once = default;
		once.Advance(100, 25, Tuning);
		Assert.Equal(100 * (1 - Math.Exp(-1)), once.Level, 6);

		ImpulseMotor stepped = default;
		for (int i = 0; i < 10; i++)
		{
			stepped.Advance(100, 2.5, Tuning);
		}
		Assert.Equal(once.Level, stepped.Level, 6);
		stepped.Advance(100, 1000, Tuning);
		Assert.Equal(100, stepped.Level);
	}

	[Fact]
	public void SpinDown_UsesTheDecayTime_AndStallsInsteadOfFadingForEver()
	{
		ImpulseMotor m = default;
		m.Advance(100, 0, Tuning with { AttackMs = 0 });
		Assert.Equal(100, m.Level);
		m.Advance(0, 40, Tuning);
		Assert.Equal(100 * Math.Exp(-1), m.Level, 6);
		// 100 -> below the stall level takes 40 ms x ln(100 / 10) = 92 ms; then it is stopped, not at 9.99...
		m = default;
		m.Advance(100, 0, Tuning with { AttackMs = 0 });
		m.Advance(0, 91, Tuning);
		Assert.True(m.Level > ImpulseTuning.StallLevel);
		m.Advance(0, 2, Tuning);
		Assert.Equal(0, m.Level);
		// A low level held by the game is not a stall.
		m.Advance(5, 1000, Tuning);
		Assert.Equal(5, m.Level);
	}

	[Fact]
	public void ZeroTimes_FollowAtOnce()
	{
		ImpulseTuning instant = Tuning with { AttackMs = 0, DecayMs = 0 };
		ImpulseMotor m = default;
		m.Advance(70, 0, instant);
		Assert.Equal(70, m.Level);
		m.Advance(20, 0, instant);
		Assert.Equal(20, m.Level);
	}
}

// EXPERIMENTAL: what reaches the controller, and how often.
public class ImpulseTriggerPlayerTests
{
	private static readonly ImpulseTuning Instant = ImpulseTuning.XboxLike with { AttackMs = 0, DecayMs = 0 };

	[Fact]
	public void RateCap_HoldsAChangeBack_UntilTheIntervalIsOver()
	{
		ImpulseTriggerPlayer p = new();
		p.SetTarget(new TriggerRumble(0, 50), 0, Instant);
		Assert.True(p.Pump(0, Instant));
		Assert.Equal((5, 60), p.SentRight);

		p.SetTarget(new TriggerRumble(0, 100), 3, Instant);
		Assert.False(p.Pump(3, Instant));
		Assert.True(p.Busy);
		Assert.False(p.Pump(9, Instant));
		Assert.True(p.Pump(10, Instant));
		Assert.Equal((8, 90), p.SentRight);
		Assert.False(p.Busy);
	}

	[Fact]
	public void SameStep_WritesNothing()
	{
		ImpulseTriggerPlayer p = new();
		p.SetTarget(new TriggerRumble(0, 50), 0, Instant);
		Assert.True(p.Pump(0, Instant));
		p.SetTarget(new TriggerRumble(0, 52), 100, Instant);
		Assert.False(p.Pump(100, Instant));
		Assert.False(p.Busy);
	}

	[Fact]
	public void SpinUpAndDown_StepsThroughAmplitudeAndPitch_AtMostOneReportPer10Ms()
	{
		ImpulseTuning tuning = ImpulseTuning.XboxLike;
		ImpulseTriggerPlayer p = new();
		List<(long T, (int A, int Hz) V)> sent = [];
		p.SetTarget(new TriggerRumble(100, 0), 0, tuning);
		for (long t = 0; t <= 300; t++)
		{
			if (p.Pump(t, tuning))
			{
				sent.Add((t, p.SentLeft));
			}
		}
		Assert.False(p.Busy);
		Assert.Equal((8, 90), p.SentLeft);
		Assert.InRange(sent[0].T, 1, ImpulseTriggerPlayer.MinIntervalMs); // like an ERM's ~10 ms lag before it is felt
		Assert.True(sent[0].V.A < 8 && sent[0].V.Hz < 90, "starts low and slow");
		Assert.True(sent.Count >= 4, "several steps on the way up");
		for (int i = 1; i < sent.Count; i++)
		{
			Assert.True(sent[i].T - sent[i - 1].T >= ImpulseTriggerPlayer.MinIntervalMs);
			Assert.True(sent[i].V.A >= sent[i - 1].V.A && sent[i].V.Hz >= sent[i - 1].V.Hz);
		}
		// Settled at full level by ~5 time constants.
		Assert.True(sent[^1].T <= 5 * tuning.AttackMs, $"last step at {sent[^1].T} ms");

		sent.Clear();
		p.SetTarget(default, 1000, tuning);
		for (long t = 1000; t <= 1300; t++)
		{
			if (p.Pump(t, tuning))
			{
				sent.Add((t, p.SentLeft));
			}
		}
		Assert.Equal((0, 0), p.SentLeft);
		Assert.False(p.Busy);
		Assert.True(sent[^1].T - 1000 <= 100, $"stopped after {sent[^1].T - 1000} ms");
		for (int i = 1; i < sent.Count; i++)
		{
			Assert.True(sent[i].T - sent[i - 1].T >= ImpulseTriggerPlayer.MinIntervalMs);
		}
	}

	[Fact]
	public void AShortPulse_IsFeltButWeakerThanAHeldLevel()
	{
		ImpulseTuning tuning = ImpulseTuning.XboxLike;
		ImpulseTriggerPlayer p = new();
		int peak = 0;
		p.SetTarget(new TriggerRumble(0, 100), 0, tuning);
		for (long t = 0; t <= 200; t++)
		{
			if (t == 20)
			{
				p.SetTarget(default, t, tuning);
			}
			p.Pump(t, tuning);
			peak = Math.Max(peak, p.SentRight.Amplitude);
		}
		Assert.InRange(peak, 3, 7);
		Assert.Equal((0, 0), p.SentRight);
	}
}

public partial class BridgeTests
{
	private static readonly ImpulseTuning InstantImpulse = ImpulseTuning.XboxLike with { AttackMs = 0, DecayMs = 0 };

	private static byte[] Vibration(int level) => ImpulseTriggers.Effect(level, InstantImpulse)!;

	private static int TriggerWrites(FakeLink link) => link.Writes.Count(w => w[0] == 0x31);

	private static ControllerBridge ImpulseBridge(BluetoothSession session, IVirtualPad pad, TimerClock clock, ImpulseTuning? tuning = null) =>
		new(session, pad) { Time = clock, TriggerRumbleTuning = tuning ?? InstantImpulse };

	// The profile's Trigger Rumble choice: the three presets as fixed tunings, Off.
	[Fact]
	public void TriggerRumbleChoice_MapsToTheFixedPresets()
	{
		Assert.Same(ImpulseTuning.XboxLike, ImpulseTriggers.TuningFor(TriggerRumbleFeel.XboxLike));
		Assert.Same(ImpulseTuning.Soft, ImpulseTriggers.TuningFor(TriggerRumbleFeel.Soft));
		Assert.Same(ImpulseTuning.Sharp, ImpulseTriggers.TuningFor(TriggerRumbleFeel.Sharp));
		Assert.Null(ImpulseTriggers.TuningFor(TriggerRumbleFeel.Off));
		Assert.Equal((30, 90, 0.8, 25.0, 35.0), (ImpulseTuning.XboxLike.MinHz, ImpulseTuning.XboxLike.MaxHz, ImpulseTuning.XboxLike.AmplitudeCurve,
			ImpulseTuning.XboxLike.AttackMs, ImpulseTuning.XboxLike.DecayMs));
		Assert.Equal((25, 60, 1.2), (ImpulseTuning.Soft.MinHz, ImpulseTuning.Soft.MaxHz, ImpulseTuning.Soft.AmplitudeCurve));
		Assert.Equal((45, 130, 0.6), (ImpulseTuning.Sharp.MinHz, ImpulseTuning.Sharp.MaxHz, ImpulseTuning.Sharp.AmplitudeCurve));
		// The setting reaches the bridge's profile; Xbox-like by default.
		Assert.Equal(TriggerRumbleFeel.XboxLike, Games.GameProfiles.For(new Games.GameProfile()).TriggerRumble);
		Assert.Equal(TriggerRumbleFeel.Sharp, Games.GameProfiles.For(new Games.GameProfile { TriggerRumble = TriggerRumbleFeel.Sharp }).TriggerRumble);
		Assert.Equal(TriggerRumbleFeel.Soft, new ControllerSettings { TriggerRumble = TriggerRumbleFeel.Soft }.Game.TriggerRumble);
	}

}
