// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Protocol;

namespace Kopru.Core.Bridge;

// How classic rumble feels when it is played as haptics.
public enum RumbleFeel
{
	// Smooth sine tones: the heavy motor at 80 Hz, the light one at 160 Hz.
	Soft,

	// Higher, squarer tones (125 / 250 Hz) with a short kick: full strength for 60 ms after the motor speeds up, then
	// 70 %. Sharper, shorter-feeling hits.
	Punchy
}

// What the bridge does with the game's output. GameHapticsLevel and RumbleLevel: 1.0 = as sent. TriggerIntensity
// 0..1 scales the game's trigger effects (TriggerEffects.Scale). LightFromGame false keeps the user's light.
// FallbackTrigger: the effect put on a trigger the game has not set, until the game sends its first real trigger effect
// (null = leave them). GameSession: a new value starts a new game session for that rule (GameModeController counts them).
public sealed record BridgeProfile(
	double GameHapticsLevel = HapticsConverter.DefaultLevel,
	double RumbleLevel = ControllerBridge.DefaultRumbleLevel,
	RumbleFeel Feel = RumbleFeel.Soft,
	double TriggerIntensity = 1,
	bool LightFromGame = true,
	byte[]? FallbackTrigger = null,
	long GameSession = 0)
{
	public static BridgeProfile Default { get; } = new();

	// Equal values, the fallback bytes compared by content.
	public bool SameAs(BridgeProfile other) =>
		this with { FallbackTrigger = null } == other with { FallbackTrigger = null }
		&& (FallbackTrigger ?? []).AsSpan().SequenceEqual(other.FallbackTrigger ?? []);
}

// What the game sent since the last look: non-silent haptics audio, a trigger effect other than off, rumble.
public readonly record struct GameSignals(bool Haptics, bool Triggers, bool Rumble)
{
	public bool IsNative => Haptics || Triggers;
}

// A strong sign that a program with the virtual pad open started using the controller as a game does
// (ControllerBridge.GameUseStarted). Weak signs (a steady output stream, light bar writes, the audio interface opened) are
// not among them: Steam and Windows produce those without a game.
public enum GameUse
{
	// A trigger effect other than off.
	TriggerEffect,
	// Haptics audio above silence on channels 3-4.
	Haptics,
	// The rumble motors running.
	Rumble
}

// A bridge's game use edge state (ControllerBridge.CaptureGameUse / SeedGameUse): trigger effect on, motors running, the
// last haptics sound (Stopwatch timestamp), and whether the game sent a real trigger effect in this game session (then
// the fallback trigger effect stays off; TriggerSession is the BridgeProfile.GameSession it was seen in).
public readonly record struct GameUseState(bool Triggers, bool Rumble, long HapticsAt, bool TriggersSeen = false, long TriggerSession = 0);

public static class GameUseNames
{
	// The name in log lines.
	public static string LogName(this GameUse use) => use switch
	{
		GameUse.TriggerEffect => "trigger effect",
		GameUse.Haptics => "haptics",
		_ => "rumble"
	};
}

// The bridge as GameModeController sees it (BridgeHost; tests use a fake).
public interface IGameBridge
{
	void ApplyProfile(BridgeProfile profile);

	GameSignals TakeGameSignals();

	// The virtual controller type games get (GameModeController decides; see GameModePadTypes); bridges without emulation
	// ignore it.
	void UsePad(Kopru.Core.Virtual.VirtualPadType type)
	{
	}

	// The active game ended (exited or another took over): the bridge hands the light back to the user.
	void GameEnded()
	{
	}
}

// Plays the two rumble motors as haptics tones, one 3 kHz frame at a time. Not thread-safe.
public sealed class RumbleSynth
{
	public const double SoftLeftHz = 80;
	public const double SoftRightHz = 160;
	public const double PunchyLeftHz = 125;
	public const double PunchyRightHz = 250;
	public const int KickFrames = HapticsConverter.OutputRate * 60 / 1000;
	public const double PunchyHold = 0.7;

	private double leftPhase, rightPhase;
	private byte lastLeft, lastRight;
	private int leftKick, rightKick;

	public (sbyte Left, sbyte Right) Next(byte leftMotor, byte rightMotor, double level, RumbleFeel feel)
	{
		if (leftMotor > lastLeft)
		{
			leftKick = KickFrames;
		}
		if (rightMotor > lastRight)
		{
			rightKick = KickFrames;
		}
		lastLeft = leftMotor;
		lastRight = rightMotor;
		if (leftMotor == 0 && rightMotor == 0)
		{
			leftPhase = rightPhase = 0;
			leftKick = rightKick = 0;
			return (0, 0);
		}
		bool punchy = feel == RumbleFeel.Punchy;
		double left = leftMotor / 255.0 * level * Wave(leftPhase, punchy) * Envelope(ref leftKick, punchy);
		double right = rightMotor / 255.0 * level * Wave(rightPhase, punchy) * Envelope(ref rightKick, punchy);
		leftPhase = (leftPhase + (punchy ? PunchyLeftHz : SoftLeftHz) / HapticsConverter.OutputRate) % 1;
		rightPhase = (rightPhase + (punchy ? PunchyRightHz : SoftRightHz) / HapticsConverter.OutputRate) % 1;
		return (Clamp(left * 127), Clamp(right * 127));
	}

	private static double Wave(double phase, bool punchy)
	{
		double s = Math.Sin(2 * Math.PI * phase);
		return punchy ? Math.Tanh(2.5 * s) / Math.Tanh(2.5) : s;
	}

	private static double Envelope(ref int kick, bool punchy)
	{
		if (!punchy)
		{
			return 1;
		}
		if (kick > 0)
		{
			kick--;
			return 1;
		}
		return PunchyHold;
	}

	private static sbyte Clamp(double v) => (sbyte)Math.Clamp((int)v, -127, 127);
}
