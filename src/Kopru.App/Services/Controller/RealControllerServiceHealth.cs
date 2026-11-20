// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Health;
using Kopru.Core.Hid;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.App.Services;

// Health: connection sub-state, chips, blocking issue, last-known battery and fixes. The report is built off the UI
// thread every HealthInterval and after bridge, hiding, Steam Input and adapter events.
public sealed partial class RealControllerService
{
	public static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(5);

	// Without a probe (tests): driver assumed present, radio on, nothing paired.
	private static readonly HealthEnvironment AssumedEnvironment = new(
		new ComponentInfo(true, null, null, true), new ComponentInfo(true, null, null, true), RadioState.On, [], null, null);

	private readonly HealthProbe? healthProbe;
	private readonly LastBatteryStore? batteryStore;
	private readonly IUrlLauncher launcher;
	private readonly Timer? healthTimer;
	private readonly SemaphoreSlim healthGate = new(1, 1);
	private volatile HealthReport health = HealthReport.Empty;
	private HealthEnvironment? lastEnvironment;
	private bool? hidHideAppAllowed;
	private bool? controllerHidden;

	public ConnectionState Connection => health.Connection;
	public HealthReport Health => health;
	public LastKnownBattery? LastKnownBattery => batteryStore?.Current;
	public bool CanStartBridge => health.CanStartBridge;
	public string CanStartBridgeReason => health.CanStartBridgeReason;
	public bool CanTest => health.CanTest;
	public string CanTestReason => health.CanTestReason;

	public event EventHandler<FixAction>? FixRequested;

	// Turns the Bluetooth radio on ("Turn On Bluetooth"); null: the fix does nothing (tests).
	public Func<Task>? TurnOnBluetooth { get; set; }

	public async Task ExecuteFix(FixAction fix)
	{
		switch (fix)
		{
			case FixAction.None:
				return;
			case FixAction.RestartBridge:
				await StopBridgeAsync("health fix: restart").ConfigureAwait(false);
				await StartBridgeAsync("health fix: restart").ConfigureAwait(false);
				return;
			case FixAction.ReconnectController:
				await ReconnectAsync().ConfigureAwait(false);
				return;
			case FixAction.OpenBluetoothSettings:
				// Linux: "bluetoothctl power on" (set by the app; desktops differ in how their Bluetooth settings open). A radio
				// blocked by rfkill stays off; the reason text says how to unblock it.
				if (TurnOnBluetooth is { } turnOn)
				{
					await turnOn().ConfigureAwait(false);
					RequestHealthRefresh();
				}
				return;
			default:
				post(() => FixRequested?.Invoke(this, fix));
				return;
		}
	}

	// Starts a refresh on the thread pool (skipped when one is already running). full: the slow system facts (installed
	// drivers, pending restart, adapter test file) are read again too; the periodic refresh reads only the radio and the
	// paired list (HealthProbe reads the rest at most every HealthProbe.StaticInterval).
	public void RequestHealthRefresh(bool full = true)
	{
		if (full)
		{
			// Kept until a refresh does it (also when this request is skipped because one is running).
			Volatile.Write(ref healthFullPending, 1);
		}
		// Skipped while a refresh runs; a direct RefreshHealthAsync call waits for it instead (it must see the latest state).
		if (Volatile.Read(ref disposed) == 0 && healthGate.CurrentCount > 0)
		{
			_ = Task.Run(() => RefreshHealthAsync(full: false));
		}
	}

	// The setup page opened or ran a step: everything is read again.
	public void RefreshHealth() => RequestHealthRefresh(full: true);

	private int healthFullPending = 1;

	// Rebuilds the report; reads the system (registry, radio, paired list) unless reuseEnvironment; full (or a full
	// request pending): the slow facts too. Returns the report.
	public async Task<HealthReport> RefreshHealthAsync(bool reuseEnvironment = false, bool full = true)
	{
		await healthGate.WaitAsync().ConfigureAwait(false);
		try
		{
			HealthEnvironment env;
			if (reuseEnvironment && lastEnvironment != null)
			{
				env = lastEnvironment;
			}
			else if (healthProbe != null)
			{
				full |= Interlocked.Exchange(ref healthFullPending, 0) != 0;
				env = healthProbe.Read(full, out bool readStatic);
				Kopru.Core.Diagnostics.BackgroundWork.HealthRefresh(readStatic);
			}
			else
			{
				env = AssumedEnvironment;
			}
			lastEnvironment = env;
			List<HidDevice> controllers;
			try
			{
				controllers = [.. finder.Find()];
			}
			catch (Exception)
			{
				controllers = [];
			}
			if (host.Device is { } held && !controllers.Contains(held))
			{
				controllers.Insert(0, held);
			}
			BridgeStatus status = host.Status;
			HealthRuntime run = new(controllers, started, status.State, status.LastError, steamInput, hidHideAppAllowed, controllerHidden);
			HealthReport next = HealthReport.Build(env, run, time.GetUtcNow());
			if (!SameReport(next, health))
			{
				health = next;
				Raise();
			}
			return next;
		}
		finally
		{
			healthGate.Release();
		}
	}

	private static bool SameReport(HealthReport a, HealthReport b) =>
		a.Connection == b.Connection && a.BlockingIssue == b.BlockingIssue && a.CanTestReason == b.CanTestReason && a.Chips.SequenceEqual(b.Chips);

	private void RecordBattery(BridgeStatus status)
	{
		if (batteryStore != null && status.Battery is { } b && batteryStore.Record(b, time.GetUtcNow()))
		{
			Raise();
		}
	}

	private void OnLanguageChanged(Language language) => _ = Task.Run(() => RefreshHealthAsync(reuseEnvironment: true, full: false));
}
