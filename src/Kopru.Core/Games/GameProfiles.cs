// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Protocol;

namespace Kopru.Core.Games;

// How the bridge treats the game in the foreground.
public enum GameMode
{
	// Only classic rumble: rumble is played as haptics with the user's feel, fallback trigger effect, the user's light.
	Unsupported,

	// Native DualSense: the game's haptics audio, its trigger effects (scaled) and, optionally, its light.
	Supported
}

// The per-exe choice older versions saved (read only to migrate settings: Supported -> always, Unsupported -> never ask).
public enum GameOverride
{
	Auto,
	Supported,
	Unsupported
}

// Why the supported mode switch is where it is.
public enum ModeReason
{
	// Off: the other games profile.
	Default,
	// The user flipped the switch.
	User,
	// Turned on for the game in front ("Turn On" on the question).
	Game,
	// Turned on by itself: the game is on the "always" list.
	Always
}

// The answer to "turn on DualSense supported mode?" when a known game starts.
public enum AskAnswer
{
	TurnOn,
	NotNow,
	Always
}

// A trigger effect for games that send none.
public enum FallbackTrigger
{
	Off,
	Feedback,
	Weapon,
	Vibration
}

// Settings for games with native DualSense support. TriggerIntensity 0..1 scales the game's trigger effects.
public sealed record SupportedProfile(
	double GameHapticsLevel = HapticsConverter.DefaultLevel,
	double TriggerIntensity = 1,
	bool LightFromGame = true);

// Settings for games that only send classic rumble. FallbackStrength 0..1.
public sealed record UnsupportedProfile(
	double RumbleLevel = ControllerBridge.DefaultRumbleLevel,
	RumbleFeel Feel = RumbleFeel.Soft,
	FallbackTrigger Trigger = FallbackTrigger.Off,
	double FallbackStrength = 0.5)
{
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

	// What the bridge runs with in the given mode. Game haptics keep their level in both modes (a misjudged native game
	// still plays its haptics), the rumble level and feel too; the rest depends on the mode.
	public static BridgeProfile For(GameMode mode, SupportedProfile supported, UnsupportedProfile unsupported) => mode == GameMode.Supported
		? new BridgeProfile(supported.GameHapticsLevel, unsupported.RumbleLevel, unsupported.Feel, supported.TriggerIntensity,
			supported.LightFromGame, null)
		: new BridgeProfile(supported.GameHapticsLevel, unsupported.RumbleLevel, unsupported.Feel, 1, false, unsupported.TriggerEffect);
}
