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

// The one profile every game runs with. Whatever the game sends passes through: its haptics at GameHapticsLevel, its trigger
// effects scaled by TriggerIntensity (0..1), its light when LightFromGame is on. The classic rumble settings (RumbleLevel,
// Feel) and the fallback trigger effect (Trigger, FallbackStrength 0..1) apply only where the game sends nothing of its own.
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

	// The fallback effect bytes, or null when it is off.
	public byte[]? TriggerEffect => Trigger == FallbackTrigger.Off ? null : GameProfiles.Effect(Trigger, FallbackStrength);
}

public static class GameProfiles
{
	// An effect starting at zone 2, strength 0..1 mapped onto the effects' 1-8 steps.
	public static byte[] Effect(FallbackTrigger trigger, double strength)
	{
		int step = Math.Clamp((int)Math.Round(8 * strength), 1, 8);
		return trigger switch
		{
			FallbackTrigger.Feedback => TriggerEffects.Feedback(2, step),
			FallbackTrigger.Weapon => TriggerEffects.Weapon(2, 6, step),
			FallbackTrigger.Vibration => TriggerEffects.Vibration(2, step, 30),
			_ => TriggerEffects.Off()
		};
	}

	// What the bridge runs with. gameSession: GameModeController's count of game sessions (a new one lets the fallback
	// trigger effect back on until the next game sends its first effect; see ControllerBridge.FallbackTrigger).
	public static BridgeProfile For(GameProfile profile, long gameSession = 0) => new(profile.GameHapticsLevel, profile.RumbleLevel,
		profile.Feel, Math.Clamp(profile.TriggerIntensity, 0, 1), profile.LightFromGame, profile.TriggerEffect, gameSession,
		Enum.IsDefined(profile.TriggerRumble) ? profile.TriggerRumble : TriggerRumbleFeel.XboxLike);
}
