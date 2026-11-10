// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;
using Kopru.Core.Input;

namespace Kopru.Core.Bridge;

// User-adjustable bridge settings the UI pushes to the service. Levels are percentages. GameHapticsLevel,
// TriggerStrength (the game's trigger effects, 0-100 %) and LightFromGame are the supported games profile; RumbleLevel,
// RumbleFeel and the fallback trigger the profile for other games.
public sealed record ControllerSettings(
	double GameHapticsLevel = 150,
	double RumbleLevel = 100,
	double TriggerStrength = 100,
	uint LightbarColour = 0x2F6BFF,
	double Brightness = 80,
	bool PlayerLeds = true,
	double IdlePowerOffMinutes = 15,
	bool LightFromGame = true,
	RumbleFeel RumbleFeel = RumbleFeel.Soft,
	FallbackTrigger FallbackTrigger = FallbackTrigger.Off,
	double FallbackStrength = 50)
{
	// The game controlled light mode always lets a supported game's light through.
	public SupportedProfile Supported => new(GameHapticsLevel / 100, Math.Clamp(TriggerStrength / 100, 0, 1),
		LightFromGame || LightOptions.Mode == Kopru.Core.Lighting.LightMode.Game);

	// Light effect, player LED pattern and brightness, mute LED.
	public Kopru.Core.Lighting.LightOptions LightOptions { get; init; } = Kopru.Core.Lighting.LightOptions.Default;

	public LightSettings Light => LightSettings.FromRgb(LightbarColour, Brightness / 100, PlayerLeds) with { Options = LightOptions };

	public UnsupportedProfile Unsupported => new(RumbleLevel / 100, RumbleFeel, FallbackTrigger, Math.Clamp(FallbackStrength / 100, 0, 1));

	// Stick and trigger tuning applied to the input games receive.
	public InputTuning Tuning { get; init; } = InputTuning.Default;

	// Button remapping, stick swap/invert and gyro aiming (see InputTransform for the order).
	public RemapSettings Remap { get; init; } = RemapSettings.Default;
	public GyroSettings Gyro { get; init; } = GyroSettings.Default;

	// Turn the controller off (not just report it) after IdlePowerOffMinutes without use.
	public bool PowerOffWhenIdle { get; init; }

	// Controller shortcuts with the PS button held (see ShortcutProcessor).
	public bool ShortcutsEnabled { get; init; } = true;

	// Experimental controller speaker (game audio to the controller; off by default).
	public SpeakerSettings Speaker { get; init; } = SpeakerSettings.Off;
	// Desktop mode: on (the controller drives Windows, games get a neutral pad) and its tuning.
	public bool DesktopModeOn { get; init; }
	public DesktopModeSettings Desktop { get; init; } = DesktopModeSettings.Default;

	// Controller microphone (mute button, experimental passthrough; off by default).
	public MicSettings Mic { get; init; } = MicSettings.Off;
}
