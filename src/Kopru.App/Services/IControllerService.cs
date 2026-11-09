// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Games;
using Kopru.Core.Input;
using Kopru.Core.Steam;

namespace Kopru.App.Services;

public enum ControllerKind
{
	None,
	DualSense,
	DualSenseEdge
}

public enum HapticSide
{
	Left,
	Right,
	Both
}

public enum TriggerTestEffect
{
	Off,
	Feedback,
	Weapon,
	Vibration
}

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
	public SupportedProfile Supported => new(GameHapticsLevel / 100, Math.Clamp(TriggerStrength / 100, 0, 1), LightFromGame);

	public UnsupportedProfile Unsupported => new(RumbleLevel / 100, RumbleFeel, FallbackTrigger, Math.Clamp(FallbackStrength / 100, 0, 1));

	// Stick and trigger tuning applied to the input games receive.
	public InputTuning Tuning { get; init; } = InputTuning.Default;

	// Turn the controller off (not just report it) after IdlePowerOffMinutes without use.
	public bool PowerOffWhenIdle { get; init; }
}

// Outcome of the Bluetooth adapter test: verdict (null when nothing could be measured) and a localized summary.
public sealed record AdapterTestResult(Kopru.Core.Diagnostics.AdapterVerdict? Verdict, string Text);

// One-off happenings the state properties don't show (for notifications).
public enum ServiceNoticeKind
{
	// The controller was turned off after the idle time.
	PoweredOffIdle,
	// The bridge stopped on a problem; Detail is the localized reason.
	BridgeError,
	// The bridge started without HidHide.
	HidHideMissing
}

public sealed record ServiceNotice(ServiceNoticeKind Kind, string? Detail = null);

// What the UI needs from the controller/bridge. RealControllerService wraps the Core services (bridge host, HidHide);
// the tests use a simulated one.
public interface IControllerService
{
	ControllerKind Kind { get; }
	string DeviceName { get; }
	bool IsConnected { get; }
	// False until the controller reported its battery (e.g. while the bridge is stopped).
	bool HasBattery { get; }
	double BatteryPercent { get; }
	bool IsCharging { get; }
	double InputRateHz { get; }
	double LatencyMs { get; }

	// Measured link delay and relay time; null until measured (bridge running with a controller).
	LatencyReading? Latency { get; }

	// The latest raw (untuned) input body while the bridge runs (at least the first 9 bytes, see InputTransform), or null.
	byte[]? RawInput { get; }

	// The last Steam Input check (when the game is on Steam or Steam is running), or null.
	SteamInputResult? SteamInput { get; }
	bool IsBridgeRunning { get; }
	bool IsHapticsActive { get; }

	// True while a game has the virtual controller open (bridge running and in use).
	bool IsGameConnected { get; }

	// Current haptics output level per actuator, 0..1 (for the live meter).
	double HapticsLeftLevel { get; }
	double HapticsRightLevel { get; }

	// A localized problem the user should know about (bridge error, hiding unavailable...), or empty.
	string Warning { get; }

	// The game in the foreground, its mode, the supported games list, overrides and learned results.
	GameModeController Games { get; }

	// Raised after any state property changed; may be raised on any thread.
	event EventHandler? StateChanged;

	// Raised for one-off happenings; may be raised on any thread.
	event EventHandler<ServiceNotice>? Notice;

	Task StartBridgeAsync();
	Task StopBridgeAsync();
	void ApplySettings(ControllerSettings settings);
	void TestHaptics(HapticSide side);
	// Rumble with the other games profile (level and feel).
	void TestRumble();
	// The other games profile's fallback trigger effect.
	void TestFallbackTrigger();
	void TestTrigger(TriggerTestEffect effect);
	void TestLight(uint rgb);

	// Runs a short adapter check and returns a localized verdict.
	Task<AdapterTestResult> TestAdapterAsync();

	// Collects raw input for the given time while the sticks are left alone; progress goes 0..1.
	Task<DriftCalibrationResult> CalibrateDriftAsync(TimeSpan duration, IProgress<double>? progress = null);

	// Turns the controller off (true: it was turned off).
	Task<bool> PowerOffAsync();

	// Writes the diagnostics zip to path.
	Task ExportDiagnosticsAsync(string path);

	// Stops the bridge and undoes the hiding (app exit).
	Task ShutdownAsync();
}
