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

	private static byte[] GipMotor(byte bitmap, byte lt, byte rt, byte lm, byte rm, byte duration, byte delay = 0, byte repeat = 0) =>
		[Gip.DirectMotor, 0x00, 0x10, 0x09, 0x00, bitmap, lt, rt, lm, rm, duration, delay, repeat];

	private static ControllerBridge ImpulseBridge(BluetoothSession session, IVirtualPad pad, TimerClock clock, ImpulseTuning? tuning = null) =>
		new(session, pad) { Time = clock, TriggerRumbleTuning = tuning ?? InstantImpulse };

	[Fact]
	public void TriggerRumble_PlaysAsVibration_ScaledByTheTriggerStrength()
	{
		FakeLink link = new();
		TimerClock clock = new();
		using BluetoothSession session = new(link);
		using VirtualXboxOne pad = new(autoTick: false);
		using ControllerBridge bridge = ImpulseBridge(session, pad, clock);

		bridge.PlayTriggerRumble(new TriggerRumble(50, 100));
		AssertSent(link, Vibration(100), Vibration(50));
		Assert.Equal(TriggerEffects.Vibration(0, 8, 90), Vibration(100));
		Assert.True(bridge.TriggerRumbleOn);
		Assert.False(bridge.TriggersSeen);
		Assert.False(bridge.TakeGameSignals().Triggers);

		// A strength change re-sends it at once.
		bridge.ApplyProfile(new BridgeProfile(TriggerIntensity: 0.5));
		AssertSent(link, TriggerEffects.Scale(Vibration(100), 0.5), TriggerEffects.Scale(Vibration(50), 0.5));
		clock.Advance(10);
		bridge.PlayTriggerRumble(new TriggerRumble(1, 100));
		AssertSent(link, TriggerEffects.Scale(Vibration(100), 0.5), Vibration(1)); // the lowest step stays felt
		Assert.Equal(1, ImpulseTriggers.AmplitudeOf(Vibration(1)));

		bridge.ApplyProfile(new BridgeProfile(TriggerIntensity: 0));
		AssertSent(link, TriggerEffects.Off(), TriggerEffects.Off());
	}

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

	[Fact]
	public void TriggerRumbleChoice_RemapsAPlayingRumble_AndTheStrengthStillScalesIt()
	{
		FakeLink link = new();
		TimerClock clock = new();
		using BluetoothSession session = new(link);
		using VirtualXboxOne pad = new(autoTick: false);
		using ControllerBridge bridge = ImpulseBridge(session, pad, clock);
		bridge.PlayTriggerRumble(new TriggerRumble(0, 100));
		AssertSent(link, Vibration(100), TriggerEffects.Off());

		bridge.ApplyProfile(new BridgeProfile(TriggerRumble: TriggerRumbleFeel.Sharp, TriggerIntensity: 0.5));
		Assert.Same(ImpulseTuning.Sharp, bridge.TriggerRumbleTuning);
		for (int i = 0; i < 30; i++)
		{
			clock.Advance(10);
		}
		Assert.Equal(ImpulseTuning.Sharp.Map(100), bridge.TriggerRumbleNow.Right);
		AssertSent(link, TriggerEffects.Scale(ImpulseTriggers.Effect(ImpulseTuning.Sharp.Map(100))!, 0.5), TriggerEffects.Off());
	}

	[Fact]
	public void TriggerRumbleOff_LeavesTheTriggersAlone_ButTheGameStillCountsAsUsingIt()
	{
		FakeLink link = new();
		TimerClock clock = new();
		using BluetoothSession session = new(link);
		using VirtualXboxOne pad = new(autoTick: false);
		using ControllerBridge bridge = ImpulseBridge(session, pad, clock);
		byte[] fallback = TriggerEffects.Feedback(2, 5);
		bridge.ApplyProfile(new BridgeProfile(FallbackTrigger: fallback));
		bridge.PlayTriggerRumble(new TriggerRumble(60, 60));
		AssertSent(link, Vibration(60), Vibration(60));
		bridge.TakeGameSignals();

		// Off while it plays: the triggers get back what they have without it.
		bridge.ApplyProfile(new BridgeProfile(FallbackTrigger: fallback, TriggerRumble: TriggerRumbleFeel.Off));
		AssertSent(link, fallback, fallback);
		Assert.False(bridge.TriggerRumbleOn);
		int writes = TriggerWrites(link);
		clock.Advance(10);
		bridge.PlayTriggerRumble(new TriggerRumble(80, 20));
		clock.Advance(50);
		Assert.Equal(writes, TriggerWrites(link));
		Assert.True(bridge.TakeGameSignals().TriggerRumble);
		Assert.False(bridge.TakeGameSignals().TriggerRumble);

		// On again: the game's last levels play at once.
		bridge.ApplyProfile(new BridgeProfile(FallbackTrigger: fallback, TriggerRumble: TriggerRumbleFeel.XboxLike));
		bridge.TriggerRumbleTuning = InstantImpulse;
		clock.Advance(10);
		AssertSent(link, Vibration(20), Vibration(80));
	}

	// The Xbox Trigger Rumble setting off (BridgeProfile.XboxTriggerRumbleOn): on an Xbox Series pad the game's trigger motor
	// commands put nothing on the triggers (the fallback stays) while its main rumble still plays; the game still counts as
	// sending trigger rumble (learning). On: they play with the profile's preset.
	[Fact]
	public void XboxTriggerRumbleSettingOff_IgnoresTriggerMotors_MainRumbleStillPlays_OnPlaysThePreset()
	{
		FakeLink link = new();
		TimerClock clock = new();
		using BluetoothSession session = new(link);
		using VirtualXboxOne pad = new(autoTick: false);
		using ControllerBridge bridge = ImpulseBridge(session, pad, clock);
		byte[] fallback = TriggerEffects.Feedback(2, 5);
		bridge.ApplyProfile(new BridgeProfile(FallbackTrigger: fallback, TriggerRumble: TriggerRumbleFeel.Sharp, XboxTriggerRumbleOn: false));
		Assert.Equal(TriggerRumbleFeel.Off, bridge.TriggerRumbleFeel);
		AssertSent(link, fallback, fallback);
		bridge.TakeGameSignals();

		// Trigger motors only: no trigger write, the fallback stays.
		int writes = TriggerWrites(link);
		pad.Submit(Command(9, UsbIpCodes.DirOut, VirtualXboxOne.OutEndpoint, GipMotor(0x0C, 70, 90, 0, 0, 255)), _ => { });
		clock.Advance(100);
		Assert.Equal(writes, TriggerWrites(link));
		Assert.False(bridge.TriggerRumbleOn);
		Assert.Equal(0, bridge.TriggerRumbleReports);
		AssertSent(link, fallback, fallback);
		Assert.True(bridge.TakeGameSignals().TriggerRumble);

		// The main motors still play as rumble, with the fallback on the triggers.
		pad.Submit(Command(10, UsbIpCodes.DirOut, VirtualXboxOne.OutEndpoint, GipMotor(0x03, 0, 0, 60, 60, 255)), _ => { });
		Assert.NotEqual(((byte)0, (byte)0), bridge.Motors);
		Assert.True(bridge.TakeGameSignals().Rumble);
		AssertSent(link, fallback, fallback);
		Assert.False(bridge.TriggerRumbleOn);

		// On: the trigger motors play with the profile's preset (the game's last levels at once, then its next command).
		bridge.ApplyProfile(new BridgeProfile(FallbackTrigger: fallback, TriggerRumble: TriggerRumbleFeel.Sharp, XboxTriggerRumbleOn: true));
		Assert.Equal(TriggerRumbleFeel.Sharp, bridge.TriggerRumbleFeel);
		Assert.Same(ImpulseTuning.Sharp, bridge.TriggerRumbleTuning);
		clock.Advance(10);
		pad.Submit(Command(11, UsbIpCodes.DirOut, VirtualXboxOne.OutEndpoint, GipMotor(0x0C, 0, 100, 0, 0, 255)), _ => { });
		for (int i = 0; i < 30; i++)
		{
			clock.Advance(10);
		}
		Assert.True(bridge.TriggerRumbleOn);
		Assert.Equal(ImpulseTuning.Sharp.Map(100), bridge.TriggerRumbleNow.Right);
		AssertSent(link, ImpulseTriggers.Effect(ImpulseTuning.Sharp.Map(100))!, fallback);
	}

	[Fact]
	public void TriggerRumble_ReplacesTheFallbackWhileItPlays_ThenTheFallbackComesBack()
	{
		FakeLink link = new();
		TimerClock clock = new();
		using BluetoothSession session = new(link);
		using VirtualXboxOne pad = new(autoTick: false);
		using ControllerBridge bridge = ImpulseBridge(session, pad, clock);
		byte[] fallback = TriggerEffects.Feedback(2, 5);
		bridge.ApplyProfile(new BridgeProfile(FallbackTrigger: fallback));
		AssertSent(link, fallback, fallback);

		bridge.PlayTriggerRumble(new TriggerRumble(50, 0));
		AssertSent(link, fallback, Vibration(50));
		clock.Advance(10);
		bridge.PlayTriggerRumble(new TriggerRumble(50, 75));
		AssertSent(link, Vibration(75), Vibration(50));

		// The game's main rumble meanwhile: its report carries the rumble, not the fallback, on both triggers.
		pad.Submit(Command(9, UsbIpCodes.DirOut, VirtualXboxOne.OutEndpoint, GipMotor(0x03, 0, 0, 60, 60, 255)), _ => { });
		AssertSent(link, Vibration(75), Vibration(50));

		clock.Advance(10);
		bridge.PlayTriggerRumble(new TriggerRumble(0, 75));
		AssertSent(link, Vibration(75), fallback);
		clock.Advance(10);
		bridge.PlayTriggerRumble(new TriggerRumble(0, 0));
		AssertSent(link, fallback, fallback);
		Assert.False(bridge.TriggersSeen);
		Assert.False(bridge.TriggerRumbleOn);
	}

	[Fact]
	public void TriggerRumble_WithoutAFallback_EndsWithTheTriggersOff()
	{
		FakeLink link = new();
		TimerClock clock = new();
		using BluetoothSession session = new(link);
		using VirtualXboxOne pad = new(autoTick: false);
		using ControllerBridge bridge = ImpulseBridge(session, pad, clock);
		bridge.PlayTriggerRumble(new TriggerRumble(30, 30));
		AssertSent(link, Vibration(30), Vibration(30));
		bridge.PlayTriggerRumble(default);
		// Within the rate cap: held back, then sent by the timer.
		AssertSent(link, Vibration(30), Vibration(30));
		clock.Advance(10);
		AssertSent(link, TriggerEffects.Off(), TriggerEffects.Off());
		Assert.Equal(0, clock.ActiveTimers);
	}

	[Fact]
	public void TriggerRumble_WritesOnlyWhenTheMappedEffectChanges()
	{
		FakeLink link = new();
		TimerClock clock = new();
		using BluetoothSession session = new(link);
		using VirtualXboxOne pad = new(autoTick: false);
		using ControllerBridge bridge = ImpulseBridge(session, pad, clock);

		int before = TriggerWrites(link);
		bridge.PlayTriggerRumble(new TriggerRumble(0, 0)); // nothing playing yet: nothing to write
		Assert.Equal(before, TriggerWrites(link));

		// A game moving the level every frame within one amplitude and pitch step (49-56 -> 5 at 60 Hz): one write.
		Assert.Single(Enumerable.Range(49, 8).Select(l => InstantImpulse.Map(l)).Distinct());
		for (int frame = 0; frame < 120; frame++)
		{
			bridge.PlayTriggerRumble(new TriggerRumble((byte)(49 + frame % 8), 0));
			clock.Advance(16);
		}
		Assert.Equal(before + 1, TriggerWrites(link));
		Assert.Equal(1, bridge.TriggerRumbleReports);
		Assert.Equal(121, bridge.TriggerRumbleChanges);

		bridge.PlayTriggerRumble(new TriggerRumble(100, 0));
		clock.Advance(16);
		bridge.PlayTriggerRumble(new TriggerRumble(95, 0));
		clock.Advance(16);
		Assert.Equal(before + 2, TriggerWrites(link));
		bridge.PlayTriggerRumble(new TriggerRumble(0, 0));
		clock.Advance(16);
		bridge.PlayTriggerRumble(new TriggerRumble(0, 0));
		clock.Advance(16);
		Assert.Equal(before + 3, TriggerWrites(link));
	}

	[Fact]
	public void TriggerRumble_SpinsUpAndDown_CappedAt100ReportsASecond_ThenTheTimerStops()
	{
		FakeLink link = new();
		TimerClock clock = new();
		using BluetoothSession session = new(link);
		using VirtualXboxOne pad = new(autoTick: false);
		using ControllerBridge bridge = ImpulseBridge(session, pad, clock, ImpulseTuning.XboxLike);

		int before = TriggerWrites(link);
		bridge.PlayTriggerRumble(new TriggerRumble(0, 100));
		Assert.Equal(before, TriggerWrites(link)); // the motor has not turned yet
		Assert.Equal(1, clock.ActiveTimers);
		clock.Advance(10);
		(int a, int hz) = bridge.TriggerRumbleNow.Right;
		Assert.True(a is > 0 and < 8 && hz < 90, $"first step {a} at {hz} Hz");
		clock.Advance(290);
		AssertSent(link, TriggerEffects.Vibration(0, 8, 90), TriggerEffects.Off());
		int up = TriggerWrites(link) - before;
		Assert.InRange(up, 4, 30);
		Assert.Equal(0, clock.ActiveTimers);

		bridge.PlayTriggerRumble(default);
		clock.Advance(300);
		AssertSent(link, TriggerEffects.Off(), TriggerEffects.Off());
		Assert.Equal(0, clock.ActiveTimers);
		Assert.False(bridge.TriggerRumbleOn);
		int all = TriggerWrites(link);
		clock.Advance(1000);
		Assert.Equal(all, TriggerWrites(link));
	}

	[Fact]
	public void TriggerRumble_FollowsTheLiveTuningFile()
	{
		string dir = Path.Combine(Path.GetTempPath(), "kopru-impulse-" + Guid.NewGuid().ToString("N"));
		string path = Path.Combine(dir, ImpulseTuning.FileName);
		try
		{
			InstantImpulse.Save(path);
			using ImpulseTuningFile file = new(path, poll: false);
			Assert.Equal(InstantImpulse, file.Current);
			FakeLink link = new();
			TimerClock clock = new();
			using BluetoothSession session = new(link);
			using VirtualXboxOne pad = new(autoTick: false);
			using ControllerBridge bridge = new(session, pad) { Time = clock, TriggerRumbleTuningSource = file };

			bridge.PlayTriggerRumble(new TriggerRumble(0, 75));
			AssertSent(link, TriggerEffects.Vibration(0, 6, 75), TriggerEffects.Off());
			clock.Advance(10);

			ImpulseTuning sharp = ImpulseTuning.Sharp with { AttackMs = 0, DecayMs = 0 };
			sharp.Save(path);
			File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5)); // a new time even on a coarse file system clock
			Assert.True(file.Reload());
			(int a, int hz) = sharp.Map(75);
			AssertSent(link, TriggerEffects.Vibration(0, a, hz), TriggerEffects.Off());
			Assert.False(file.Reload());

			File.WriteAllText(path, "{ broken");
			File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(10));
			Assert.False(file.Reload());
			Assert.Equal(sharp, file.Current);
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void TriggerRumble_FromThePad_StopsWhenTheCommandsTimeIsOver_AndTheBridgeEndingClearsIt()
	{
		long now = 0;
		FakeLink link = new();
		TimerClock clock = new();
		using BluetoothSession session = new(link);
		using VirtualXboxOne pad = new(nowMs: () => now, autoTick: false);
		byte[] fallback = TriggerEffects.Vibration(2, 3, 30);
		ControllerBridge bridge = ImpulseBridge(session, pad, clock);
		bridge.ApplyProfile(new BridgeProfile(FallbackTrigger: fallback));

		// Left trigger at 50 % for 100 ms (trigger bits only: no main motor report).
		int before = TriggerWrites(link);
		pad.Submit(Command(9, UsbIpCodes.DirOut, VirtualXboxOne.OutEndpoint, GipMotor(0x08, 50, 0, 0, 0, 10)), _ => { });
		Assert.Equal(before + 1, TriggerWrites(link));
		AssertSent(link, fallback, Vibration(50));
		now = 99;
		clock.Advance(99);
		pad.Tick();
		Assert.Equal(before + 1, TriggerWrites(link));
		now = 100;
		clock.Advance(1);
		pad.Tick();
		Assert.Equal(before + 2, TriggerWrites(link));
		AssertSent(link, fallback, fallback);

		// A level-0 command stops it at once.
		clock.Advance(10);
		pad.Submit(Command(10, UsbIpCodes.DirOut, VirtualXboxOne.OutEndpoint, GipMotor(0x04, 0, 80, 0, 0, 255)), _ => { });
		AssertSent(link, Vibration(80), fallback);
		clock.Advance(10);
		pad.Submit(Command(11, UsbIpCodes.DirOut, VirtualXboxOne.OutEndpoint, GipMotor(0x04, 0, 0, 0, 0, 255)), _ => { });
		AssertSent(link, fallback, fallback);

		// Still on when the bridge ends (a pad swap, a disconnect): taken off the trigger.
		clock.Advance(10);
		pad.Submit(Command(12, UsbIpCodes.DirOut, VirtualXboxOne.OutEndpoint, GipMotor(0x04, 0, 80, 0, 0, 255)), _ => { });
		AssertSent(link, Vibration(80), fallback);
		bridge.Dispose();
		AssertSent(link, fallback, fallback);
		int after = TriggerWrites(link);
		pad.Submit(Command(13, UsbIpCodes.DirOut, VirtualXboxOne.OutEndpoint, GipMotor(0x04, 0, 100, 0, 0, 255)), _ => { });
		clock.Advance(100);
		Assert.Equal(after, TriggerWrites(link));
		Assert.Equal(0, clock.ActiveTimers);
	}

	[Fact]
	public void TriggerRumble_AModsTriggerEffectStaysOnTop()
	{
		FakeLink link = new();
		TimerClock clock = new();
		using BluetoothSession session = new(link);
		using VirtualXboxOne pad = new(autoTick: false);
		using ControllerBridge bridge = ImpulseBridge(session, pad, clock);
		byte[] modRight = TriggerEffects.Weapon(2, 6, 8);
		bridge.SetModOutput(new Mods.ModOutput { RightTrigger = modRight });
		bridge.PlayTriggerRumble(new TriggerRumble(50, 50));
		AssertSent(link, modRight, Vibration(50));
	}
}
