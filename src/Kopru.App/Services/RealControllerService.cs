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

// The controller service on the real Core: BridgeHost for the bridge, light and tests, HidHide for hiding. The service
// never starts the bridge by itself (the app's automatic start, SettingsState.AttachAutoStart, or the user does).
// StateChanged is raised on the UI thread.
public sealed partial class RealControllerService : IControllerService, IMicLevelSource, IDisposable
{
	public static readonly TimeSpan HapticsTestLength = TimeSpan.FromSeconds(1.5);
	public static readonly TimeSpan TriggerTestLength = TimeSpan.FromSeconds(3);
	public static readonly TimeSpan AdapterPhase = TimeSpan.FromSeconds(5);
	public static readonly TimeSpan SteamCheckInterval = TimeSpan.FromSeconds(10);
	public static readonly TimeSpan RawInputMaxAge = TimeSpan.FromSeconds(1);
	// How often the battery is read while the bridge is stopped (BridgeHost.ProbeBatteryAsync).
	public static readonly TimeSpan BatteryProbeInterval = TimeSpan.FromSeconds(30);

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
	private readonly byte[] rawInput = new byte[GyroAim.MinBodySize];

	private HidDevice? present;
	private bool started;
	private string hidingWarning = string.Empty;
	private string testWarning = string.Empty;
	private ControllerSettings settings = new();
	private bool settingsApplied;
	private int disposed;
	private long rawInputAt;
	private SteamInputResult? steamInput;
	private string? steamAppId;
	private long steamCheckedAt = long.MinValue;
	private readonly TimeProvider time;
	private DateTimeOffset? lastBatteryProbe;
	private int batteryProbing;

