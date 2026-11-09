// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bluetooth;
using Kopru.Core.Hid;
using Kopru.Core.Localization;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

public enum BridgeState
{
	Stopped,
	WaitingForController,
	Connecting,
	Running,
	Error
}

public enum BridgeError
{
	None,
	NoCapture,
	CaptureInvalid,
	UsbIpMissing,
	AttachFailed,
	ControllerOpenFailed,
	ControllerLost
}

// What the UI shows. Battery is null until the first input report.
// Latency (null until measured, see LatencyMeter): link extra delay p50/p99 and our relay time, in ms.
public sealed record BridgeStatus(BridgeState State, BatteryStatus? Battery, double InputRateHz, double HapticsReportsPerSecond,
	BridgeError LastError, string? LastErrorDetail, double? LinkLatencyP50Ms = null, double? LinkLatencyP99Ms = null, double? RelayMs = null)
{
	public string? LastErrorText(Language? language = null) => BridgeHost.Describe(LastError, language);

	public string StateText(Language? language = null) => BridgeHost.Describe(State, language);
}

// The bridge as a service: waits for the Bluetooth DualSense, then runs it as a virtual wired DualSense (USB/IP server
// on the loopback, plugged in with the USB/IP client) until it is lost, unplugs the virtual pad and waits again.
// Events are raised on the host's background task.
public sealed partial class BridgeHost : IAsyncDisposable, IGameBridge
{
	public const double DefaultGameHapticsLevel = 1.5;

	public const double DefaultIdleMinutes = 15;

	public const int LowBatteryPercent = 15;

	private readonly IControllerSource controllers;

	private readonly IIdentitySource identities;

	private readonly IUsbIpAttacher attacher;

	private readonly TimeProvider time;

	private readonly object gate = new();

	private CancellationTokenSource? cancel;

	private Task? supervisor;

	private Connection? current;

	private BridgeState state = BridgeState.Stopped;

	private BridgeError lastError;

	private string? lastErrorDetail;

	private BatteryStatus? battery;

	private double inputRate, hapticsRate;

	private Diagnostics.LatencyReading? latency;

	private bool lowBatteryRaised;

	private double gameHapticsLevel = DefaultGameHapticsLevel;

	private double rumbleLevel = ControllerBridge.DefaultRumbleLevel;

	private BridgeProfile profile = BridgeProfile.Default;

	public BridgeHost(IControllerSource controllers, IIdentitySource identities, IUsbIpAttacher attacher, TimeProvider? time = null)
	{
		this.controllers = controllers;
		this.identities = identities;
		this.attacher = attacher;
		this.time = time ?? TimeProvider.System;
	}

	public static BridgeHost CreateDefault() => new(new WindowsControllerSource(), IdentitySources.Default(), new UsbIpExeAttacher());

	// How often to look for the controller while waiting (and the pause after losing it).
	public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

	// How often status, battery, idle and the input watchdog are checked while running.
	public TimeSpan MonitorInterval { get; set; } = TimeSpan.FromMilliseconds(250);

	// No input report for this long counts as a lost controller (it sends ~250 per second). Infinite turns it off.
	public TimeSpan NoInputTimeout { get; set; } = TimeSpan.FromSeconds(3);

	// Minutes without input changes before IdleTimeout; 0 turns it off.
	public double IdleMinutes { get; set; } = DefaultIdleMinutes;

	public double GameHapticsLevel
	{
		get => gameHapticsLevel;
		set
		{
			gameHapticsLevel = value;
			Connection? c = current;
			if (c != null)
			{
				c.Bridge.GameHapticsLevel = value;
			}
		}
	}

	// The profile every connection runs with (GameModeController sets it when the game or mode changes).
	public BridgeProfile Profile => profile;

	// Applies the profile now and on every later connection; GameHapticsLevel and RumbleLevel follow it.
	public void ApplyProfile(BridgeProfile value)
	{
		profile = value;
		gameHapticsLevel = value.GameHapticsLevel;
		rumbleLevel = value.RumbleLevel;
		Connection? c = current;
		if (c != null)
		{
			try
			{
				c.Bridge.ApplyProfile(value);
			}
			catch (Exception)
			{
				// The controller is going away; the profile is applied again on the next connect.
			}
			if (!value.LightFromGame)
			{
				ApplyLightTo(c.Session);
			}
		}
	}

