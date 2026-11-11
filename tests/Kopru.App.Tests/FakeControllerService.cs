// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Localization;

using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Games;

namespace Kopru.App.Tests;

// Test-only fake controller for view-model tests and screenshots: simulated battery drain, input rate jitter and latency.
public sealed partial class FakeControllerService : IControllerService, ILiveInputSource, IMicLevelSource, IDisposable
{
	// Controller mic level and mute state for the Settings meter.
	public double MicLevel { get; set; }

	// Live input for the controller test page: the open session's handler (null while closed) and how often it opened.
	public Action<Kopru.Core.Input.InputSnapshot>? LiveHandler { get; private set; }
	public int LiveOpens { get; private set; }

	public IDisposable OpenLiveInput(Action<Kopru.Core.Input.InputSnapshot> onSnapshot)
	{
		LiveOpens++;
		LiveHandler = onSnapshot;
		return new LiveClose(() => LiveHandler = null);
	}

	private sealed class LiveClose(Action close) : IDisposable
	{
		public void Dispose() => close();
	}

	private readonly Random _random = new(1234);
	private readonly Timer? _timer;
	private readonly Timer? _meterTimer;
	private double _phase;

	public FakeControllerService(ControllerKind kind = ControllerKind.DualSense, bool animate = true)
	{
		Kind = kind;
		IsConnected = kind != ControllerKind.None;
		Games = new GameModeController(SampleCatalog, Watcher, null);
		Games.StateChanged += _ => StateChanged?.Invoke(this, EventArgs.Empty);
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
	public bool HeadphonesPlugged { get; set; }
	public bool IsGameConnected { get; private set; }
	public double HapticsLeftLevel { get; private set; }
	public double HapticsRightLevel { get; private set; }
	public ControllerSettings Settings { get; private set; } = new();

	public Kopru.Core.Diagnostics.LatencyReading? Latency { get; set; } = new(1.2, 3.4, 0.05);

	// The link state the Overview shows (null: not running); WarningDue drives the weak link toast.
	public Kopru.Core.Diagnostics.LinkQualityReading? LinkQuality { get; set; }
	public int LinkWarningsAcknowledged { get; private set; }

	public void AcknowledgeLinkWarning()
	{
		LinkWarningsAcknowledged++;
		if (LinkQuality is { } r)
		{
			LinkQuality = r with { WarningDue = false };
		}
	}

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

	// Health: set Health (or Connection) directly; the "can" properties follow the report.
	public Kopru.Core.Health.HealthReport Health { get; set; } = Kopru.Core.Health.HealthReport.Build(
		new Kopru.Core.Health.HealthEnvironment(new Kopru.Core.Setup.ComponentInfo(true, "1.0", null, true),
			new Kopru.Core.Setup.ComponentInfo(true, "1.6", null, true), Kopru.Core.Setup.RadioState.On, [], null, null),
		new Kopru.Core.Health.HealthRuntime([new Kopru.Core.Hid.HidDevice(@"\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_fake", 0x054C, 0x0CE6, 78, 547, 64)],
			false, Kopru.Core.Bridge.BridgeState.Stopped, Kopru.Core.Bridge.BridgeError.None, null), DateTimeOffset.Now);

	public Kopru.Core.Health.ConnectionState Connection => Health.Connection;
	public Kopru.Core.Health.LastKnownBattery? LastKnownBattery { get; set; }
	public bool CanStartBridge => Health.CanStartBridge;
	public string CanStartBridgeReason => Health.CanStartBridgeReason;
	public bool CanTest => Health.CanTest;
	public string CanTestReason => Health.CanTestReason;

	// Fixes asked for, in order.
	public List<Kopru.Core.Health.FixAction> Fixes { get; } = [];

	public event EventHandler<Kopru.Core.Health.FixAction>? FixRequested;

	public async Task ExecuteFix(Kopru.Core.Health.FixAction fix)
	{
		Fixes.Add(fix);
		if (fix == Kopru.Core.Health.FixAction.RestartBridge)
		{
			await StopBridgeAsync();
			await StartBridgeAsync();
		}
		else if (fix != Kopru.Core.Health.FixAction.None && !Kopru.Core.Health.HealthReport.IsHandledByService(fix))
		{
			FixRequested?.Invoke(this, fix);
		}
	}

	public FakeGameWatcher Watcher { get; } = new("Cyberpunk2077.exe");
	public GameModeController Games { get; }

	public event EventHandler? StateChanged;

	public event EventHandler<ServiceNotice>? Notice;

	// Raises StateChanged after a test changed properties.
	public void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

	public void RaiseNotice(ServiceNotice notice) => Notice?.Invoke(this, notice);

	public event EventHandler<Kopru.Core.Input.ShortcutAction>? Shortcut;

	// Simulates a controller shortcut (raised on the calling thread).
	public void RaiseShortcut(Kopru.Core.Input.ShortcutAction action) => Shortcut?.Invoke(this, action);

	// Profile numbers shown on the player LEDs.
	public List<int> ProfileIndicators { get; } = [];

	public void ShowProfileIndicator(int index) => ProfileIndicators.Add(index);

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
		// Set: the start stays under way (running, like the real service) until the test completes it.
		return StartGate?.Task ?? Task.CompletedTask;
	}

