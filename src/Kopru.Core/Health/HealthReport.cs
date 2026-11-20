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
	// HidHide is required: without it games see the real and the virtual controller.
	HidHideMissing,
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

// Slow, system-wide facts (radio, paired list, the Linux checks, files): read off the UI thread. The driver chip and the
// blocking issue follow the first missing requirement (none: nothing missing); hiding is shown as not available yet.
public sealed record HealthEnvironment(
	ComponentInfo UsbIp,
	ComponentInfo HidHide,
	RadioState Radio,
	IReadOnlyList<PairedController> Paired,
	bool? RestartPending,
	AdapterSummary? Adapter,
	IReadOnlyList<SetupRequirement>? Requirements = null)
{
	// The first missing Linux requirement that keeps the virtual controller from being plugged in, then any missing one.
	public SetupRequirement? FirstBlockingMissing => Requirements?.FirstOrDefault(r => !r.Ready && r.BlocksBridge);
	public SetupRequirement? FirstMissing => FirstBlockingMissing ?? Requirements?.FirstOrDefault(r => !r.Ready);
}

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

// Reads HealthEnvironment from small fakeable probes. The radio and the paired list change any time and are read on every
// call; the slow facts (the Linux requirements, a pending restart, the adapter test file) are read on a full read and
// otherwise reused for up to StaticInterval.
public sealed class HealthProbe(IBluetoothRadio radio, IPairedControllers paired, IRestartProbe? restart, string? adapterResultPath,
	Func<long>? clockMs = null, Func<IReadOnlyList<SetupRequirement>>? requirements = null)
{
	public static readonly TimeSpan StaticInterval = TimeSpan.FromMinutes(1);

	private sealed record StaticFacts(ComponentInfo UsbIp, ComponentInfo HidHide, bool? RestartPending, AdapterSummary? Adapter, long ReadAt,
		IReadOnlyList<SetupRequirement>? Requirements = null);

	private readonly Func<long> clock = clockMs ?? (() => Environment.TickCount64);
	private readonly object gate = new();
	private StaticFacts? facts;

	// How many times the slow facts were read (tests).
	public int StaticReads { get; private set; }

	// full: read the slow facts now; otherwise they are read only when older than StaticInterval. True in the out
	// parameter when they were read by this call.
	public HealthEnvironment Read(bool full = true) => Read(full, out _);

	public HealthEnvironment Read(bool full, out bool readStatic)
	{
		StaticFacts current;
		lock (gate)
		{
			long now = clock();
			readStatic = full || facts == null || now - facts.ReadAt >= (long)StaticInterval.TotalMilliseconds;
			if (readStatic)
			{
				IReadOnlyList<SetupRequirement> linux = requirements == null ? [] : Safe(requirements, []);
				facts = new StaticFacts(LinuxUsbIp(linux), ComponentInfo.Missing, restart == null ? null : Safe(restart.IsRestartPending, null),
					AdapterSummary.Load(adapterResultPath), now, linux);
				StaticReads++;
			}
			current = facts!;
		}
		RadioState state = Safe(radio.Query, RadioState.Missing);
		IReadOnlyList<PairedController> list = state == RadioState.On ? Safe(paired.Find, []) : [];
		return new HealthEnvironment(current.UsbIp, current.HidHide, state, list, current.RestartPending, current.Adapter, current.Requirements);
	}

	// Linux: the USB/IP client counts as installed with usbip and pkexec, its driver with vhci_hcd loaded.
	public static ComponentInfo LinuxUsbIp(IReadOnlyList<SetupRequirement> requirements)
	{
		bool Ready(SetupRequirementId id) => requirements.FirstOrDefault(r => r.Id == id)?.Ready != false;
		return new ComponentInfo(Ready(SetupRequirementId.UsbIpTool) && Ready(SetupRequirementId.Pkexec), null, null, Ready(SetupRequirementId.VhciModule));
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

	// Priority: a missing USB/IP part (vhci_hcd, usbip, pkexec) > no radio > Bluetooth off > never paired > on a cable. A
	// paired controller that is off or asleep does not block: the bridge starts and waits for it. Hiding never blocks.
	public static BlockingIssue? Blocking(HealthEnvironment env, ConnectionState connection, Language? l = null)
	{
		if (env.FirstBlockingMissing is { } missing)
		{
			return new(BlockingKind.DriverMissing, HealthLevel.Error, Text.HealthChipDriver(l), missing.Problem(l), FixAction.InstallDriver);
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
		return env.FirstMissing is { } missing
			? new(HealthChipKind.Driver, missing.BlocksBridge ? HealthLevel.Error : HealthLevel.Warning, label, missing.Problem(l), FixAction.InstallDriver)
			: new(HealthChipKind.Driver, HealthLevel.Ok, label, Text.HealthLinuxSystemOk(l), FixAction.None);
	}

	// The real controller is not hidden on Linux yet (LinuxHiding): always says so.
	public static HealthChip HidHideChip(HealthEnvironment env, HealthRuntime run, Language? l = null) =>
		new(HealthChipKind.HidHide, HealthLevel.Unknown, Text.HealthChipHiding(l), Text.HealthHidingLinux(l), FixAction.None);

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
			FixAction fix = run.LastError is BridgeError.UsbIpMissing or BridgeError.PkexecMissing ? FixAction.InstallDriver : FixAction.RestartBridge;
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
