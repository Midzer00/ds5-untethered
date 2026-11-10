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
				try
				{
					launcher.Open(SetupLinks.BluetoothSettings);
				}
				catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
				{
					// Nothing else to do; the reason text says where the setting is.
				}
				return;
			default:
				post(() => FixRequested?.Invoke(this, fix));
				return;
		}
	}

	// Starts a refresh on the thread pool (skipped when one is already running).
	public void RequestHealthRefresh()
	{
		// Skipped while a refresh runs; a direct RefreshHealthAsync call waits for it instead (it must see the latest state).
		if (Volatile.Read(ref disposed) == 0 && healthGate.CurrentCount > 0)
		{
			_ = Task.Run(() => RefreshHealthAsync());
		}
	}

	// Rebuilds the report; reads the system (registry, radio, paired list) unless reuseEnvironment. Returns the report.
	public async Task<HealthReport> RefreshHealthAsync(bool reuseEnvironment = false)
	{
		await healthGate.WaitAsync().ConfigureAwait(false);
		try
		{
			HealthEnvironment env = reuseEnvironment && lastEnvironment != null ? lastEnvironment : healthProbe?.Read() ?? AssumedEnvironment;
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

	private void OnLanguageChanged(Language language) => _ = Task.Run(() => RefreshHealthAsync(reuseEnvironment: true));
}
