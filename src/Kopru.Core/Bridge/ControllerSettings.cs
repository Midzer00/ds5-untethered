// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;
using Kopru.Core.Input;

namespace Kopru.Core.Bridge;

// User-adjustable bridge settings the UI pushes to the service. Levels are percentages. GameHapticsLevel and
// TriggerStrength (0-100 %) set how strong the game's own haptics and trigger effects feel; RumbleLevel, RumbleFeel and the
// fallback trigger apply where the game sends only classic rumble or no trigger effect; LightFromGame shows the game's
// colour when it sends one. Together they are the one game profile (Game).
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
	// The profile every game runs with (LightFromGame is the only switch for the game's light).
	public GameProfile Game => new(GameHapticsLevel / 100, Math.Clamp(TriggerStrength / 100, 0, 1), LightFromGame,
		RumbleLevel / 100, RumbleFeel, FallbackTrigger, Math.Clamp(FallbackStrength / 100, 0, 1))
	{
		AudioHaptics = new Kopru.Core.Audio.AudioHapticsSettings(AudioHapticsOn, Math.Clamp(AudioHapticsStrength / 100, 0, Kopru.Core.Audio.AudioHapticsDsp.MaxStrength),
			AudioHapticsBand, Kopru.Core.Audio.AudioHapticsDsp.ClampSensitivity(AudioHapticsSensitivity)),
		TriggerRumble = TriggerRumble,
		Shape = (FallbackShape ?? FallbackShape.Default).Normalized(),
		LeftCustom = (LeftCustomTrigger ?? CustomTrigger.Default).Normalized(),
		RightCustom = (RightCustomTrigger ?? CustomTrigger.Default).Normalized()
	};

	// Custom Trigger per trigger (Game's Effect: nothing over the game).
	public CustomTrigger LeftCustomTrigger { get; init; } = CustomTrigger.Default;
	public CustomTrigger RightCustomTrigger { get; init; } = CustomTrigger.Default;

	// Where the fallback trigger effect sits on the trigger's travel (and the vibration's frequency).
	public FallbackShape FallbackShape { get; init; } = FallbackShape.Default;

	// How the Xbox Series pad's trigger rumble feels (Off, Xbox-like, Soft, Sharp).
	public TriggerRumbleFeel TriggerRumble { get; init; } = TriggerRumbleFeel.XboxLike;

	// "Turn sound into haptics" (the game's sound felt on the controller; AudioHapticsRunner): on, strength 0-200 %, band,
	// sensitivity 0-100 % (where the noise gate opens).
	public bool AudioHapticsOn { get; init; }
	public double AudioHapticsStrength { get; init; } = 100;
	public Kopru.Core.Audio.AudioHapticsBand AudioHapticsBand { get; init; } = Kopru.Core.Audio.AudioHapticsBand.Bass;
	public double AudioHapticsSensitivity { get; init; } = Kopru.Core.Audio.AudioHapticsDsp.DefaultSensitivity;

	// Light effect, player LED pattern and brightness, mute LED.
	public Kopru.Core.Lighting.LightOptions LightOptions { get; init; } = Kopru.Core.Lighting.LightOptions.Default;

	public LightSettings Light => LightSettings.FromRgb(LightbarColour, Brightness / 100, PlayerLeds) with { Options = LightOptions };

	// Stick and trigger tuning applied to the input games receive.
	public InputTuning Tuning { get; init; } = InputTuning.Default;

	// Button remapping, stick swap/invert and gyro aiming (see InputTransform for the order).
	public RemapSettings Remap { get; init; } = RemapSettings.Default;
	public GyroSettings Gyro { get; init; } = GyroSettings.Default;

	// Turn the controller off (not just report it) after IdlePowerOffMinutes without use.
	public bool PowerOffWhenIdle { get; init; }

	// Controller shortcuts with the PS button held (see ShortcutProcessor).
	public bool ShortcutsEnabled { get; init; } = true;

	// A single short PS tap with no game in front brings the app's window up (PsTapRule; under ShortcutsEnabled).
	public bool PsOpensApp { get; init; } = true;

	// "Also In Games": with PsOpensApp on, the tap opens the app while a game is in front too (PsTapRule).
	public bool PsOpensAppInGames { get; init; } = true;

	// The controller speaker: the one switch (plays what the game sends to the controller's speaker; only a virtual
	// DualSense has one). Off in this record's default; the app's setting is on by default.
	public SpeakerSettings Speaker { get; init; } = SpeakerSettings.Off;
	// Desktop mode: on (the controller drives Windows, games get a neutral pad) and its tuning.
	public bool DesktopModeOn { get; init; }
	public DesktopModeSettings Desktop { get; init; } = DesktopModeSettings.Default;

	// Controller microphone (mute button, experimental passthrough; off by default).
	public MicSettings Mic { get; init; } = MicSettings.Off;

	// DSX mod support: listen for game mods made for DSX on 127.0.0.1 (Mods.DsxListener); off by default.
	public bool DsxModSupport { get; init; }

	// "Pause Haptics In Silence" (app setting, not per profile; off by default): no haptics reports while the game is
	// silent (BluetoothSession.PauseHapticsInSilence).
	public bool PauseHapticsInSilence { get; init; }

	// What the bridge does while the controller's USB cable is plugged in (app setting, not per profile): ChargeOnly
	// hides the USB twin and keeps everything on Bluetooth, Wired moves input, output and audio onto the cable. This
	// record's default and the app's (SettingsState.DefaultCableMode) are both ChargeOnly: wired mode is opt-in.
	public CableMode CableMode { get; init; } = CableMode.ChargeOnly;

	// Low Power mode turns the pause on as well while it is active (LowPower.Apply), without changing the setting.
	public bool PauseHapticsForLowPower { get; init; }

	// Whether the pause is on, and why (for the log): "setting", "low power" or null when off.
	public string? PauseHapticsReason => PauseHapticsInSilence ? "setting" : PauseHapticsForLowPower ? "low power" : null;
}
