// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Localization;

using Kopru.App.Services;
using Kopru.Core.Games;

namespace Kopru.App.Tests;

// Test-only fake controller for view-model tests and screenshots: simulated battery drain, input rate jitter and latency.
public sealed class FakeControllerService : IControllerService, IDisposable
{
	private readonly Random _random = new(1234);
	private readonly Timer? _timer;
	private readonly Timer? _meterTimer;
	private double _phase;

	public FakeControllerService(ControllerKind kind = ControllerKind.DualSense, bool animate = true)
	{
		Kind = kind;
		IsConnected = kind != ControllerKind.None;
		Games = new GameModeController(SampleCatalog, Watcher, null);
		Games.Tick();
		if (animate)
		{
			_timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
			_meterTimer = new Timer(_ => MeterTick(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
		}
	}

	public ControllerKind Kind { get; }
	public string DeviceName => Kind == ControllerKind.DualSenseEdge ? "DualSense Edge" : "DualSense";
	public bool IsConnected { get; set; }
	public bool HasBattery => true;
	public string Warning { get; set; } = string.Empty;
	public double BatteryPercent { get; set; } = 78;
	public bool IsCharging { get; set; }
	public double InputRateHz { get; private set; } = 998;
	public double LatencyMs { get; private set; } = 4.2;
	public bool IsBridgeRunning { get; private set; }
	public bool IsHapticsActive => IsGameConnected;
	public bool IsGameConnected { get; private set; }
	public double HapticsLeftLevel { get; private set; }
	public double HapticsRightLevel { get; private set; }
	public ControllerSettings Settings { get; private set; } = new();

	public Kopru.Core.Diagnostics.LatencyReading? Latency { get; set; } = new(1.2, 3.4, 0.05);

	// A stick slightly off center and the triggers part-way down (LX, LY, RX, RY, L2, R2, ..., buttons).
	public byte[]? RawInput { get; set; } = [168, 96, 131, 125, 70, 210, 0, 0, 0];

	public Kopru.Core.Steam.SteamInputResult? SteamInput { get; set; }

	public Kopru.Core.Input.DriftCalibrationResult CalibrationResult { get; set; } =
		new(Kopru.Core.Input.DriftCalibrationStatus.Success, 3, -2, 0.5, 0, 6, 4);

	public int Calibrations { get; private set; }

	public bool PowerOffSucceeds { get; set; } = true;

	public int PowerOffs { get; private set; }

	public List<string> Exports { get; } = [];

	public Exception? ExportFailure { get; set; }
	public int TestCount { get; private set; }

	// A few catalog games; the simulated foreground game is Cyberpunk 2077.
	public static GameCatalog SampleCatalog { get; } = new(
	[
		new CatalogGame("Cyberpunk 2077", ["Cyberpunk2077.exe"], 1091500, true, true, "high"),
		new CatalogGame("Assetto Corsa Competizione", ["AC2-Win64-Shipping.exe"], 805550, false, true, "medium"),
		new CatalogGame("Marvel's Spider-Man Remastered", ["Spider-Man.exe"], 1817070, true, true, "high"),
		new CatalogGame("Returnal", ["Returnal-Win64-Shipping.exe"], 1649240, true, true, "high"),
	]);

	public FakeGameWatcher Watcher { get; } = new("Cyberpunk2077.exe");
	public GameModeController Games { get; }

	public event EventHandler? StateChanged;

	public event EventHandler<ServiceNotice>? Notice;

	// Raises StateChanged after a test changed properties.
	public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

	public void RaiseNotice(ServiceNotice notice) => Notice?.Invoke(this, notice);

	public int Starts { get; private set; }

	public int Stops { get; private set; }

	// Advances the simulation by one step (called by the timer; tests call it directly).
	public void Tick()
	{
		lock (_random)
		{
			Step();
		}
		StateChanged?.Invoke(this, EventArgs.Empty);
	}

	private void Step()
	{
		InputRateHz = 990 + _random.Next(0, 12);
		LatencyMs = 3.6 + _random.NextDouble() * 1.4;
		BatteryPercent = Math.Max(5, BatteryPercent - 0.02);
		// A simulated game picks up the virtual controller one step after the bridge starts.
		IsGameConnected = IsBridgeRunning;
		UpdateMeter();
	}

	public Task StartBridgeAsync()
	{
		Starts++;
		IsBridgeRunning = true;
		StateChanged?.Invoke(this, EventArgs.Empty);
		return Task.CompletedTask;
	}

	// Moves the simulated haptics meter (called by a faster timer while a game is connected).
	public void MeterTick()
	{
		if (IsGameConnected)
		{
			lock (_random)
			{
				UpdateMeter();
			}
			StateChanged?.Invoke(this, EventArgs.Empty);
		}
	}

	private void UpdateMeter()
	{
		if (!IsGameConnected)
		{
			HapticsLeftLevel = HapticsRightLevel = 0;
			return;
		}
		_phase += 0.7;
		double scale = Settings.GameHapticsLevel / 300.0;
		HapticsLeftLevel = Math.Clamp(scale * (0.45 + 0.35 * Math.Sin(_phase) + 0.25 * _random.NextDouble()), 0, 1);
		HapticsRightLevel = Math.Clamp(scale * (0.40 + 0.35 * Math.Cos(_phase * 1.3) + 0.25 * _random.NextDouble()), 0, 1);
	}

	public Task StopBridgeAsync()
	{
		Stops++;
		IsBridgeRunning = false;
		IsGameConnected = false;
		UpdateMeter();
		StateChanged?.Invoke(this, EventArgs.Empty);
		return Task.CompletedTask;
	}

	public void ApplySettings(ControllerSettings settings) => Settings = settings;
	public void TestHaptics(HapticSide side) => TestCount++;
	public void TestRumble() => TestCount++;
	public void TestTrigger(TriggerTestEffect effect) => TestCount++;
	public void TestFallbackTrigger() => TestCount++;
	public void TestLight(uint rgb) => TestCount++;

	public async Task<AdapterTestResult> TestAdapterAsync()
	{
		await Task.Delay(30).ConfigureAwait(false);
		return new AdapterTestResult(Kopru.Core.Diagnostics.AdapterVerdict.Suitable, Text.AdapterAllGood(InputRateHz));
	}

	public async Task<Kopru.Core.Input.DriftCalibrationResult> CalibrateDriftAsync(TimeSpan duration, IProgress<double>? progress = null)
	{
		Calibrations++;
		progress?.Report(0.5);
		await Task.Yield();
		progress?.Report(1);
		return CalibrationResult;
	}

	public Task<bool> PowerOffAsync()
	{
		PowerOffs++;
		return Task.FromResult(PowerOffSucceeds);
	}

	public Task ExportDiagnosticsAsync(string path)
	{
		Exports.Add(path);
		return ExportFailure != null ? Task.FromException(ExportFailure) : Task.CompletedTask;
	}

	public Task ShutdownAsync() => StopBridgeAsync();

	public void Dispose()
	{
		_timer?.Dispose();
		_meterTimer?.Dispose();
	}
}

// The foreground app is whatever the test sets.
public sealed class FakeGameWatcher(string? exe) : IGameWatcher
{
	public string? Exe { get; set; } = exe;

	public ForegroundGame? Poll() => Exe == null ? null : new ForegroundGame(Exe, FakeControllerService.SampleCatalog.Find(Exe));
}
