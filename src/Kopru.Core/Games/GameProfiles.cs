// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Protocol;

namespace Kopru.Core.Games;

// The two games list pages (the catalog of DualSense games and the other games). Only the pages use it: since the DualSense
// Mode switch is gone the bridge runs one profile for every game (GameProfile), and GameModeState.Mode is always Supported.
public enum GameMode
{
	Unsupported,

	Supported
}

// The per-exe choice older versions saved (read only to migrate settings: a Supported game the catalog does not know
// becomes one of the user's games).
public enum GameOverride
{
	Auto,
	Supported,
	Unsupported
}

// A trigger effect for games that send none.
public enum FallbackTrigger
{
	Off,
	Feedback,
	Weapon,
	Vibration
}

// Where the fallback effect sits on the trigger's travel, per effect: the zone it starts at (0-9, ten zones: zone n is n x
// 10 % of the travel), the weapon's end zone, and the vibration's frequency. One shape for both triggers today; a later
// left/right split keeps one of these per trigger. The defaults are the effects the app always sent (zone 2, weapon to
// zone 6, 30 Hz). Fields missing from older saved profiles read as the defaults.
public sealed record FallbackShape
{
	public const int MinStart = 0;
	public const int MaxStart = 9;
	public const int MinWeaponStart = 2;
	public const int MaxWeaponStart = 7;
	public const int MinWeaponEnd = 3;
	public const int MaxWeaponEnd = 8;
	public const int MinFrequency = 5;
	public const int MaxFrequency = 60;

	public static FallbackShape Default { get; } = new();

	public int FeedbackStart { get; init; } = 2;
	public int WeaponStart { get; init; } = 2;
	public int WeaponEnd { get; init; } = 6;
	public int VibrationStart { get; init; } = 2;
	public int VibrationFrequency { get; init; } = 30;
	// Force in percent (0-100, mapped onto the effects' 1-8 steps): a Custom Trigger's own strength. The profile's fallback
	// effect keeps its Effect Strength (FallbackStrength) and leaves this alone.
	public double Force { get; init; } = DefaultForce;

	public const double DefaultForce = 50;

	// Every value in its range, the weapon's end after its start (an end at or before the start moves to start + 1).
	public FallbackShape Normalized()
	{
		int weaponStart = Math.Clamp(WeaponStart, MinWeaponStart, MaxWeaponStart);
		FallbackShape n = new()
		{
			FeedbackStart = Math.Clamp(FeedbackStart, MinStart, MaxStart),
			WeaponStart = weaponStart,
			WeaponEnd = Math.Clamp(WeaponEnd, weaponStart + 1, MaxWeaponEnd),
			VibrationStart = Math.Clamp(VibrationStart, MinStart, MaxStart),
			VibrationFrequency = Math.Clamp(VibrationFrequency, MinFrequency, MaxFrequency),
			Force = Math.Clamp(double.IsFinite(Force) ? Force : DefaultForce, 0, 100)
		};
		return n == this ? this : n;
	}

	// A new weapon start (clamped); the end moves up with it when it would no longer be after it.
	public FallbackShape WithWeaponStart(int start)
	{
		int s = Math.Clamp(start, MinWeaponStart, MaxWeaponStart);
		return (this with { WeaponStart = s, WeaponEnd = Math.Max(WeaponEnd, s + 1) }).Normalized();
	}

	// A new weapon end (clamped); the start moves down with it when it would no longer be before it.
	public FallbackShape WithWeaponEnd(int end)
	{
		int e = Math.Clamp(end, MinWeaponEnd, MaxWeaponEnd);
		return (this with { WeaponEnd = e, WeaponStart = Math.Min(WeaponStart, e - 1) }).Normalized();
	}
}

// One trigger's Custom Trigger (per profile, L2 and R2 apart). Off is "Game's Effect": nothing over what the game sends.
// Another effect goes on the trigger whatever the game sends (or when it sends nothing), in its shape at its own Force;
// the trigger strength (TriggerIntensity) does not scale it. Missing in older profiles: Game's Effect.
public sealed record CustomTrigger
{
	public static CustomTrigger Default { get; } = new();

	public FallbackTrigger Effect { get; init; } = FallbackTrigger.Off;
	public FallbackShape Shape { get; init; } = FallbackShape.Default;

	public bool IsSet => Effect != FallbackTrigger.Off && Enum.IsDefined(Effect);

