// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.Versioning;
using System.Text.Json;
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Hid;
using Kopru.Core.Localization;
using Kopru.Core.Setup;
using Kopru.Core.Steam;

namespace Kopru.Core.Health;

public enum HealthLevel
{
	Ok,
	Unknown,
	Warning,
	Error
}

public enum HealthChipKind
{
	Driver,
	HidHide,
	SteamInput,
	Adapter,
	Bridge
}

// What a chip's (or the blocking issue's) button does. The UI handles the page ones (IControllerService.FixRequested).
public enum FixAction
{
	None,
	// Opens the setup wizard at the usbip-win2 step.
	InstallDriver,
	InstallHidHide,
	OpenSteamInputHelp,
	RunAdapterTest,
	OpenBluetoothSettings,
	PairController,
	RestartBridge,
	// Tears the bridge's connection down and opens the controller again (stuck connection).
	ReconnectController
}

public enum BlockingKind
{
	DriverMissing,
	NoRadio,
	BluetoothOff,
	NeverPaired,
	UsbConnected
}

// Label: short Title Case name; Reason: one localized sentence.
public sealed record HealthChip(HealthChipKind Kind, HealthLevel Level, string Label, string Reason, FixAction Fix);

// The most severe problem that keeps the bridge from starting (header primary action).
public sealed record BlockingIssue(BlockingKind Kind, HealthLevel Level, string Label, string Reason, FixAction Fix);

// The last adapter test as stored in adapter-check.json (Verdict null = the test measured nothing).
public sealed record AdapterSummary(AdapterVerdict? Verdict, DateTimeOffset? Time)
{
	// Reads the file RealControllerService.WriteAdapterResult writes (and the diagnostics export collects). Null if never run.
	public static AdapterSummary? Load(string? path)
	{
		try
		{
			if (path == null || !File.Exists(path))
			{
				return null;
			}
			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
			JsonElement root = doc.RootElement;
			AdapterVerdict? verdict = root.TryGetProperty("Verdict", out JsonElement v) && v.ValueKind == JsonValueKind.String &&
				Enum.TryParse(v.GetString(), out AdapterVerdict parsed) ? parsed : null;
			DateTimeOffset? time = root.TryGetProperty("Time", out JsonElement t) && t.ValueKind == JsonValueKind.String &&
				t.TryGetDateTimeOffset(out DateTimeOffset at) ? at : null;
			return new AdapterSummary(verdict, time);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			return null;
		}
	}
}

// Slow, system-wide facts (registry, radio, paired list, files): read off the UI thread.
public sealed record HealthEnvironment(
	ComponentInfo UsbIp,
	ComponentInfo HidHide,
	RadioState Radio,
	IReadOnlyList<PairedController> Paired,
	bool? RestartPending,
	AdapterSummary? Adapter);

// What the running app knows. HidHideAppAllowed / ControllerHidden: null = not tried in this run.
public sealed record HealthRuntime(
	IReadOnlyList<HidDevice> Controllers,
	bool BridgeStarted,
	BridgeState BridgeState,
	BridgeError LastError,
	SteamInputResult? SteamInput,
	bool? HidHideAppAllowed = null,
	bool? ControllerHidden = null)
{
	public static HealthRuntime Idle { get; } = new([], false, BridgeState.Stopped, BridgeError.None, null);
}

// Reads HealthEnvironment from small fakeable probes.
public sealed class HealthProbe(IInstallRegistry registry, IBluetoothRadio radio, IPairedControllers paired, IRestartProbe? restart, string? adapterResultPath)
{
	[SupportedOSPlatform("windows")]
	public static HealthProbe CreateDefault() => new(new WindowsInstallRegistry(), new WindowsBluetoothRadio(), new WindowsPairedControllers(),
		new WindowsRestartProbe(), Path.Combine(AppPaths.DataDirectory, DiagnosticsExport.AdapterResultFileName));

	public HealthEnvironment Read()
	{
		SystemCheck check = new(registry, radio, new NoControllers(), restart ?? new NoRestart());
		RadioState state = Safe(radio.Query, RadioState.Missing);
		IReadOnlyList<PairedController> list = state == RadioState.On ? Safe(paired.Find, []) : [];
		return new HealthEnvironment(Safe(check.CheckUsbIp, ComponentInfo.Missing), Safe(check.CheckHidHide, ComponentInfo.Missing), state,
			list, restart == null ? null : Safe(restart.IsRestartPending, null), AdapterSummary.Load(adapterResultPath));
	}

	private static T Safe<T>(Func<T> read, T fallback)
	{
		try
		{
			return read();
		}
		catch (Exception)
		{
			return fallback;
		}
	}

	private sealed class NoControllers : IControllerFinder
	{
		public IReadOnlyList<HidDevice> Find() => [];
	}

	private sealed class NoRestart : IRestartProbe
	{
		public bool? IsRestartPending() => null;
	}
}