	// What the game sent since the last call; nothing while no game is connected.
	public GameSignals TakeGameSignals() => current?.Bridge.TakeGameSignals() ?? default;

	public double RumbleLevel
	{
		get => rumbleLevel;
		set
		{
			rumbleLevel = value;
			Connection? c = current;
			if (c != null)
			{
				c.Bridge.RumbleLevel = value;
			}
		}
	}

	public BridgeState State
	{
		get
		{
			lock (gate)
			{
				return state;
			}
		}
	}

	public BridgeStatus Status
	{
		get
		{
			lock (gate)
			{
				return new BridgeStatus(state, battery, inputRate, hapticsRate, lastError, lastErrorDetail,
					latency?.LinkP50Ms, latency?.LinkP99Ms, latency?.RelayMs);
			}
		}
	}

	public event Action<BridgeState>? StateChanged;

	// Raised after every monitor pass while running, and on state changes.
	public event Action<BridgeStatus>? StatusUpdated;

	// Raised once per discharge when the battery reaches LowBatteryPercent or less; the argument is the percentage.
	public event Action<int>? LowBattery;

	// Raised once after IdleMinutes without input changes; again only after the controller was used.
	public event Action? IdleTimeout;

	public static string Describe(BridgeState state, Language? language = null) => state switch
	{
		BridgeState.WaitingForController => Text.BridgeWaiting(language),
		BridgeState.Connecting => Text.BridgeConnecting(language),
		BridgeState.Running => Text.BridgeRunning(language),
		BridgeState.Error => Text.BridgeError(language),
		_ => Text.BridgeStopped(language)
	};

	public static string? Describe(BridgeError error, Language? language = null) => error switch
	{
		BridgeError.NoCapture => Text.BridgeNoCapture(language),
		BridgeError.CaptureInvalid => Text.BridgeCaptureInvalid(language),
		BridgeError.UsbIpMissing => Text.BridgeUsbIpMissing(language),
		BridgeError.AttachFailed => Text.BridgeAttachFailed(language),
		BridgeError.ControllerOpenFailed => Text.BridgeControllerOpenFailed(language),
		BridgeError.ControllerLost => Text.BridgeControllerLost(language),
		_ => null
	};

	public string IdleMessage(Language? language = null) => Text.IdleTimeout(IdleMinutes, language);

	// Starts the service; does nothing if it is already running or waiting. Waits for a running test to finish.
	public async Task StartAsync()
	{
		await sessionGate.WaitAsync().ConfigureAwait(false);
		try
		{
			await StartCore().ConfigureAwait(false);
		}
		finally
		{
			sessionGate.Release();
		}
	}

	private Task StartCore()
	{
		lock (gate)
		{
			if (supervisor != null)
			{
				return Task.CompletedTask;
			}
			lastError = BridgeError.None;
			lastErrorDetail = null;
		}
		DeviceIdentity? identity;
		try
		{
			identity = identities.Load();
		}
		catch (Exception e)
		{
			Fail(BridgeError.CaptureInvalid, e.Message);
			return Task.CompletedTask;
		}
		if (identity == null)
		{
			Fail(BridgeError.NoCapture, null);
			return Task.CompletedTask;
		}
		lock (gate)
		{
			if (supervisor != null)
			{
				return Task.CompletedTask;
			}
			cancel = new CancellationTokenSource();
			CancellationToken token = cancel.Token;
			supervisor = Task.Run(() => SuperviseAsync(identity, token));
		}
		return Task.CompletedTask;
	}

	// Unplugs the virtual pad, closes the controller and returns to Stopped; does nothing when already stopped.
	public async Task StopAsync()
	{
		Task? running;
		CancellationTokenSource? stopping;
		lock (gate)
		{
			running = supervisor;
			stopping = cancel;
			supervisor = null;
			cancel = null;
		}
		if (running != null)
		{
			stopping!.Cancel();
			await running.ConfigureAwait(false);
			stopping.Dispose();
		}
		SetState(BridgeState.Stopped);
	}

	public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

