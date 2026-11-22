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

// Outcome of the Bluetooth adapter test: verdict (null when nothing could be measured properly), a localized summary and
// the measurement (null when nothing arrived), from which AdapterCheck.Explain words the result in any language.
public sealed record AdapterTestResult(Kopru.Core.Diagnostics.AdapterVerdict? Verdict, string Text, AdapterReport? Report = null);

// One-off happenings the state properties don't show (for notifications).
public enum ServiceNoticeKind
{
	// The controller was turned off after the idle time.
	PoweredOffIdle,
	// The bridge stopped on a problem; Detail is the localized reason.
	BridgeError,
	// A virtual controller start was refused because HidHide is not installed.
	HidHideMissing,
	// The bridged controller lost Bluetooth while on a cable: the bridge stopped (resumes on Bluetooth). A cable plugged
	// in while Bluetooth stays up only charges and raises nothing (CablePluggedIn).
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

	// How the link feels (good / borderline / weak, see LinkQualityMonitor); null while the bridge does not run.
	LinkQualityReading? LinkQuality => null;

	// The last minute of the link, a second at a time, oldest first (LinkHistory, kept by the bridge whatever page is
	// shown); null while the bridge does not run.
	IReadOnlyList<LinkSecondPoint>? LinkSeconds => null;

	// The weak link warning was shown (one per bad episode, see LinkQualityMonitor.AcknowledgeWarning).
	void AcknowledgeLinkWarning()
	{
	}

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

	// Reads the whole health picture again soon, the slow facts (installed drivers, pending restart) included: the setup
	// page opened or ran a step. The periodic refresh reads those only about once a minute.
	void RefreshHealth()
	{
	}

	// Carries out a chip's fix: RestartBridge and OpenBluetoothSettings here, the rest raise FixRequested for the UI.
	Task ExecuteFix(FixAction fix);

	// A fix that needs a page (setup wizard step, HidHide install, Steam Input help, adapter test, pairing guide).
	event EventHandler<FixAction>? FixRequested;

	// Raised after any state property changed; may be raised on any thread.
	event EventHandler? StateChanged;

	// DSX mod support's listener state (Kopru.Core.Mods.DsxListener; Off while the setting is off), and its changes (raised on the UI
	// thread: a state change, a mod's first message or end, and once a second while a mod's effects are active).
	Kopru.Core.Mods.DsxStatus DsxStatus { get; }

	event EventHandler? DsxStatusChanged;

	// Raised for one-off happenings; may be raised on any thread.
	event EventHandler<ServiceNotice>? Notice;

	// A controller shortcut was used; raised on the input thread.
	event EventHandler<Kopru.Core.Input.ShortcutAction>? Shortcut;

	// A program with the virtual pad open started using the controller as a game does (a trigger effect, haptics sound,
	// rumble; Kopru.Core.Bridge.ControllerBridge.GameUseStarted). Never raised by the app's own tests. Raised on the
	// bridge's report or audio thread.
	event EventHandler<Kopru.Core.Bridge.GameUse>? GameUsingController;

	// What a game is doing with any controller right now (a trigger effect held, motors running, haptics sound in the last
	// 2 s), or null. The level behind GameUsingController.
	Kopru.Core.Bridge.GameUse? GameUsingControllerNow { get; }

	// While Mouse Mode is on (read at the game tick, about once a second): the exe of the foreground program when it could
	// be a game (not this app, not a shell process or common desktop app: GameWatcher.Ignored, not one of Windows' own apps
	// and tools, not a program whose path can't be read: RealControllerService.PossibleGame), else null. Always null
	// while Mouse Mode is off.
	string? PossibleGameInFront { get; }

	// PossibleGameInFront changed; raised on a timer thread.
	event EventHandler? ForegroundChanged;

	// The main window shows a page where the controller's buttons are being tried (the controller page's Test or Remap
	// tab): the touchpad double press does nothing there. Set by the UI.
	bool ButtonPageShown { get; set; }

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
	// An effect per trigger for a moment (a game's Custom Trigger), then the triggers go back to what they were.
	void TestTriggers(byte[] right, byte[] left);
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

	// The controller is on Bluetooth and its USB cable is plugged in too (charging; the bridge stays on Bluetooth).
	bool CablePluggedIn => false;

	// The bridge talks to the selected controller over its USB cable (experimental wired mode).
	bool IsWired => false;

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

	// The Windows playback and recording devices (device pickers, the mic button on a PC microphone); null: none.
	Kopru.Core.Audio.AudioDevices? AudioDevices => null;

	// "Turn sound into haptics": what it is doing right now (AudioHapticsRunner), and when that changes (any thread).
	Kopru.Core.Audio.AudioHapticsStatus AudioHapticsStatus => Kopru.Core.Audio.AudioHapticsStatus.Off;

	event Action? AudioHapticsStatusChanged
	{
		add { }
		remove { }
	}

	// The player slot whose virtual pad sits on that virtual hub port (names several pads' audio devices), or null.
	int? SlotOfAudioPort(int port) => null;

	// The light last written to the selected controller (light bar, player LEDs, whoever set them); null while none is
	// open. A cheap read: the controller drawings show it (LiveLight).
	Kopru.Core.Lighting.LightOutput? ControllerLight => null;
}
