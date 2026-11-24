// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// When the game's classic rumble stops. The motors are played as haptics for as long as they are not zero, so a last
// non-zero value nobody takes back would vibrate the controller until it is turned off. It stops when:
//  - a game report with the rumble bits sets the motors to zero (a report without the bits leaves them as they are: the
//    bits are latched state, and games may send their light or triggers in reports of their own);
//  - on the virtual DualSense, a report without the rumble bits came and none with them followed for BitsOffHold (some
//    game input layers stop rumble by leaving the bits out; while rumble runs they send every report with the bits);
//  - no game report carried the rumble for the hold limit: RumbleHoldLimit on the virtual DualSense, whose senders resend
//    their state; LongRumbleHoldLimit on the Xbox 360, Xbox Series and DualShock 4 pads, where an XInput game may set a
//    rumble once;
//  - the active game exited with no other game taking over (GameModeController), the host released the virtual pad (USB/IP
//    detach), or the bridge stops. These also take the Xbox Series pad's trigger rumble off the triggers at once
//    (StopTriggerRumble, ControllerBridgeImpulse).
// The timed stops fade out (RumbleFadeTime, BitsOffFadeTime), then zero the motors. With the motors at zero the rumble
// refill stops topping the haptics stream up, so the stream ends after its silent tail.
public sealed partial class ControllerBridge
{
	// Virtual DualSense: no game report carrying the rumble for this long and the rumble fades out. Games that rumble
	// resend the motors far more often (every frame, or at least on every change; Steam Input resends a held rumble every
	// 2 s), so only a sender that went away leaves a non-zero value standing this long.
	public static readonly TimeSpan RumbleHoldLimit = TimeSpan.FromSeconds(10);

	// Xbox 360, Xbox Series and DualShock 4 pads: a long safety limit only (an XInput game may set a constant rumble once and leave it).
	public static readonly TimeSpan LongRumbleHoldLimit = TimeSpan.FromSeconds(60);

	// Virtual DualSense: after a report without the rumble bits, one with them must follow within this time or the rumble
	// fades out (over BitsOffFadeTime).
	public static readonly TimeSpan BitsOffHold = TimeSpan.FromSeconds(1);

	// The fade after the hold limit, and the shorter one after BitsOffHold, before the motors are zeroed.
	public static readonly TimeSpan RumbleFadeTime = TimeSpan.FromSeconds(1);
	public static readonly TimeSpan BitsOffFadeTime = TimeSpan.FromMilliseconds(250);

	// Where the rumble stop lines go (tests replace it).
	public Action<string> RumbleLog { get; set; } = Diagnostics.AppLog.Write;

	// When a game report last carried the rumble, and when the first one without the bits came after it (0: none since).
	// Time's timestamp units.
	private long lastRumbleReport = long.MinValue / 2;
	private long bitsOffSince;

	private long rumbleStops;

	// Times running motors were stopped by something other than a game report with motors (diagnostics, tests).
	public long RumbleStops => Interlocked.Read(ref rumbleStops);

	// The no-report hold limit of this bridge's pad type.
	public TimeSpan HoldLimit => pad.Type == VirtualPadType.DualSense ? RumbleHoldLimit : LongRumbleHoldLimit;

	// The rumble bits of an output block: the classic motors (or the improved rumble) are set by this report.
	internal static bool HasRumbleBits(ReadOnlySpan<byte> block) =>
		(block[OutputBlock.Flag0] & (OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect)) != 0
		|| (block[OutputBlock.Flag2] & OutputBlock.ImprovedRumble) != 0;

	// 1 while the rumble is held, then down to 0 over the fade of whichever stop comes first.
	public double RumbleGain => GainAndCause().Gain;

	private (double Gain, bool BitsOff) GainAndCause()
	{
		long now = Time.GetTimestamp();
		double gain = Fade(Time.GetElapsedTime(Interlocked.Read(ref lastRumbleReport), now), HoldLimit, RumbleFadeTime);
		long off = Interlocked.Read(ref bitsOffSince);
		if (off != 0)
		{
			double offGain = Fade(Time.GetElapsedTime(off, now), BitsOffHold, BitsOffFadeTime);
			if (offGain < gain)
			{
				return (offGain, true);
			}
		}
		return (gain, false);
	}

	private static double Fade(TimeSpan quiet, TimeSpan hold, TimeSpan fade) =>
		quiet <= hold ? 1 : Math.Clamp(1 - (quiet - hold) / fade, 0, 1);

	// Every game report (repeats too: they are dropped before the motors are looked at, but still show what the game holds).
	// With the bits: the rumble is held. Without them on the virtual DualSense: the short hold starts, unless it runs already.
	private void NoteRumbleReport(ReadOnlySpan<byte> block)
	{
		if (HasRumbleBits(block))
		{
			Interlocked.Exchange(ref lastRumbleReport, Time.GetTimestamp());
			Interlocked.Exchange(ref bitsOffSince, 0);
		}
		else if (pad.Type == VirtualPadType.DualSense && Interlocked.Read(ref bitsOffSince) == 0)
		{
			Interlocked.CompareExchange(ref bitsOffSince, Math.Max(1, Time.GetTimestamp()), 0);
		}
	}

	// The motors a new game report asks for, or null when it leaves them as they are (no rumble bits).
	private static (byte Left, byte Right)? MotorsAsked(byte[] sent) =>
		HasRumbleBits(sent) ? (sent[OutputBlock.MotorLeft], sent[OutputBlock.MotorRight]) : null;

	// Zeroes running motors for `why` (one log line; nothing when they already stand still). The game's next report goes
	// through even if it repeats the last one, so a game still holding a rumble gets it back at once.
	public bool StopRumble(string why)
	{
		byte left, right;
		lock (rumbleGate)
		{
			(left, right) = (leftMotor, rightMotor);
			if (left == 0 && right == 0)
			{
				return false;
			}
			leftMotor = rightMotor = 0;
			gameRumbleOn = false;
		}
		lock (lastBlock)
		{
			resendNext = true;
		}
		Interlocked.Increment(ref rumbleStops);
		// A pad that reports only changes (Xbox Series) forgets the levels too, or a held rumble resent would not come back.
		(pad as IChangeOnlyMotors)?.ForgetMotors(main: true, triggers: false);
		RumbleLog($"rumble{LogSlot}: stopped ({why}); motors were {left}/{right}");
		return true;
	}

	// The active game exited with no other game taking over, or the host released the pad: the motors stop, and so does a
	// trigger rumble (Xbox Series), the triggers getting back what they have without it.
	public bool StopAllRumble(string why)
	{
		bool motors = StopRumble(why);
		bool triggers = StopTriggerRumble(why);
		return motors || triggers;
	}

	// The monitor's pass (BridgeHost) and the rumble refill: zeroes motors whose fade is over.
	public void CheckRumbleStale()
	{
		if (Motors == (0, 0))
		{
			return;
		}
		(double gain, bool bitsOff) = GainAndCause();
		if (gain <= 0)
		{
			StopRumble(bitsOff
				? string.Create(CultureInfo.InvariantCulture, $"a game report without the rumble bits, none with them for {BitsOffHold.TotalSeconds:0.#} s, faded out")
				: string.Create(CultureInfo.InvariantCulture, $"no rumble report from the game for {HoldLimit.TotalSeconds:0} s, faded out"));
		}
	}
}