	private async Task SuperviseAsync(DeviceIdentity identity, CancellationToken token)
	{
		try
		{
			while (!token.IsCancellationRequested)
			{
				SetState(BridgeState.WaitingForController);
				IHidLink? link;
				while ((link = controllers.TryOpen()) == null)
				{
					await Task.Delay(PollInterval, token).ConfigureAwait(false);
				}
				if (token.IsCancellationRequested)
				{
					link.Dispose();
					return;
				}
				SetState(BridgeState.Connecting);
				Connection connection = new(link, identity, this);
				try
				{
					connection.Session.Start();
				}
				catch (Exception e)
				{
					await connection.CloseAsync().ConfigureAwait(false);
					Record(BridgeError.ControllerOpenFailed, e.Message);
					await Task.Delay(PollInterval, token).ConfigureAwait(false);
					continue;
				}
				ApplyLightTo(connection.Session);
				if (profile.FallbackTrigger != null)
				{
					connection.Bridge.ApplyProfile(profile);
				}
				if (ControllerOpened is { } opened)
				{
					try
					{
						await opened(link.Device, token).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						await connection.CloseAsync().ConfigureAwait(false);
						return;
					}
					catch (Exception)
					{
						// The hook reports its own failures; the bridge runs anyway.
					}
				}
				try
				{
					await connection.AttachAsync(token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					await connection.CloseAsync().ConfigureAwait(false);
					return;
				}
				catch (Exception e)
				{
					await connection.CloseAsync().ConfigureAwait(false);
					Fail(e is FileNotFoundException ? BridgeError.UsbIpMissing : BridgeError.AttachFailed, e.Message);
					return;
				}
				current = connection;
				SetState(BridgeState.Running);
				try
				{
					while (!token.IsCancellationRequested && !Monitor(connection))
					{
						await Task.Delay(MonitorInterval, token).ConfigureAwait(false);
					}
				}
				catch (OperationCanceledException)
				{
					// Stopping.
				}
				current = null;
				await connection.CloseAsync().ConfigureAwait(false);
				if (connection.PowerOff is { } powerOff)
				{
					await FinishPowerOffAsync(powerOff).ConfigureAwait(false);
				}
				if (token.IsCancellationRequested)
				{
					return;
				}
				if (connection.PowerOff == null)
				{
					Record(BridgeError.ControllerLost, null);
				}
				SetState(BridgeState.WaitingForController);
				await Task.Delay(PollInterval, token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			// Stopping.
		}
		finally
		{
			Connection? left = current;
			current = null;
			if (left != null)
			{
				await left.CloseAsync().ConfigureAwait(false);
			}
		}
	}

	// One pass while running: rates, battery, idle; true when the controller is lost.
	private bool Monitor(Connection c)
	{
		DateTimeOffset now = time.GetUtcNow();
		long inputs = c.Session.InputReports;
		long haptics = c.Session.HapticsReports;
		if (inputs != c.LastInputCount)
		{
			c.LastInputCount = inputs;
			c.LastInputAt = now;
		}
		TimeSpan timeout = NoInputTimeout;
		if (c.Lost || c.PowerOff != null || (timeout >= TimeSpan.Zero && now - c.LastInputAt > timeout))
		{
			return true;
		}
		double seconds = (now - c.RateSince).TotalSeconds;
		BatteryStatus? reading = inputs > 0 ? BatteryStatus.From(c.Session.LastInput) : null;
		bool lowNow = false;
		lock (gate)
		{
			if (seconds >= 1)
			{
				inputRate = (inputs - c.RateInputs) / seconds;
				hapticsRate = (haptics - c.RateHaptics) / seconds;
				c.RateSince = now;
				c.RateInputs = inputs;
				c.RateHaptics = haptics;
				latency = c.Latency.Snapshot();
			}
			if (reading is { } b)
			{
				battery = b;
				if (b.State is ChargeState.Charging or ChargeState.Full)
				{
					lowBatteryRaised = false;
				}
				else if (b.State == ChargeState.Discharging && b.Percent <= LowBatteryPercent && !lowBatteryRaised)
				{
					lowBatteryRaised = lowNow = true;
				}
			}
		}
		if (lowNow)
		{
			LowBattery?.Invoke(reading!.Value.Percent);
		}
		double idle = IdleMinutes;
		if (idle > 0 && !c.IdleRaised && now - c.LastActivityAt >= TimeSpan.FromMinutes(idle))
		{
			c.IdleRaised = true;
			IdleTimeout?.Invoke();
			if (PowerOffWhenIdle)
			{
				_ = RequestPowerOff(c, true);
			}
		}
		StatusUpdated?.Invoke(Status);
		return false;
	}

	private void OnActivity(Connection c)
	{
		c.LastActivityAt = time.GetUtcNow();
		c.IdleRaised = false;
	}

	private void Record(BridgeError error, string? detail)
	{
		lock (gate)
		{
			lastError = error;
			lastErrorDetail = detail;
		}
		Diagnostics.AppLog.Write($"error {error}: {detail}");
	}

	private void Fail(BridgeError error, string? detail)
	{
		Record(error, detail);
		SetState(BridgeState.Error);
	}

	private void SetState(BridgeState next)
	{
		lock (gate)
		{
			if (state == next)
			{
				return;
			}
			state = next;
			if (next != BridgeState.Running)
			{
				inputRate = hapticsRate = 0;
				latency = null;
			}
		}
		Diagnostics.AppLog.Write($"state {next}");
		StateChanged?.Invoke(next);
		StatusUpdated?.Invoke(Status);
	}

	// One run of the bridge for one controller connection.
	private sealed class Connection
	{
		private readonly BridgeHost host;

		private readonly VirtualDualSense pad;

		private UsbIpServer? server;

		private int? port;

		private int closed;

		private readonly byte[] lastActivityInput = new byte[InputActivity.Size];

		public Connection(IHidLink link, DeviceIdentity identity, BridgeHost host)
		{
			this.host = host;
			pad = new VirtualDualSense(CalibrationOverlay.Apply(identity, link));
			Session = new BluetoothSession(link);
			Bridge = new ControllerBridge(Session, pad)
			{
				RumbleFeel = host.profile.Feel,
				LightFromGame = host.profile.LightFromGame,
				TriggerIntensity = host.profile.TriggerIntensity,
				FallbackTrigger = host.profile.FallbackTrigger,
				GameHapticsLevel = host.gameHapticsLevel,
				RumbleLevel = host.rumbleLevel,
				InputTransform = host.inputTransform
			};
			DateTimeOffset now = host.time.GetUtcNow();
			LastInputAt = LastActivityAt = RateSince = now;
			Session.Lost += () => Lost = true;
			Session.InputBodyReceived += OnInput;
		}

		public BluetoothSession Session { get; }

		public ControllerBridge Bridge { get; }

		public volatile bool Lost;

		public long LastInputCount;

		public DateTimeOffset LastInputAt;

		public DateTimeOffset LastActivityAt;

		public bool IdleRaised;

		public PowerOffRequest? PowerOff;

		public readonly Diagnostics.LatencyMeter Latency = new();

		public DateTimeOffset RateSince;

		public long RateInputs, RateHaptics;

		public async Task AttachAsync(CancellationToken token)
		{
			server = new UsbIpServer(pad);
			server.Start();
			port = await host.attacher.AttachAsync(server.Port, VirtualDualSense.BusId, token).ConfigureAwait(false);
		}

		// Unplug first, so games never see a pad that stopped answering; then the bridge, server, controller and pad.
		public async Task CloseAsync()
		{
			if (Interlocked.Exchange(ref closed, 1) != 0)
			{
				return;
			}
			if (port is int p)
			{
				try
				{
					await host.attacher.DetachAsync(p).ConfigureAwait(false);
				}
				catch (Exception)
				{
					// Already gone; the server closing below ends the connection anyway.
				}
			}
			Session.InputBodyReceived -= OnInput;
			Bridge.Dispose();
			if (server != null)
			{
				await server.DisposeAsync().ConfigureAwait(false);
			}
			Session.Dispose();
			pad.Dispose();
		}

		// Subscribed after ControllerBridge, so the virtual pad already has this report: the relay time ends here.
		private void OnInput(ReadOnlySpan<byte> body)
		{
			Protocol.InputState input = Session.LastInput;
			Latency.Record(Session.LastReadTicks, input.Sequence, input.SensorTicks, System.Diagnostics.Stopwatch.GetTimestamp());
			host.RawInput?.Invoke(body);
			if (InputActivity.Changed(lastActivityInput, body))
			{
				host.OnActivity(this);
			}
		}
	}
}
