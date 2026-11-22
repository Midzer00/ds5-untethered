// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Health;
using Kopru.Core.Hid;
using Kopru.Core.Localization;
using Kopru.Core.Setup;
using Kopru.Core.Steam;

namespace Kopru.Core.Tests;

// Reads text in the current language (LocalizationTests switch it): not in parallel with them.
[Collection("Localization")]
public class HealthTests
{
	private static readonly HidDevice BtPad = new(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6", 0x054C, 0x0CE6, 78, 547, 64);
	private static readonly HidDevice BtEdge = BtPad with { ProductId = HidDevice.DualSenseEdgeProductId };
	private static readonly HidDevice UsbPad = new(@"\\?\hid#vid_054c&pid_0ce6", 0x054C, 0x0CE6, 64, 48, 64);
	private static readonly PairedController Paired = new("DualSense Wireless Controller", 0xA0B1C2D3E4F5, false, false);
	private static readonly ComponentInfo Ready = new(true, "0.9.8.1", null, true);
	private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

	private static HealthEnvironment Env(RadioState radio = RadioState.On, IReadOnlyList<PairedController>? paired = null, ComponentInfo? usbIp = null,
		ComponentInfo? hidHide = null, bool? restart = false, AdapterSummary? adapter = null) =>
		new(usbIp ?? Ready, hidHide ?? Ready, radio, paired ?? [Paired], restart, adapter);

	private static HealthRuntime Run(IReadOnlyList<HidDevice>? pads = null, bool started = false, BridgeState state = BridgeState.Stopped,
		BridgeError error = BridgeError.None, SteamInputResult? steam = null, bool? allowed = null, bool? hidden = null) =>
		new(pads ?? [BtPad], started, state, error, steam, allowed, hidden);

	public static TheoryData<Language> Languages => new() { Language.Turkish, Language.English };

	// Connection sub-states.

	[Fact]
	public void Connection_Bluetooth_WinsOverUsb() =>
		Assert.Equal(new ConnectionState(ConnectionKind.ConnectedBluetooth), ConnectionState.Detect(RadioState.On, [UsbPad, BtPad], []));

	[Fact]
	public void Connection_Edge_IsFlagged()
	{
		Assert.True(ConnectionState.Detect(RadioState.On, [BtEdge], []).IsEdge);
		Assert.True(ConnectionState.Detect(RadioState.On, [], [Paired with { IsEdge = true }]).IsEdge);
	}

	[Fact]
	public void Connection_UsbOnly_EvenWithBluetoothOff() =>
		Assert.Equal(ConnectionKind.ConnectedUsb, ConnectionState.Detect(RadioState.Off, [UsbPad], []).Kind);

	[Theory]
	[InlineData(RadioState.Missing, ConnectionKind.NoRadio)]
	[InlineData(RadioState.Off, ConnectionKind.BluetoothOff)]
	public void Connection_Radio(RadioState radio, ConnectionKind expected) =>
		Assert.Equal(expected, ConnectionState.Detect(radio, [], [Paired]).Kind);

	[Fact]
	public void Connection_PairedButAsleep_And_NeverPaired()
	{
		Assert.Equal(ConnectionKind.PairedNotConnected, ConnectionState.Detect(RadioState.On, [], [Paired]).Kind);
		Assert.Equal(ConnectionKind.NeverPaired, ConnectionState.Detect(RadioState.On, [], []).Kind);
	}

	[Theory]
	[MemberData(nameof(Languages))]
	public void Connection_EveryKind_HasLabelAndReason(Language l)
	{
		foreach (ConnectionKind kind in Enum.GetValues<ConnectionKind>())
		{
			ConnectionState s = new(kind);
			Assert.False(string.IsNullOrWhiteSpace(s.Label(l)));
			Assert.False(string.IsNullOrWhiteSpace(s.Reason(l)));
		}
	}

	[Fact]
	public void PairedList_FromFakeProbe()
	{
		HealthProbe probe = new(new Registry(usbIp: true), new Radio(RadioState.On), new PairedList([Paired]), null, null);
		HealthEnvironment env = probe.Read();
		Assert.True(env.UsbIp.Ready);
		Assert.False(env.HidHide.Installed);
		Assert.Single(env.Paired);
		Assert.Null(env.Adapter);
		// The paired list is not read with the radio off.
		Assert.Empty(new HealthProbe(new Registry(true), new Radio(RadioState.Off), new PairedList([Paired]), null, null).Read().Paired);
	}

	[Fact]
	public void PairedList_ProbeFailure_IsEmpty() =>
		Assert.Empty(new HealthProbe(new Registry(true), new Radio(RadioState.On), new PairedList(null), null, null).Read().Paired);

	[Theory]
	[InlineData("{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6", false)]
	[InlineData("{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0df2", true)]
	[InlineData("{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&09cc", null)]
	[InlineData("{00001124-0000-1000-8000-00805f9b34fb}_VID&0002045e_PID&0ce6", null)]
	public void BthEnum_PidMatch(string key, bool? edge) => Assert.Equal(edge, WindowsPairedControllers.MatchPid(key));

	[Theory]
	[InlineData("9&2d1b0b2a&0&A0B1C2D3E4F5_C00000000", "A0B1C2D3E4F5")]
	[InlineData("8&1234&0&a0b1c2d3e4f5_C00000000", "A0B1C2D3E4F5")]
	[InlineData("8&1234&0&NOTANADDRESS_C00000000", null)]
	public void BthEnum_AddressFromInstance(string instance, string? address) =>
		Assert.Equal(address, WindowsPairedControllers.AddressFromInstance(instance));

	// Chips.

	[Theory]
	[InlineData(false, false, null, HealthLevel.Error, FixAction.InstallDriver)]
	[InlineData(true, false, null, HealthLevel.Error, FixAction.InstallDriver)]
	[InlineData(true, true, true, HealthLevel.Warning, FixAction.None)]
	[InlineData(true, true, false, HealthLevel.Ok, FixAction.None)]
	public void DriverChip(bool installed, bool driver, bool? restart, HealthLevel level, FixAction fix)
	{
		HealthChip chip = HealthReport.DriverChip(Env(usbIp: new ComponentInfo(installed, "0.9.8.1", null, driver), restart: restart));
		Assert.Equal((level, fix), (chip.Level, chip.Fix));
		Assert.Equal(HealthChipKind.Driver, chip.Kind);
	}

	[Fact]
	public void DriverChip_ShowsVersion()
	{
		Assert.Contains("0.9.8.1", HealthReport.DriverChip(Env(), Language.English).Reason);
		Assert.Contains("sürüm bilinmiyor", HealthReport.DriverChip(Env(usbIp: Ready with { Version = null }), Language.Turkish).Reason);
	}

	[Theory]
	[InlineData(false, false, false, null, null, HealthLevel.Error, FixAction.InstallHidHide)]
	[InlineData(true, false, false, null, null, HealthLevel.Error, FixAction.InstallHidHide)]
	[InlineData(true, true, false, null, null, HealthLevel.Ok, FixAction.None)]
	[InlineData(true, true, true, false, null, HealthLevel.Warning, FixAction.RestartBridge)]
	[InlineData(true, true, true, true, false, HealthLevel.Warning, FixAction.RestartBridge)]
	[InlineData(true, true, true, true, true, HealthLevel.Ok, FixAction.None)]
	public void HidHideChip(bool installed, bool driver, bool started, bool? allowed, bool? hidden, HealthLevel level, FixAction fix)
	{
		HealthChip chip = HealthReport.HidHideChip(Env(hidHide: new ComponentInfo(installed, "1.5", null, driver)), Run(started: started, allowed: allowed, hidden: hidden));
		Assert.Equal((level, fix), (chip.Level, chip.Fix));
	}

	[Fact]
	public void HidHideChip_Hidden_SaysSo() =>
		Assert.Equal(Text.HealthHidingActive(Language.English),
			HealthReport.HidHideChip(Env(), Run(started: true, allowed: true, hidden: true), Language.English).Reason);

	[Theory]
	[InlineData(null, HealthLevel.Unknown, FixAction.None)]
	[InlineData(SteamInputStatus.NotInstalled, HealthLevel.Ok, FixAction.None)]
	[InlineData(SteamInputStatus.Off, HealthLevel.Ok, FixAction.None)]
	[InlineData(SteamInputStatus.OnGlobally, HealthLevel.Warning, FixAction.OpenSteamInputHelp)]
	[InlineData(SteamInputStatus.OnForApp, HealthLevel.Warning, FixAction.OpenSteamInputHelp)]
	[InlineData(SteamInputStatus.Unknown, HealthLevel.Unknown, FixAction.OpenSteamInputHelp)]
	public void SteamChip(SteamInputStatus? status, HealthLevel level, FixAction fix)
	{
		HealthChip chip = HealthReport.SteamChip(status is { } s ? new SteamInputResult(s, true, "x") : null);
		Assert.Equal((level, fix), (chip.Level, chip.Fix));
	}

	[Theory]
	[InlineData(AdapterVerdict.Suitable, HealthLevel.Ok, FixAction.None)]
	[InlineData(AdapterVerdict.Borderline, HealthLevel.Warning, FixAction.RunAdapterTest)]
	[InlineData(AdapterVerdict.Unsuitable, HealthLevel.Error, FixAction.RunAdapterTest)]
	public void AdapterChip(AdapterVerdict verdict, HealthLevel level, FixAction fix)
	{
		AdapterSummary summary = new(verdict, Now.AddHours(-2));
		HealthChip chip = HealthReport.AdapterChip(summary, Now, Language.Turkish);
		Assert.Equal((level, fix), (chip.Level, chip.Fix));
		Assert.Contains("2 sa önce", chip.Reason);
	}

	[Fact]
	public void AdapterChip_NeverRun_And_NoData_AreUnknown()
	{
		Assert.Equal((HealthLevel.Unknown, FixAction.RunAdapterTest), Pick(HealthReport.AdapterChip(null, Now)));
		Assert.Equal((HealthLevel.Unknown, FixAction.RunAdapterTest), Pick(HealthReport.AdapterChip(new AdapterSummary(null, Now), Now)));
		static (HealthLevel, FixAction) Pick(HealthChip c) => (c.Level, c.Fix);
	}

	[Fact]
	public void AdapterSummary_ReadsTheFileTheServiceWrites()
	{
		string dir = Path.Combine(Path.GetTempPath(), "kopru-health-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			string path = Path.Combine(dir, DiagnosticsExport.AdapterResultFileName);
			Assert.Null(AdapterSummary.Load(path));
			File.WriteAllText(path, "{\"Time\":\"2026-11-09T10:00:00+00:00\",\"Verdict\":\"Borderline\",\"Text\":\"x\"}");
			AdapterSummary? s = AdapterSummary.Load(path);
			Assert.Equal(AdapterVerdict.Borderline, s?.Verdict);
			Assert.Equal(Now.AddHours(-2), s?.Time);
			File.WriteAllText(path, "{\"Time\":\"2026-11-09T10:00:00+00:00\",\"Verdict\":null}");
			Assert.Null(AdapterSummary.Load(path)?.Verdict);
			File.WriteAllText(path, "not json");
			Assert.Null(AdapterSummary.Load(path));
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[Theory]
	[InlineData(false, BridgeState.Stopped, BridgeError.None, HealthLevel.Ok, FixAction.None)]
	[InlineData(true, BridgeState.WaitingForController, BridgeError.None, HealthLevel.Warning, FixAction.None)]
	[InlineData(true, BridgeState.Connecting, BridgeError.None, HealthLevel.Ok, FixAction.None)]
	[InlineData(true, BridgeState.Running, BridgeError.None, HealthLevel.Ok, FixAction.None)]
	[InlineData(true, BridgeState.Error, BridgeError.Watchdog, HealthLevel.Error, FixAction.RestartBridge)]
	[InlineData(true, BridgeState.Error, BridgeError.UsbIpMissing, HealthLevel.Error, FixAction.InstallDriver)]
	[InlineData(true, BridgeState.WaitingForController, BridgeError.ControllerLost, HealthLevel.Warning, FixAction.ReconnectController)]
	public void BridgeChip(bool started, BridgeState state, BridgeError error, HealthLevel level, FixAction fix)
	{
		HealthChip chip = HealthReport.BridgeChip(Run(started: started, state: state, error: error));
		Assert.Equal((level, fix), (chip.Level, chip.Fix));
	}

	[Fact]
	public void BridgeChip_Error_GivesTheReason() =>
		Assert.Contains(BridgeHost.Describe(BridgeError.Watchdog, Language.English)!,
			HealthReport.BridgeChip(Run(started: true, state: BridgeState.Error, error: BridgeError.Watchdog), Language.English).Reason);

	[Theory]
	[MemberData(nameof(Languages))]
	public void EveryChip_HasLabelAndReason(Language l)
	{
		HealthReport report = HealthReport.Build(Env(), Run(), Now, l);
		Assert.Equal(Enum.GetValues<HealthChipKind>().Length, report.Chips.Count);
		Assert.All(report.Chips, c => Assert.False(string.IsNullOrWhiteSpace(c.Label) || string.IsNullOrWhiteSpace(c.Reason)));
		Assert.Equal(Text.HealthChipDriver(l), report.Chip(HealthChipKind.Driver).Label);
	}

	// Blocking issue and "why disabled".

	[Fact]
	public void Blocking_DriverMissing_BeatsEverything()
	{
		HealthReport r = HealthReport.Build(Env(radio: RadioState.Off, paired: [], usbIp: ComponentInfo.Missing), Run(pads: []), Now);
		Assert.Equal(BlockingKind.DriverMissing, r.BlockingIssue?.Kind);
		Assert.Equal(FixAction.InstallDriver, r.BlockingIssue?.Fix);
		Assert.False(r.CanStartBridge);
		Assert.Equal(Text.CannotStartDriver(), r.CanStartBridgeReason);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public void Blocking_HidHideMissing_BlocksLikeTheDriver(bool installed, bool driver)
	{
		HealthReport r = HealthReport.Build(Env(hidHide: new ComponentInfo(installed, "1.5", null, driver)), Run(), Now);
		Assert.Equal(BlockingKind.HidHideMissing, r.BlockingIssue?.Kind);
		Assert.Equal(HealthLevel.Error, r.BlockingIssue?.Level);
		Assert.Equal(FixAction.InstallHidHide, r.BlockingIssue?.Fix);
		Assert.False(r.CanStartBridge);
		Assert.Equal(Text.CannotStartHidHide(), r.CanStartBridgeReason);
	}

	[Fact]
	public void Blocking_UsbIpMissing_BeatsHidHideMissing()
	{
		HealthReport r = HealthReport.Build(Env(usbIp: ComponentInfo.Missing, hidHide: ComponentInfo.Missing), Run(), Now);
		Assert.Equal(BlockingKind.DriverMissing, r.BlockingIssue?.Kind);
	}

	[Fact]
	public void Blocking_HidHideMissing_BeatsBluetooth()
	{
		HealthReport r = HealthReport.Build(Env(radio: RadioState.Off, hidHide: ComponentInfo.Missing), Run(pads: []), Now);
		Assert.Equal(BlockingKind.HidHideMissing, r.BlockingIssue?.Kind);
	}

	[Theory]
	[InlineData(RadioState.Missing, false, BlockingKind.NoRadio, FixAction.None)]
	[InlineData(RadioState.Off, true, BlockingKind.BluetoothOff, FixAction.OpenBluetoothSettings)]
	[InlineData(RadioState.On, false, BlockingKind.NeverPaired, FixAction.PairController)]
	public void Blocking_ByConnection(RadioState radio, bool paired, BlockingKind kind, FixAction fix)
	{
		HealthReport r = HealthReport.Build(Env(radio: radio, paired: paired ? [Paired] : []), Run(pads: []), Now);
		Assert.Equal((kind, fix), (r.BlockingIssue!.Kind, r.BlockingIssue.Fix));
		Assert.Equal(HealthLevel.Error, r.BlockingIssue.Level);
		Assert.False(r.CanStartBridge);
		Assert.NotEmpty(r.CanStartBridgeReason);
	}

	[Fact]
	public void Blocking_Usb_ExplainsTheBridgeIsNotNeeded()
	{
		HealthReport r = HealthReport.Build(Env(), Run(pads: [UsbPad]), Now, Language.English);
		Assert.Equal(BlockingKind.UsbConnected, r.BlockingIssue?.Kind);
		Assert.Equal(HealthLevel.Warning, r.BlockingIssue?.Level);
		Assert.Equal(Text.CannotStartUsb(Language.English), r.CanStartBridgeReason);
		Assert.False(r.CanTest);
		Assert.Equal(Text.CannotTestUsb(Language.English), r.CanTestReason);
	}

	[Fact]
	public void PairedButAsleep_CanStart_ButNotTest()
	{
		HealthReport r = HealthReport.Build(Env(), Run(pads: []), Now);
		Assert.Equal(ConnectionKind.PairedNotConnected, r.Connection.Kind);
		Assert.Null(r.BlockingIssue);
		Assert.True(r.CanStartBridge);
		Assert.Empty(r.CanStartBridgeReason);
		Assert.False(r.CanTest);
		Assert.Equal(Text.CannotTestNoController(), r.CanTestReason);
	}

	[Fact]
	public void AllGood()
	{
		HealthReport r = HealthReport.Build(Env(adapter: new AdapterSummary(AdapterVerdict.Suitable, Now)),
			Run(started: true, state: BridgeState.Running, steam: new SteamInputResult(SteamInputStatus.Off, true, "x"), allowed: true, hidden: true), Now);
		Assert.True(r.CanStartBridge);
		Assert.True(r.CanTest);
		Assert.Empty(r.CanTestReason);
		Assert.Equal(HealthLevel.Ok, r.Overall);
	}

	[Fact]
	public void Overall_IsTheWorstChip() =>
		Assert.Equal(HealthLevel.Error, HealthReport.Build(Env(usbIp: ComponentInfo.Missing), Run(), Now).Overall);

	[Theory]
	[InlineData(FixAction.RestartBridge, true)]
	[InlineData(FixAction.OpenBluetoothSettings, true)]
	[InlineData(FixAction.InstallDriver, false)]
	[InlineData(FixAction.InstallHidHide, false)]
	[InlineData(FixAction.OpenSteamInputHelp, false)]
	[InlineData(FixAction.RunAdapterTest, false)]
	[InlineData(FixAction.PairController, false)]
	public void FixRouting(FixAction fix, bool byService) => Assert.Equal(byService, HealthReport.IsHandledByService(fix));

	// Last-known battery and relative time.

	[Theory]
	[InlineData(20, "az önce", "just now")]
	[InlineData(5 * 60 + 30, "5 dk önce", "5 min ago")]
	[InlineData(2 * 3600 + 100, "2 sa önce", "2 h ago")]
	[InlineData(30 * 3600, "dün", "yesterday")]
	[InlineData(3 * 86400 + 5, "3 gün önce", "3 days ago")]
	public void RelativeTime_TurkishAndEnglish(int seconds, string tr, string en)
	{
		Assert.Equal(tr, RelativeTime.Format(TimeSpan.FromSeconds(seconds), Language.Turkish));
		Assert.Equal(en, RelativeTime.Format(TimeSpan.FromSeconds(seconds), Language.English));
	}

	[Fact]
	public void LastKnownBattery_Describe()
	{
		LastKnownBattery b = new(62, false, Now.AddHours(-2));
		Assert.Equal("Son: %62 · 2 sa önce", b.Describe(Now, Language.Turkish));
		Assert.Equal("Last: 62% · 2 h ago", b.Describe(Now, Language.English));
		Assert.Contains("şarj", (b with { Charging = true }).Describe(Now, Language.Turkish));
	}

	[Fact]
	public void LastBatteryStore_PersistsAndThrottles()
	{
		string dir = Path.Combine(Path.GetTempPath(), "kopru-battery-" + Guid.NewGuid().ToString("N"));
		try
		{
			string path = Path.Combine(dir, LastBatteryStore.FileName);
			LastBatteryStore store = new(path);
			Assert.Null(store.Current);
			Assert.True(store.Record(new BatteryStatus(60, ChargeState.Discharging), Now));
			// Same reading soon after: not written again.
			Assert.False(store.Record(new BatteryStatus(60, ChargeState.Discharging), Now.AddSeconds(10)));
			// Changed: written.
			Assert.True(store.Record(new BatteryStatus(50, ChargeState.Charging), Now.AddSeconds(20)));
			// Same but old: refreshed.
			Assert.True(store.Record(new BatteryStatus(50, ChargeState.Charging), Now.AddMinutes(5)));

			LastKnownBattery? reloaded = new LastBatteryStore(path).Current;
			Assert.Equal(new LastKnownBattery(50, true, Now.AddMinutes(5)), reloaded);
		}
		finally
		{
			if (Directory.Exists(dir))
			{
				Directory.Delete(dir, true);
			}
		}
	}

	[Fact]
	public void LastBatteryStore_BadFile_IsNull()
	{
		string path = Path.Combine(Path.GetTempPath(), "kopru-battery-bad-" + Guid.NewGuid().ToString("N") + ".json");
		File.WriteAllText(path, "{oops");
		try
		{
			Assert.Null(new LastBatteryStore(path).Current);
		}
		finally
		{
			File.Delete(path);
		}
	}

	private sealed class Registry(bool usbIp) : IInstallRegistry
	{
		public (string? Version, string? Location)? FindProgram(string prefix) =>
			usbIp && prefix == SystemCheck.UsbIpProgramPrefix ? ("0.9.8.1", @"C:\USBip\") : null;

		public bool ServiceExists(string serviceName) => usbIp && serviceName == "usbip2_ude";
		public bool FileExists(string path) => usbIp;
	}

	private sealed class Radio(RadioState state) : IBluetoothRadio
	{
		public RadioState Query() => state;
	}

	// null list = the probe throws.
	private sealed class PairedList(IReadOnlyList<PairedController>? list) : IPairedControllers
	{
		public IReadOnlyList<PairedController> Find() => list ?? throw new InvalidOperationException("probe failed");
	}
}
