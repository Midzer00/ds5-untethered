// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Games;
using Kopru.Core.Health;
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

// Optional: the live controller mic level (the real service; fakes may implement it).
public interface IMicLevelSource
{
	double MicLevel { get; }
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
	HidHideMissing,
	// The bridged controller was plugged in by cable: the bridge stopped (resumes on Bluetooth).
	CableConnected,
	// The virtual controller type changed for the game in front; Detail is the VirtualPadType name.
	PadTypeChanged
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

	// The latest raw (untuned) input body while the bridge runs (at least the first 9 bytes, see InputTransform; 27 with the gyro, see GyroAim), or null.
	byte[]? RawInput { get; }

	// The last Steam Input check (when the game is on Steam or Steam is running), or null.
	SteamInputResult? SteamInput { get; }
	bool IsBridgeRunning { get; }
	bool IsHapticsActive { get; }

	// Headphones in the controller's jack (known while the bridge runs).
	bool HeadphonesPlugged { get; }

	// True while a game has the virtual controller open (bridge running and in use).
	bool IsGameConnected { get; }

	// Current haptics output level per actuator, 0..1 (for the live meter).
	double HapticsLeftLevel { get; }
	double HapticsRightLevel { get; }

	// A localized problem the user should know about (bridge error, hiding unavailable...), or empty.
	string Warning { get; }

	// The game in the foreground, its mode, the supported games list, overrides and learned results.
	GameModeController Games { get; }

	// Controller connection sub-state (Bluetooth off, not paired, paired but asleep, Bluetooth, cable; Edge flag).
	ConnectionState Connection { get; }

	// Health chips, the blocking issue and the "why disabled" reasons; refreshed about every 5 s and on bridge events.
	HealthReport Health { get; }

	// The last battery reading seen (kept across runs), or null.
	LastKnownBattery? LastKnownBattery { get; }

	bool CanStartBridge { get; }
	// Localized reason when CanStartBridge is false, else empty.
	string CanStartBridgeReason { get; }
	bool CanTest { get; }
	// Localized reason when CanTest is false, else empty.
	string CanTestReason { get; }

	// Carries out a chip's fix: RestartBridge and OpenBluetoothSettings here, the rest raise FixRequested for the UI.
	Task ExecuteFix(FixAction fix);

	// A fix that needs a page (setup wizard step, HidHide install, Steam Input help, adapter test, pairing guide).
	event EventHandler<FixAction>? FixRequested;

	// Raised after any state property changed; may be raised on any thread.
	event EventHandler? StateChanged;

	// Raised for one-off happenings; may be raised on any thread.
	event EventHandler<ServiceNotice>? Notice;

	// A controller shortcut was used; raised on the input thread.
	event EventHandler<Kopru.Core.Input.ShortcutAction>? Shortcut;

	// Shows a profile number (1-5) on the player LEDs for a moment.
	void ShowProfileIndicator(int index);

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

	// Runs a short adapter check (two phases of RealControllerService.AdapterPhase) and returns a localized verdict;
	// throws OperationCanceledException when cancelled.
	Task<AdapterTestResult> TestAdapterAsync(CancellationToken cancel = default);

	// Collects raw input for the given time while the sticks are left alone; progress goes 0..1.
	Task<DriftCalibrationResult> CalibrateDriftAsync(TimeSpan duration, IProgress<double>? progress = null);

	// Turns the controller off (true: it was turned off).
	Task<bool> PowerOffAsync();

	// Writes the diagnostics zip to path.
	Task ExportDiagnosticsAsync(string path);

	// Stops the bridge and undoes the hiding (app exit).
	Task ShutdownAsync();

	// What the connected controller says about itself (firmware info, address), or null.
	Kopru.Core.Hid.ControllerDetails? Details => null;

	// When the controller connected (for "connected for"), or null while none is connected.
	DateTimeOffset? ConnectedSince => null;

	// Tears the bridge's connection down, opens the controller again and restarts the bridge if it ran.
	Task ReconnectAsync() => Task.CompletedTask;

	// Several controllers (see ControllerEntry): every controller present, by slot; empty when the service has no list.
	// The single-controller members above describe SelectedController.
	IReadOnlyList<ControllerEntry> Controllers => [];

	ControllerEntry? SelectedController => null;

	// Makes the controller with that id the one the pages show and test.
	void SelectController(string id)
	{
	}

	// The settings one controller runs with instead of the global ones (null: the global settings again).
	void SetControllerProfile(string id, ControllerSettings? settings)
	{
	}
}
