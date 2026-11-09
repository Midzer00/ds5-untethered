// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Games;
using Kopru.Core.Hid;
using Kopru.Core.Hiding;
using Kopru.Core.Input;
using Kopru.Core.Localization;
using Kopru.Core.Setup;
using Kopru.Core.Steam;

namespace Kopru.App.Services;

// The controller service on the real Core: BridgeHost for the bridge, light and tests, HidHide for hiding. The bridge
// never starts by itself; the user presses Start. StateChanged is raised on the UI thread.
public sealed class RealControllerService : IControllerService, IDisposable
{
	public static readonly TimeSpan HapticsTestLength = TimeSpan.FromSeconds(1.5);
	public static readonly TimeSpan TriggerTestLength = TimeSpan.FromSeconds(3);
	public static readonly TimeSpan AdapterPhase = TimeSpan.FromSeconds(5);
	public static readonly TimeSpan SteamCheckInterval = TimeSpan.FromSeconds(10);
	public static readonly TimeSpan RawInputMaxAge = TimeSpan.FromSeconds(1);

	private readonly BridgeHost host;
	private readonly IHidingService hiding;
	private readonly IControllerFinder finder;
	private readonly Action<Action> post;
	private readonly Func<HidDevice, string?> instanceId;
	private readonly Timer? presenceTimer;
	private readonly Timer? meterTimer;
	private readonly Timer? gameTimer;
	private readonly object gate = new();
	private readonly Func<string?, SteamInputResult>? steamCheck;
	private readonly string? adapterResultPath;
	private readonly byte[] rawInput = new byte[InputTransform.Buttons1 + 1];

	private HidDevice? present;
	private bool started;
	private string hidingWarning = string.Empty;
	private string testWarning = string.Empty;
	private LightSettings? lastLight;
	private ControllerSettings settings = new();
	private int disposed;
	private long rawInputAt;
	private SteamInputResult? steamInput;
	private string? steamAppId;
	private long steamCheckedAt = long.MinValue;