	public TaskCompletionSource? StartGate { get; set; }

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

	// How often settings were pushed (one user change should be one push).
	public int Applies { get; private set; }

	public void ApplySettings(ControllerSettings settings)
	{
		Applies++;
		Settings = settings;
	}
	public void TestHaptics(HapticSide side) => TestCount++;
	public void TestRumble() => TestCount++;
	public void TestTrigger(TriggerTestEffect effect) => TestCount++;
	public void TestFallbackTrigger() => TestCount++;
	public void TestLight(uint rgb) => TestCount++;

	// When set, the adapter test waits for it (tests drive progress and cancellation).
	public TaskCompletionSource<AdapterTestResult>? AdapterGate { get; set; }

	public async Task<AdapterTestResult> TestAdapterAsync(CancellationToken cancel = default)
	{
		if (AdapterGate is { } gate)
		{
			return await gate.Task.WaitAsync(cancel).ConfigureAwait(false);
		}
		await Task.Delay(30, cancel).ConfigureAwait(false);
		return AdapterResult ?? GoodAdapter(InputRateHz);
	}

	// What the adapter test returns (null: a good adapter at the fake's input rate).
	public AdapterTestResult? AdapterResult { get; set; }

	// A test result as the real service words it, from link measurements like this PC's good adapter.
	public static AdapterTestResult GoodAdapter(double rate = 640) => Adapter(rate, 4.8, 0, 93.9);

	public static AdapterTestResult Adapter(double rate, double worstMs, double lostPercent, double hapticsRate, int gaps = 0, double coverage = 1)
	{
		int reports = (int)(rate * 5);
		int lost = (int)Math.Round(reports * lostPercent / (100 - lostPercent));
		Kopru.Core.Diagnostics.LinkResult Link() => new(reports, 5, rate, new(1.3, 2.5, 4.4, 20), new(1.0, 2.0, worstMs, worstMs + 10), lost, gaps, 0);
		Kopru.Core.Diagnostics.AdapterReport report = Kopru.Core.Diagnostics.AdapterCheck.Grade(Link(), Link(), hapticsRate) with { Coverage = coverage };
		return RealControllerService.Describe(report);
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

	public Kopru.Core.Hid.ControllerDetails? Details { get; set; }
	public DateTimeOffset? ConnectedSince { get; set; }
	public int Reconnects { get; private set; }

	public Task ReconnectAsync()
	{
		Reconnects++;
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		_timer?.Dispose();
		_meterTimer?.Dispose();
	}
}

// The foreground app is whatever the test sets (an exe file name, null for none); recognised like the real watcher does
// (known games only). Each exe runs as its own process until the test exits it.
public sealed class FakeGameWatcher : IGameWatcher
{
	private readonly FakeProcessSource _source = new();
	private readonly GameWatcher _inner;

	public FakeGameWatcher(string? exe, GameCatalog? catalog = null)
	{
		_inner = new GameWatcher(catalog ?? FakeControllerService.SampleCatalog, _source);
		Exe = exe;
	}

	public string? Exe
	{
		get => _source.Current?.Exe;
		set => _source.Show(value);
	}

	public FakeProcessSource Source => _source;

	// The exe's process ends (it is no longer in front either).
	public void Exit(string exe) => _source.Exit(exe);

	public ForegroundGame? Poll() => _inner.Poll();

	public bool IsRunning(ForegroundGame game) => _inner.IsRunning(game);

	public IReadOnlyList<ProcessInfo> RunningApps() => _inner.RunningApps();

	public IReadOnlyList<UserGame> UserGames { get => _inner.UserGames; set => _inner.UserGames = value; }
}

// Processes by exe name: Show starts one (or brings it to the front), Exit ends it.
public sealed class FakeProcessSource : IProcessSource
{
	private readonly Dictionary<string, ProcessInfo> _running = new(StringComparer.OrdinalIgnoreCase);
	private int _nextId = 1000;

	public ProcessInfo? Current { get; private set; }

	public void Show(string? exe, string? path = null)
	{
		if (exe == null)
		{
			Current = null;
			return;
		}
		if (!_running.TryGetValue(exe, out ProcessInfo? process))
		{
			process = new ProcessInfo(_nextId++, exe, path);
			_running[exe] = process;
		}
		Current = process;
	}

	public void Exit(string exe)
	{
		_running.Remove(exe);
		if (Current != null && string.Equals(Current.Exe, exe, StringComparison.OrdinalIgnoreCase))
		{
			Current = null;
		}
	}

	public ProcessInfo? Foreground() => Current;

	public bool IsRunning(ProcessInfo process) => _running.Values.Any(p => p.Id == process.Id);

	public IReadOnlyList<ProcessInfo> WindowedApps() => _running.Values.ToList();
}