	public RealControllerService(BridgeHost host, IHidingService hiding, IControllerFinder finder, Action<Action>? post = null,
		Func<HidDevice, string?>? instanceId = null, bool timers = true, GameModeController? games = null,
		Func<string?, SteamInputResult>? steamCheck = null, string? adapterResultPath = null, TimeProvider? time = null,
		Kopru.Core.Health.HealthProbe? healthProbe = null, Kopru.Core.Health.LastBatteryStore? batteryStore = null, IUrlLauncher? launcher = null)
	{
		this.time = time ?? TimeProvider.System;
		this.steamCheck = steamCheck;
		this.adapterResultPath = adapterResultPath;
		GameCatalog catalog = games?.Catalog ?? GameCatalog.Load(Kopru.Core.AppPaths.GameList);
		Games = games ?? new GameModeController(catalog, new GameWatcher(catalog, steam: SteamLibrary.CreateDefault()), host);
		Games.StateChanged += _ => Raise();
		Games.MemoryChanged += Raise;
		this.host = host;
		this.hiding = hiding;
		this.finder = finder;
		this.post = post ?? (a => Dispatcher.UIThread.Post(a));
		this.instanceId = instanceId ?? (d => DeviceInstance.FromInterfacePath(d.Path));
		this.healthProbe = healthProbe;
		this.batteryStore = batteryStore;
		this.launcher = launcher ?? new ShellUrlLauncher();
		host.StatusUpdated += status =>
		{
			RecordBattery(status);
			Raise();
		};
		host.ControllerOpened = OnControllerOpenedAsync;
		host.DetailsRead += OnDetailsRead;
		host.RawInput += body => OnHostRawInput(1, body);
		host.PadTypeChanged += type => RaiseNotice(new ServiceNotice(ServiceNoticeKind.PadTypeChanged, type.ToString()));
		host.Shortcuts.Triggered += action =>
		{
			AppLog.Write("shortcut: " + action);
			Shortcut?.Invoke(this, action);
		};
		host.MicButtonPressed += OnMicButton;
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
				RaiseNotice(new ServiceNotice(ServiceNoticeKind.BridgeError, NotificationCenter.BridgeReason(host.Status.LastError)));
			}
			RequestHealthRefresh();
		};
		if (timers)
		{
			presenceTimer = new Timer(_ => RefreshPresence(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
			meterTimer = new Timer(_ => MeterTick(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
			gameTimer = new Timer(_ => GameTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
			healthTimer = new Timer(_ => RequestHealthRefresh(), null, TimeSpan.Zero, HealthInterval);
		}
		Localizer.LanguageChanged += OnLanguageChanged;
	}

	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public static RealControllerService CreateDefault() =>
		new(BridgeHost.CreateDefault(), HidHideHidingService.CreateDefault(), new HidControllerFinder(),
			steamCheck: SteamInputCheck.CreateDefault().Check,
			adapterResultPath: Path.Combine(Kopru.Core.AppPaths.DataDirectory, DiagnosticsExport.AdapterResultFileName),
			healthProbe: Kopru.Core.Health.HealthProbe.CreateDefault(), batteryStore: Kopru.Core.Health.LastBatteryStore.CreateDefault());

	private BridgeStatus Status => Selected.Status;

	private HidDevice? Device => SelectedDevice;

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
	public bool HeadphonesPlugged => host.HeadphonesPlugged;

	public bool IsHapticsActive => Status.HapticsReportsPerSecond > 1;
	public bool IsGameConnected => started && Selected.State == BridgeState.Running;
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

	public event EventHandler<ShortcutAction>? Shortcut;

	public void ShowProfileIndicator(int index) =>
		_ = Task.Run(() => Selected.ShowPlayerLeds(Kopru.Core.Profiles.ProfileFeedback.PlayerLeds(index)));

	// Start and stop are serialized: a start and a stop (or two starts from the UI, a fix and the cable switch) never
	// interleave their sequences; each one sees the state the previous one left.
	private readonly SemaphoreSlim bridgeGate = new(1, 1);

	// From the UI (Overview, sidebar, tray) or the automatic start; the app's own restarts pass their reason.
	public Task StartBridgeAsync() => StartBridgeAsync("UI or auto start");

	public Task StopBridgeAsync() => StopBridgeAsync("UI (Overview, sidebar or tray)");

	public async Task StartBridgeAsync(string reason)
	{
		await bridgeGate.WaitAsync().ConfigureAwait(false);
		try
		{
			await StartBridgeCoreAsync(reason).ConfigureAwait(false);
		}
		finally
		{
			bridgeGate.Release();
		}
	}

	public async Task StopBridgeAsync(string reason)
	{
		await bridgeGate.WaitAsync().ConfigureAwait(false);
		try
		{
			await StopBridgeCoreAsync(reason).ConfigureAwait(false);
		}
		finally
		{
			bridgeGate.Release();
		}
	}

	private async Task StartBridgeCoreAsync(string reason)
	{
		if (started)
		{
			return;
		}
		AppLog.Write("virtual controller start: " + reason);
		started = true;
		hidHideAppAllowed = null;
		controllerHidden = null;
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
				hidHideAppAllowed = true;
			}
			catch (HidHideException e)
			{
				hidHideAppAllowed = false;
				SetHidingWarning(Text.HidingFailed(e.Message));
			}
		}
		await host.StartAsync(reason).ConfigureAwait(false);
		await StartOthersAsync().ConfigureAwait(false);
		Raise();
		RequestHealthRefresh();
	}

	private async Task StopBridgeCoreAsync(string reason)
	{
		if (!started)
		{
			return;
		}
		AppLog.Write("virtual controller stop: " + reason);
		started = false;
		await StopOthersAsync(reason).ConfigureAwait(false);
		await host.StopAsync(reason).ConfigureAwait(false);
		await RestoreHidingAsync().ConfigureAwait(false);
		HapticsLeftLevel = HapticsRightLevel = 0;
		Raise();
		lastBatteryProbe = null;
		RefreshPresence();
		RequestHealthRefresh();
	}

	// Nothing here restarts the bridge: every setting is applied to the running connection in place (speaker and mic
	// included). Settings equal to the ones applied last are ignored; speaker, mic and light changes go through one
	// ordered, coalescing worker per host (LatestValueWorker), so quick toggles end in the state the user left.
	public void ApplySettings(ControllerSettings value)
	{
		if (settingsApplied && value == settings)
		{
			return;
		}
		settingsApplied = true;
		settings = value;
		Games.SetProfiles(value.Supported, value.Unsupported);
		host.IdleMinutes = value.IdlePowerOffMinutes;
		host.PowerOffWhenIdle = value.PowerOffWhenIdle;
		host.Shortcuts.Enabled = value.ShortcutsEnabled;
		host.Desktop.Settings = value.Desktop;
		host.Desktop.Active = value.DesktopModeOn;
		InputTransform t = host.InputTransform;
		if (t.Tuning != value.Tuning || t.RemapSettings != value.Remap || t.GyroSettings != value.Gyro)
		{
			host.InputTransform = new InputTransform(value.Tuning, value.Remap, value.Gyro);
		}
		// Slot 1 with its own profile gets its speaker, mic and light from ApplyOthers.
		if (OwnSettings(1) == null)
		{
			HostAppliers primary = AppliersFor(host);
			primary.Speaker.Post(value.Speaker);
			primary.Mic.Post(value.Mic);
			primary.Light.Post(value.Light);
		}
		ApplyOthers();
	}

	public double MicLevel => Selected.MicLevel;

	public void TestHaptics(HapticSide side) =>
		RunTest(() => Selected.TestHapticsAsync(side != HapticSide.Right, side != HapticSide.Left, HapticsTestLength));

	// Rumble is played as haptics over Bluetooth (rumble bits would switch the game's haptics off).
	public void TestRumble() =>
		RunTest(() => Selected.TestRumbleAsync(settings.RumbleFeel, settings.RumbleLevel / 100, HapticsTestLength));

	public void TestTrigger(TriggerTestEffect effect)
	{
		byte[] bytes = TriggerEffect(effect, settings.TriggerStrength);
		RunTest(() => Selected.TestTriggersAsync(bytes, effect == TriggerTestEffect.Off ? TimeSpan.Zero : TriggerTestLength));
	}

	public void TestFallbackTrigger()
	{
		byte[] bytes = GameProfiles.Effect(settings.FallbackTrigger, settings.FallbackStrength / 100);
		RunTest(() => Selected.TestTriggersAsync(bytes, settings.FallbackTrigger == FallbackTrigger.Off ? TimeSpan.Zero : TriggerTestLength));
	}

	public void TestLight(uint rgb)
	{
		LightSettings light = (settings with { LightbarColour = rgb }).Light;
		BridgeHost target = Selected;
		// Through the host's light worker: the next settings push puts the user's light back.
		AppliersFor(target).Light.Post(BridgeManager.LightForSlot(light, target.Slot));
	}

	// Test effects at the trigger strength setting (0-100 % maps to the effects' 1-8 steps).
	public static byte[] TriggerEffect(TriggerTestEffect effect, double strengthPercent) =>
		GameProfiles.Effect((FallbackTrigger)effect, strengthPercent / 100);

	public async Task<AdapterTestResult> TestAdapterAsync(CancellationToken cancel = default)
	{
		bool wasRunning = started;
		if (wasRunning)
		{
			await StopBridgeAsync("adapter test").ConfigureAwait(false);
		}
		AdapterReport? report;
		try
		{
			report = await host.RunAdapterCheckAsync(AdapterPhase, AdapterPhase, cancel).ConfigureAwait(false);
		}
		finally
		{
			if (wasRunning)
			{
				await StartBridgeAsync("adapter test finished").ConfigureAwait(false);
			}
		}
		cancel.ThrowIfCancellationRequested();
		AdapterTestResult result = Describe(report);
		if (adapterResultPath != null)
		{
			WriteAdapterResult(adapterResultPath, report, result);
		}
		RequestHealthRefresh();
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
		void OnDirect(InputSnapshot snapshot) => OnInput(snapshot.ToTransformBody());
		BridgeHost source = Selected;
		source.RawInput += OnInput;
		// Bridge stopped: read the controller directly, through the test page's session when it is open.
		DirectInput += OnDirect;
		IDisposable? own = !started && Volatile.Read(ref liveSessions) == 0 ? OpenLiveInput(_ => { }) : null;
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
			source.RawInput -= OnInput;
			DirectInput -= OnDirect;
			own?.Dispose();
		}
		progress?.Report(1);
		lock (sync)
		{
			return calibrator.Finish();
		}
	}

	public Task<bool> PowerOffAsync() => Selected.PowerOffAsync("app (Controller page, tray or PS + Triangle shortcut)");

	public Task ExportDiagnosticsAsync(string path) =>
		DiagnosticsExport.CreateZipAsync(path, DiagnosticsSources.Default(Device is { } d ? ControllerInfo.From(d) : null));

	public static AdapterTestResult Describe(AdapterReport? report) => report == null
		? new AdapterTestResult(null, Text.AdapterTestNoData())
		: new AdapterTestResult(report.Verdict, report.VerdictText + Environment.NewLine + string.Join(Environment.NewLine, report.Reasons));

	public async Task ShutdownAsync()
	{
		Dispose();
		started = false;
		await StopOthersAsync("app exit").ConfigureAwait(false);
		await host.StopAsync("app exit").ConfigureAwait(false);
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
			healthTimer?.Dispose();
			Localizer.LanguageChanged -= OnLanguageChanged;
		}
	}

	// Looks for a paired controller while the bridge does not hold one (listing only; nothing is opened).
	public void RefreshPresence()
	{
		DeviceTick();
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
			if (present == null)
			{
				lastBatteryProbe = null;
			}
			present = found;
			Raise();
		}
		ProbeBatteryIfDue();
	}

	// While the bridge is stopped and a controller is paired: reads the battery on connect, right after the bridge
	// stopped, then every BatteryProbeInterval. Returns the probe started, or null.
	public bool IsReadingBattery => Volatile.Read(ref batteryProbing) != 0;

	public Task? ProbeBatteryIfDue()
	{
		if (started || present == null || host.Device != null || Volatile.Read(ref disposed) != 0)
		{
			return null;
		}
		DateTimeOffset now = time.GetUtcNow();
		if (lastBatteryProbe is { } last && now - last < BatteryProbeInterval)
		{
			return null;
		}
		if (Interlocked.Exchange(ref batteryProbing, 1) != 0)
		{
			return null;
		}
		lastBatteryProbe = now;
		return Task.Run(async () =>
		{
			try
			{
				if (await host.ProbeBatteryAsync().ConfigureAwait(false) != null)
				{
					Raise();
				}
			}
			catch (Exception)
			{
				// Tried again on the next interval.
			}
			finally
			{
				Volatile.Write(ref batteryProbing, 0);
			}
		});
	}

	// Takes the haptics levels actually sent since the last tick.
	public void MeterTick()
	{
		(double left, double right) = Selected.TakeHapticsLevels();
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
			RequestHealthRefresh();
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
			controllerHidden = true;
		}
		catch (Exception e) when (e is HidHideException or ArgumentException)
		{
			controllerHidden = false;
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