	public RealControllerService(BridgeHost host, IHidingService hiding, IControllerFinder finder, Action<Action>? post = null,
		Func<HidDevice, string?>? instanceId = null, bool timers = true, GameModeController? games = null,
		Func<string?, SteamInputResult>? steamCheck = null, string? adapterResultPath = null)
	{
		this.steamCheck = steamCheck;
		this.adapterResultPath = adapterResultPath;
		GameCatalog catalog = games?.Catalog ?? GameCatalog.LoadEmbedded();
		Games = games ?? new GameModeController(catalog, new GameWatcher(catalog), host);
		Games.StateChanged += _ => Raise();
		Games.MemoryChanged += Raise;
		this.host = host;
		this.hiding = hiding;
		this.finder = finder;
		this.post = post ?? (a => Dispatcher.UIThread.Post(a));
		this.instanceId = instanceId ?? (d => DeviceInstance.FromInterfacePath(d.Path));
		host.StatusUpdated += _ => Raise();
		host.ControllerOpened = OnControllerOpenedAsync;
		host.RawInput += OnRawInput;
		host.PoweredOff += (ok, idle) =>
		{
			if (ok && idle)
			{
				RaiseNotice(new ServiceNotice(ServiceNoticeKind.PoweredOffIdle));
			}
		};
		host.StateChanged += state =>
		{
			if (state == BridgeState.Error && started)
			{
				RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, host.Status.LastErrorText() ?? Text.BridgeError()));
			}
		};
		if (timers)
		{
			presenceTimer = new Timer(_ => RefreshPresence(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
			meterTimer = new Timer(_ => MeterTick(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
			gameTimer = new Timer(_ => GameTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
		}
	}

	public static RealControllerService CreateDefault() =>
		new(BridgeHost.CreateDefault(), HidHideHidingService.CreateDefault(), new HidControllerFinder(),
			steamCheck: SteamInputCheck.CreateDefault().Check,
			adapterResultPath: Path.Combine(Kopru.Core.AppPaths.DataDirectory, DiagnosticsExport.AdapterResultFileName));

	private BridgeStatus Status => host.Status;

	private HidDevice? Device => host.Device ?? present;

	public ControllerKind Kind => Device is { } d ? d.IsEdge ? ControllerKind.DualSenseEdge : ControllerKind.DualSense : ControllerKind.None;
	public string DeviceName => Kind == ControllerKind.DualSenseEdge ? "DualSense Edge" : "DualSense";
	public bool IsConnected => Device != null;
	public bool HasBattery => Status.Battery != null;
	public double BatteryPercent => Status.Battery?.Percent ?? 0;
	public bool IsCharging => Status.Battery?.State is ChargeState.Charging;
	public double InputRateHz => Status.InputRateHz;

	// No end-to-end latency is measured yet; this is the average time between input reports.
	public double LatencyMs => Status.InputRateHz > 1 ? 1000 / Status.InputRateHz : 0;

	public LatencyReading? Latency => Status is { LinkLatencyP50Ms: double p50, LinkLatencyP99Ms: double p99, RelayMs: double relay }
		? new LatencyReading(p50, p99, relay)
		: null;

	public byte[]? RawInput
	{
		get
		{
			lock (rawInput)
			{
				long at = rawInputAt;
				return at == 0 || Environment.TickCount64 - at > RawInputMaxAge.TotalMilliseconds ? null : (byte[])rawInput.Clone();
			}
		}
	}

	public SteamInputResult? SteamInput => steamInput;

	public bool IsBridgeRunning => started;
	public bool IsHapticsActive => Status.HapticsReportsPerSecond > 1;
	public bool IsGameConnected => started && host.State == BridgeState.Running;
	public double HapticsLeftLevel { get; private set; }
	public double HapticsRightLevel { get; private set; }

	public string Warning
	{
		get
		{
			if (started && Status.LastErrorText() is { } error)
			{
				return error;
			}
			lock (gate)
			{
				return hidingWarning.Length > 0 ? hidingWarning : testWarning;
			}
		}
	}

	public GameModeController Games { get; }

	public event EventHandler? StateChanged;

	public event EventHandler<ServiceNotice>? Notice;

	public async Task StartBridgeAsync()
	{
		if (started)
		{
			return;
		}
		started = true;
		SetHidingWarning(string.Empty);
		if (!hiding.IsInstalled)
		{
			SetHidingWarning(Text.HidingUnavailable());
			RaiseNotice(new ServiceNotice(ServiceNoticeKind.HidHideMissing));
		}
		else
		{
			try
			{
				await hiding.BeginAsync().ConfigureAwait(false);
			}
			catch (HidHideException e)
			{
				SetHidingWarning(Text.HidingFailed(e.Message));
			}
		}
		await host.StartAsync().ConfigureAwait(false);
		Raise();
	}

	public async Task StopBridgeAsync()
	{
		if (!started)
		{
			return;
		}
		started = false;
		await host.StopAsync().ConfigureAwait(false);
		await RestoreHidingAsync().ConfigureAwait(false);
		HapticsLeftLevel = HapticsRightLevel = 0;
		Raise();
	}

	public void ApplySettings(ControllerSettings value)
	{
		settings = value;
		Games.SetProfiles(value.Supported, value.Unsupported);
		host.IdleMinutes = value.IdlePowerOffMinutes;
		host.PowerOffWhenIdle = value.PowerOffWhenIdle;
		if (host.InputTransform.Tuning != value.Tuning)
		{
			host.InputTransform = new InputTransform(value.Tuning);
		}
		LightSettings light = LightSettings.FromRgb(value.LightbarColour, value.Brightness / 100, value.PlayerLeds);
		if (light != lastLight)
		{
			lastLight = light;
			_ = Task.Run(() => host.ApplyLight(light));
		}
	}

	public void TestHaptics(HapticSide side) =>
		RunTest(() => host.TestHapticsAsync(side != HapticSide.Right, side != HapticSide.Left, HapticsTestLength));

	// Rumble is played as haptics over Bluetooth (rumble bits would switch the game's haptics off).
	public void TestRumble() =>
		RunTest(() => host.TestRumbleAsync(settings.RumbleFeel, settings.RumbleLevel / 100, HapticsTestLength));

	public void TestTrigger(TriggerTestEffect effect)
	{
		byte[] bytes = TriggerEffect(effect, settings.TriggerStrength);
		RunTest(() => host.TestTriggersAsync(bytes, effect == TriggerTestEffect.Off ? TimeSpan.Zero : TriggerTestLength));
	}

	public void TestFallbackTrigger()
	{
		byte[] bytes = GameProfiles.Effect(settings.FallbackTrigger, settings.FallbackStrength / 100);
		RunTest(() => host.TestTriggersAsync(bytes, settings.FallbackTrigger == FallbackTrigger.Off ? TimeSpan.Zero : TriggerTestLength));
	}

	public void TestLight(uint rgb)
	{
		LightSettings light = LightSettings.FromRgb(rgb, settings.Brightness / 100, settings.PlayerLeds);
		lastLight = light;
		_ = Task.Run(() => host.ApplyLight(light));
	}

	// Test effects at the trigger strength setting (0-100 % maps to the effects' 1-8 steps).
	public static byte[] TriggerEffect(TriggerTestEffect effect, double strengthPercent) =>
		GameProfiles.Effect((FallbackTrigger)effect, strengthPercent / 100);

	public async Task<AdapterTestResult> TestAdapterAsync()
	{
		bool wasRunning = started;
		if (wasRunning)
		{
			await StopBridgeAsync().ConfigureAwait(false);
		}
		AdapterReport? report;
		try
		{
			report = await host.RunAdapterCheckAsync(AdapterPhase, AdapterPhase).ConfigureAwait(false);
		}
		finally
		{
			if (wasRunning)
			{
				await StartBridgeAsync().ConfigureAwait(false);
			}
		}
		AdapterTestResult result = Describe(report);
		if (adapterResultPath != null)
		{
			WriteAdapterResult(adapterResultPath, report, result);
		}
		return result;
	}

	// Keeps the last adapter test for the diagnostics export (DiagnosticsExport.AdapterResultFileName).
	public static void WriteAdapterResult(string path, AdapterReport? report, AdapterTestResult result)
	{
		try
		{
			string? dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir))
			{
				Directory.CreateDirectory(dir);
			}
			var data = new
			{
				Time = DateTimeOffset.Now,
				Verdict = result.Verdict?.ToString(),
				result.Text,
				Idle = report?.Idle,
				Load = report?.Load,
				report?.HapticsRateHz
			};
			File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			// The export simply has no adapter result then.
		}
	}

	public async Task<DriftCalibrationResult> CalibrateDriftAsync(TimeSpan duration, IProgress<double>? progress = null)
	{
		DriftCalibrator calibrator = new(duration);
		System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
		object sync = new();
		void OnInput(ReadOnlySpan<byte> body)
		{
			lock (sync)
			{
				calibrator.Add(body, clock.Elapsed);
			}
		}
		host.RawInput += OnInput;
		try
		{
			// Ends when enough time passed even without input (then the result says there was not enough data).
			while (clock.Elapsed < duration + TimeSpan.FromMilliseconds(500))
			{
				lock (sync)
				{
					if (calibrator.IsComplete)
					{
						break;
					}
				}
				progress?.Report(Math.Min(1, clock.Elapsed / duration));
				await Task.Delay(50).ConfigureAwait(false);
			}
		}
		finally
		{
			host.RawInput -= OnInput;
		}
		progress?.Report(1);
		lock (sync)
		{
			return calibrator.Finish();
		}
	}

	public Task<bool> PowerOffAsync() => host.PowerOffAsync();

	public Task ExportDiagnosticsAsync(string path) =>
		DiagnosticsExport.CreateZipAsync(path, DiagnosticsSources.Default(Device is { } d ? ControllerInfo.From(d) : null));

	public static AdapterTestResult Describe(AdapterReport? report) => report == null
		? new AdapterTestResult(null, Text.AdapterTestNoData())
		: new AdapterTestResult(report.Verdict, report.VerdictText + Environment.NewLine + string.Join(Environment.NewLine, report.Reasons));

	public async Task ShutdownAsync()
	{
		Dispose();
		started = false;
		await host.StopAsync().ConfigureAwait(false);
		// The active hiding session first, then the early registration made at app start.
		await HidingLifecycle.StopAsync(hiding).ConfigureAwait(false);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref disposed, 1) == 0)
		{
			presenceTimer?.Dispose();
			meterTimer?.Dispose();
			gameTimer?.Dispose();
		}
	}

	// Looks for a paired controller while the bridge does not hold one (listing only; nothing is opened).
	public void RefreshPresence()
	{
		if (host.Device != null)
		{
			return;
		}
		HidDevice? found;
		try
		{
			found = finder.Find().FirstOrDefault(d => d.Transport == Transport.Bluetooth);
		}
		catch (Exception)
		{
			found = null;
		}
		if (found != present)
		{
			present = found;
			Raise();
		}
	}

	// Takes the haptics levels actually sent since the last tick.
	public void MeterTick()
	{
		(double left, double right) = host.TakeHapticsLevels();
		if (left != HapticsLeftLevel || right != HapticsRightLevel)
		{
			HapticsLeftLevel = left;
			HapticsRightLevel = right;
			Raise();
		}
	}

	// Looks at the foreground game and applies its mode (once a second).
	public void GameTick()
	{
		try
		{
			Games.Tick();
		}
		catch (Exception)
		{
			// Tried again on the next tick.
		}
		CheckSteamInput();
	}

	// Runs the Steam Input check at most every SteamCheckInterval, or at once when the game changed. Kept only when the
	// game is on Steam or Steam is running.
	public void CheckSteamInput(bool force = false)
	{
		if (steamCheck == null)
		{
			return;
		}
		string? appId = Games.State.Match?.SteamAppId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
		long now = Environment.TickCount64;
		if (!force && appId == steamAppId && steamCheckedAt != long.MinValue && now - steamCheckedAt < SteamCheckInterval.TotalMilliseconds)
		{
			return;
		}
		steamAppId = appId;
		steamCheckedAt = now;
		SteamInputResult? result;
		try
		{
			result = steamCheck(appId);
		}
		catch (Exception)
		{
			result = null;
		}
		if (result != null && appId == null && !result.SteamRunning)
		{
			result = null;
		}
		if (result != steamInput)
		{
			steamInput = result;
			Raise();
		}
	}

	private void OnRawInput(ReadOnlySpan<byte> body)
	{
		if (body.Length < rawInput.Length)
		{
			return;
		}
		lock (rawInput)
		{
			body[..rawInput.Length].CopyTo(rawInput);
			rawInputAt = Environment.TickCount64;
		}
	}

	private async Task OnControllerOpenedAsync(HidDevice device, CancellationToken cancel)
	{
		present = device;
		if (!hiding.IsInstalled)
		{
			return;
		}
		string? id = instanceId(device);
		if (id == null)
		{
			return;
		}
		try
		{
			await hiding.HideAsync(id).ConfigureAwait(false);
		}
		catch (Exception e) when (e is HidHideException or ArgumentException)
		{
			SetHidingWarning(Text.HidingFailed(e.Message));
		}
	}

	private async Task RestoreHidingAsync()
	{
		if (!hiding.IsInstalled)
		{
			return;
		}
		try
		{
			await hiding.RestoreAsync().ConfigureAwait(false);
		}
		catch (HidHideException e)
		{
			SetHidingWarning(e.Message);
		}
	}

	private void RunTest(Func<Task<bool>> test) => _ = Task.Run(async () =>
	{
		bool ok;
		try
		{
			ok = await test().ConfigureAwait(false);
		}
		catch (Exception)
		{
			ok = false;
		}
		lock (gate)
		{
			testWarning = ok ? string.Empty : Text.TestNoController();
		}
		Raise();
	});

	private void SetHidingWarning(string text)
	{
		lock (gate)
		{
			hidingWarning = text;
		}
		Raise();
	}

	private void Raise() => post(() => StateChanged?.Invoke(this, EventArgs.Empty));

	private void RaiseNotice(ServiceNotice notice) => post(() => Notice?.Invoke(this, notice));
}