// Everything the overview needs to say what is wrong and what to press. Texts are in the language given to Build.
public sealed record HealthReport(
	ConnectionState Connection,
	IReadOnlyList<HealthChip> Chips,
	BlockingIssue? BlockingIssue,
	string CanStartBridgeReason,
	string CanTestReason)
{
	public static HealthReport Empty { get; } = Build(
		new HealthEnvironment(ComponentInfo.Missing, ComponentInfo.Missing, RadioState.On, [], null, null), HealthRuntime.Idle, DateTimeOffset.MinValue);

	public bool CanStartBridge => BlockingIssue == null;

	public bool CanTest => CanTestReason.Length == 0;

	public HealthChip Chip(HealthChipKind kind) => Chips.First(c => c.Kind == kind);

	// Worst chip level (Unknown counts below Warning).
	public HealthLevel Overall => Chips.Max(c => c.Level);

	public static HealthReport Build(HealthEnvironment env, HealthRuntime run, DateTimeOffset now, Language? l = null)
	{
		ConnectionState connection = ConnectionState.Detect(env.Radio, run.Controllers, env.Paired);
		HealthChip[] chips =
		[
			DriverChip(env, l),
			HidHideChip(env, run, l),
			SteamChip(run.SteamInput, l),
			AdapterChip(env.Adapter, now, l),
			BridgeChip(run, l)
		];
		BlockingIssue? blocking = Blocking(env, connection, l);
		string testReason = connection.Kind switch
		{
			ConnectionKind.ConnectedBluetooth => string.Empty,
			ConnectionKind.ConnectedUsb => Text.CannotTestUsb(l),
			_ => Text.CannotTestNoController(l)
		};
		return new HealthReport(connection, chips, blocking, blocking?.Reason ?? string.Empty, testReason);
	}

	// Priority: driver missing > no radio > Bluetooth off > never paired > on a cable. A paired controller that is off or
	// asleep does not block: the bridge starts and waits for it.
	public static BlockingIssue? Blocking(HealthEnvironment env, ConnectionState connection, Language? l = null)
	{
		if (!env.UsbIp.Ready)
		{
			return new(BlockingKind.DriverMissing, HealthLevel.Error, Text.HealthChipDriver(l), Text.CannotStartDriver(l), FixAction.InstallDriver);
		}
		return connection.Kind switch
		{
			ConnectionKind.NoRadio => new(BlockingKind.NoRadio, HealthLevel.Error, connection.Label(l), Text.CannotStartNoRadio(l), FixAction.None),
			ConnectionKind.BluetoothOff => new(BlockingKind.BluetoothOff, HealthLevel.Error, connection.Label(l), Text.CannotStartBluetoothOff(l), FixAction.OpenBluetoothSettings),
			ConnectionKind.NeverPaired => new(BlockingKind.NeverPaired, HealthLevel.Error, connection.Label(l), Text.CannotStartNotPaired(l), FixAction.PairController),
			ConnectionKind.ConnectedUsb => new(BlockingKind.UsbConnected, HealthLevel.Warning, connection.Label(l), Text.CannotStartUsb(l), FixAction.None),
			_ => null
		};
	}

	public static HealthChip DriverChip(HealthEnvironment env, Language? l = null)
	{
		string label = Text.HealthChipDriver(l);
		ComponentInfo usbIp = env.UsbIp;
		if (!usbIp.Installed)
		{
			return new(HealthChipKind.Driver, HealthLevel.Error, label, Text.HealthDriverMissing(l), FixAction.InstallDriver);
		}
		if (!usbIp.DriverPresent)
		{
			return new(HealthChipKind.Driver, HealthLevel.Error, label, Text.HealthDriverServiceMissing(l), FixAction.InstallDriver);
		}
		if (env.RestartPending == true)
		{
			return new(HealthChipKind.Driver, HealthLevel.Warning, label, Text.HealthRestartPending(l), FixAction.None);
		}
		return new(HealthChipKind.Driver, HealthLevel.Ok, label, Text.HealthDriverOk(usbIp.Version ?? Text.VersionUnknown(l), l), FixAction.None);
	}

	public static HealthChip HidHideChip(HealthEnvironment env, HealthRuntime run, Language? l = null)
	{
		string label = Text.HealthChipHiding(l);
		HealthChip Chip(HealthLevel level, string reason, FixAction fix) => new(HealthChipKind.HidHide, level, label, reason, fix);
		if (!env.HidHide.Installed)
		{
			return Chip(HealthLevel.Warning, Text.HealthHidingMissing(l), FixAction.InstallHidHide);
		}
		if (!env.HidHide.DriverPresent)
		{
			return Chip(HealthLevel.Warning, Text.HealthHidingDriverMissing(l), FixAction.InstallHidHide);
		}
		if (!run.BridgeStarted)
		{
			return Chip(HealthLevel.Ok, Text.HealthHidingReady(l), FixAction.None);
		}
		if (run.HidHideAppAllowed == false)
		{
			return Chip(HealthLevel.Warning, Text.HealthHidingAppNotAllowed(l), FixAction.RestartBridge);
		}
		if (run.ControllerHidden == false)
		{
			return Chip(HealthLevel.Warning, Text.HealthHidingNotHidden(l), FixAction.RestartBridge);
		}
		return Chip(HealthLevel.Ok, run.ControllerHidden == true ? Text.HealthHidingActive(l) : Text.HealthHidingReady(l), FixAction.None);
	}

	public static HealthChip SteamChip(SteamInputResult? steam, Language? l = null)
	{
		string label = Text.HealthChipSteamInput(l);
		HealthChip Chip(HealthLevel level, string reason, FixAction fix = FixAction.None) => new(HealthChipKind.SteamInput, level, label, reason, fix);
		return steam?.Status switch
		{
			null => Chip(HealthLevel.Unknown, Text.HealthSteamNotChecked(l)),
			SteamInputStatus.NotInstalled => Chip(HealthLevel.Ok, Text.HealthSteamNotInstalled(l)),
			SteamInputStatus.Off => Chip(HealthLevel.Ok, Text.HealthSteamOff(l)),
			SteamInputStatus.OnGlobally => Chip(HealthLevel.Warning, Text.HealthSteamOnGlobally(l), FixAction.OpenSteamInputHelp),
			SteamInputStatus.OnForApp => Chip(HealthLevel.Warning, Text.HealthSteamOnForApp(l), FixAction.OpenSteamInputHelp),
			_ => Chip(HealthLevel.Unknown, Text.HealthSteamUnknown(l), FixAction.OpenSteamInputHelp)
		};
	}

	public static HealthChip AdapterChip(AdapterSummary? adapter, DateTimeOffset now, Language? l = null)
	{
		string label = Text.HealthChipAdapter(l);
		if (adapter == null)
		{
			return new(HealthChipKind.Adapter, HealthLevel.Unknown, label, Text.HealthAdapterNeverTested(l), FixAction.RunAdapterTest);
		}
		if (adapter.Verdict is not { } verdict)
		{
			return new(HealthChipKind.Adapter, HealthLevel.Unknown, label, Text.HealthAdapterNoData(l), FixAction.RunAdapterTest);
		}
		(HealthLevel level, string text) = verdict switch
		{
			AdapterVerdict.Suitable => (HealthLevel.Ok, Text.VerdictSuitable(l)),
			AdapterVerdict.Borderline => (HealthLevel.Warning, Text.VerdictBorderline(l)),
			_ => (HealthLevel.Error, Text.VerdictUnsuitable(l))
		};
		string when = adapter.Time is { } t ? RelativeTime.Format(now - t, l) : "—";
		return new(HealthChipKind.Adapter, level, label, Text.HealthAdapterResult(text, when, l),
			level == HealthLevel.Ok ? FixAction.None : FixAction.RunAdapterTest);
	}

	public static HealthChip BridgeChip(HealthRuntime run, Language? l = null)
	{
		string label = Text.HealthChipBridge(l);
		HealthChip Chip(HealthLevel level, string reason, FixAction fix = FixAction.None) => new(HealthChipKind.Bridge, level, label, reason, fix);
		if (IsStuck(run))
		{
			return Chip(HealthLevel.Warning, Text.HealthBridgeStuck(l), FixAction.ReconnectController);
		}
		if (run.BridgeState == BridgeState.Error || (run.LastError != BridgeError.None && run.BridgeStarted))
		{
			string reason = BridgeHost.Describe(run.LastError, l) ?? Text.HealthBridgeStopped(l);
			FixAction fix = run.LastError == BridgeError.UsbIpMissing ? FixAction.InstallDriver : FixAction.RestartBridge;
			return Chip(run.BridgeState == BridgeState.Error ? HealthLevel.Error : HealthLevel.Warning, Text.HealthBridgeLastError(reason, l), fix);
		}
		return run.BridgeState switch
		{
			BridgeState.WaitingForController => Chip(HealthLevel.Warning, Text.HealthBridgeWaiting(l)),
			BridgeState.Connecting => Chip(HealthLevel.Ok, Text.HealthBridgeConnecting(l)),
			BridgeState.Running => Chip(HealthLevel.Ok, Text.HealthBridgeRunning(l)),
			_ => Chip(HealthLevel.Ok, Text.HealthBridgeStopped(l))
		};
	}

	// Fixes the service carries out itself; the rest are raised to the UI (IControllerService.FixRequested).
	public static bool IsHandledByService(FixAction fix) =>
		fix is FixAction.RestartBridge or FixAction.OpenBluetoothSettings or FixAction.ReconnectController;

	// Stuck: the bridge waits for a controller that Windows lists over Bluetooth, after losing it or failing to open it.
	public static bool IsStuck(HealthRuntime run) =>
		run.BridgeStarted && run.BridgeState == BridgeState.WaitingForController &&
		run.LastError is BridgeError.ControllerLost or BridgeError.ControllerOpenFailed &&
		run.Controllers.Any(c => c.Transport == Transport.Bluetooth);
}