	// A known effect and every value of the shape in its range.
	public CustomTrigger Normalized()
	{
		CustomTrigger n = new()
		{
			Effect = Enum.IsDefined(Effect) ? Effect : FallbackTrigger.Off,
			Shape = (Shape ?? FallbackShape.Default).Normalized()
		};
		return n == this ? this : n;
	}

	// The effect bytes, or null for Game's Effect.
	public byte[]? Bytes() => IsSet ? GameProfiles.Effect(Effect, (Shape ?? FallbackShape.Default).Normalized().Force / 100, Shape) : null;
}

// The one profile every game runs with. Whatever the game sends passes through: its haptics at GameHapticsLevel, its trigger
// effects scaled by TriggerIntensity (0..1), its light when LightFromGame is on. The classic rumble settings (RumbleLevel,
// Feel) and the fallback trigger effect (Trigger, FallbackStrength 0..1, Shape) apply only where the game sends nothing of its
// own.
public sealed record GameProfile(
	double GameHapticsLevel = HapticsConverter.DefaultLevel,
	double TriggerIntensity = 1,
	bool LightFromGame = true,
	double RumbleLevel = ControllerBridge.DefaultRumbleLevel,
	RumbleFeel Feel = RumbleFeel.Soft,
	FallbackTrigger Trigger = FallbackTrigger.Off,
	double FallbackStrength = 0.5)
{
	// "Turn sound into haptics" for this game (AudioHapticsRunner; not part of what the bridge runs with).
	public Audio.AudioHapticsSettings AudioHaptics { get; init; } = Audio.AudioHapticsSettings.Default;

	// How the Xbox Series pad's trigger rumble feels (only that pad sends it).
	public TriggerRumbleFeel TriggerRumble { get; init; } = TriggerRumbleFeel.XboxLike;

	// Where the fallback effect sits on the trigger's travel (and the vibration's frequency).
	public FallbackShape Shape { get; init; } = FallbackShape.Default;

	// Custom Trigger per trigger: over whatever the game sends (Off: Game's Effect).
	public CustomTrigger LeftCustom { get; init; } = CustomTrigger.Default;
	public CustomTrigger RightCustom { get; init; } = CustomTrigger.Default;

	// The fallback effect bytes, or null when it is off.
	public byte[]? TriggerEffect => Trigger == FallbackTrigger.Off ? null : GameProfiles.Effect(Trigger, FallbackStrength, Shape);
}

public static class GameProfiles
{
	// An effect in the default shape (from zone 2; the controller's output tests), strength 0..1 mapped onto the effects'
	// 1-8 steps.
	public static byte[] Effect(FallbackTrigger trigger, double strength) => Effect(trigger, strength, FallbackShape.Default);

	// An effect in the given shape (null: the default one), strength 0..1 mapped onto the effects' 1-8 steps.
	public static byte[] Effect(FallbackTrigger trigger, double strength, FallbackShape? shape)
	{
		int step = Math.Clamp((int)Math.Round(8 * strength), 1, 8);
		FallbackShape s = (shape ?? FallbackShape.Default).Normalized();
		return trigger switch
		{
			FallbackTrigger.Feedback => TriggerEffects.Feedback(s.FeedbackStart, step),
			FallbackTrigger.Weapon => TriggerEffects.Weapon(s.WeaponStart, s.WeaponEnd, step),
			FallbackTrigger.Vibration => TriggerEffects.Vibration(s.VibrationStart, step, s.VibrationFrequency),
			_ => TriggerEffects.Off()
		};
	}

	// What the bridge runs with. gameSession: GameModeController's count of game sessions (a new one lets the fallback
	// trigger effect back on until the next game sends its first effect; see ControllerBridge.FallbackTrigger).
	public static BridgeProfile For(GameProfile profile, long gameSession = 0) => new(profile.GameHapticsLevel, profile.RumbleLevel,
		profile.Feel, Math.Clamp(profile.TriggerIntensity, 0, 1), profile.LightFromGame, profile.TriggerEffect, gameSession,
		Enum.IsDefined(profile.TriggerRumble) ? profile.TriggerRumble : TriggerRumbleFeel.XboxLike)
	{
		CustomLeftTrigger = (profile.LeftCustom ?? CustomTrigger.Default).Bytes(),
		CustomRightTrigger = (profile.RightCustom ?? CustomTrigger.Default).Bytes()
	};
}
