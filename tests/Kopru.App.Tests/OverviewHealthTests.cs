// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Health;
using Kopru.Core.Hid;
using Kopru.Core.Localization;
using Kopru.Core.Setup;
using Kopru.Core.Steam;

namespace Kopru.App.Tests;

// The Overview shows the Core health model: chips, the blocking issue as the main action, the last-known battery, the
// "why disabled" reasons; the fixes that need a page are routed by the main window.
public class OverviewHealthTests
{
	public static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

	private static readonly HidDevice BluetoothPad = new(@"\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_fake", 0x054C, 0x0CE6, 78, 547, 64);
	public static readonly HidDevice UsbPad = new(@"\?\hid#vid_054c&pid_0ce6&mi_03#usb_fake", 0x054C, 0x0CE6, 64, 64, 64);

	// A report for one situation (defaults: everything installed, radio on, paired, controller on Bluetooth, bridge off).
	public static HealthReport Report(bool driver = true, RadioState radio = RadioState.On, bool paired = true, HidDevice? pad = null,
		bool noPad = false, BridgeState bridge = BridgeState.Stopped, BridgeError error = BridgeError.None, SteamInputResult? steam = null,
		AdapterSummary? adapter = null) =>
		HealthReport.Build(
			new HealthEnvironment(driver ? new ComponentInfo(true, "1.0", null, true) : ComponentInfo.Missing, new ComponentInfo(true, "1.6", null, true),
				radio, paired ? [new PairedController("DualSense Wireless Controller", 1, !noPad, false)] : [], null, adapter),
			new HealthRuntime(noPad ? [] : [pad ?? BluetoothPad], bridge != BridgeState.Stopped, bridge, error, steam), Now);

	private static void Disconnect(FakeControllerService fake, HealthReport report)
	{
		fake.IsConnected = false;
		fake.Health = report;
		fake.RaiseStateChanged();
	}

	private static (MainWindowViewModel Vm, FakeControllerService Fake) Create(bool wizard = false)
	{
		var fake = new FakeControllerService(animate: false);
		var probes = new RealServiceTests.Probes(drivers: true);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings()),
			Wizard: wizard ? () => new SetupWizard(new SystemCheck(probes, probes, probes, probes), new RealServiceTests.Links()) : null, Now: () => Now));
		vm.Setup = null;
		return (vm, fake);
	}

	private static void Settle() => Dispatcher.UIThread.RunJobs();

	[AvaloniaFact]
	public void Chips_FollowTheModel_WithLevelReasonAndFix()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		fake.Health = Report(steam: new SteamInputResult(SteamInputStatus.OnGlobally, true, "advice"),
			adapter: new AdapterSummary(AdapterVerdict.Unsuitable, Now.AddDays(-1)));
		fake.RaiseStateChanged();
		Settle();

		Assert.Equal(5, vm.Overview.HealthChips.Count);
		Assert.Equal(Enum.GetValues<HealthChipKind>(), vm.Overview.HealthChips.Select(c => c.Kind));
		HealthChipViewModel steam = vm.Overview.Chip(HealthChipKind.SteamInput);
		Assert.Equal(Text.HealthChipSteamInput(), steam.Name);
		Assert.True(steam.IsAttention);
		Assert.Equal(Text.HealthSteamOnGlobally(), steam.Reason);
		Assert.True(steam.HasAction);
		Assert.Equal(Text.HealthFix(), steam.ActionText);
		Assert.Contains(Text.FixSteamInputHelp(), steam.ActionTip);
		Assert.True(vm.Overview.Chip(HealthChipKind.Adapter).IsProblem);
		HealthChipViewModel driver = vm.Overview.Chip(HealthChipKind.Driver);
		Assert.True(driver.IsGood);
		Assert.False(driver.HasAction);
		Assert.Equal(Text.HealthLevelOk(), driver.StatusText);

		// The chip's fix goes to the service, which asks the window for the Steam Input page.
		steam.RunCommand.Execute(null);
		Settle();
		Assert.Equal([FixAction.OpenSteamInputHelp], fake.Fixes);
		Assert.Same(vm.SupportedGames, vm.CurrentPage);
	}

	[AvaloniaFact]
	public async Task BlockingIssue_ReplacesTheBridgeButton()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		Assert.Equal(Text.BridgeStart(), vm.Overview.PrimaryText);
		Assert.True(vm.Overview.CanPrimary);
		Assert.Null(vm.Overview.PrimaryTip);

		foreach ((HealthReport report, FixAction fix, string label) in new[]
		{
			(Report(driver: false), FixAction.InstallDriver, Text.FixInstallDriver()),
			(Report(radio: RadioState.Off, noPad: true), FixAction.OpenBluetoothSettings, Text.FixOpenBluetooth()),
			(Report(paired: false, noPad: true), FixAction.PairController, Text.PairController()),
		})
		{
			fake.Fixes.Clear();
			Disconnect(fake, report);
			Settle();
			Assert.Equal(label, vm.Overview.PrimaryText);
			Assert.True(vm.Overview.CanPrimary);
			Assert.Equal(report.BlockingIssue!.Reason, vm.Overview.PrimaryTip);
			await vm.Overview.PrimaryCommand.ExecuteAsync(null);
			Assert.Equal([fix], fake.Fixes);
			Assert.False(fake.IsBridgeRunning);
		}

		// No fix (no radio): the bridge button stays, disabled, with the reason.
		Disconnect(fake, Report(radio: RadioState.Missing, noPad: true));
		Settle();
		Assert.Equal(Text.BridgeStart(), vm.Overview.PrimaryText);
		Assert.False(vm.Overview.CanPrimary);
		Assert.Equal(Text.CannotStartNoRadio(), vm.Overview.PrimaryTip);
		Assert.Equal(Text.ConnectionNoRadio(), vm.Overview.StatusLine);

		// Paired but asleep: nothing blocks, the bridge starts and waits.
		Disconnect(fake, Report(noPad: true));
		Settle();
		Assert.Equal(Text.BridgeStart(), vm.Overview.PrimaryText);
		Assert.True(vm.Overview.CanPrimary);
		Assert.Equal(Text.ConnectionPairedNotConnected(), vm.Overview.StatusLine);
		Assert.Equal(Text.ConnectionPairedNotConnectedReason(), vm.Overview.NoControllerReason);
	}

	[AvaloniaFact]
	public void Hero_ShowsTheConnectionReasonAndItsAction()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		Disconnect(fake, Report(paired: false, noPad: true));
		Settle();
		Assert.True(vm.Overview.ShowsNoController);
		Assert.Equal(Text.ConnectionNeverPaired(), vm.Overview.NoControllerTitle);
		Assert.Equal(Text.ConnectionNeverPairedReason(), vm.Overview.NoControllerReason);
		// The header already pairs: no second button.
		Assert.False(vm.Overview.HasHeroAction);

		// On a cable: an explanation, no action, nothing to start.
		Disconnect(fake, Report(pad: UsbPad));
		Settle();
		Assert.Equal(Text.ConnectionUsbReason(), vm.Overview.NoControllerReason);
		Assert.False(vm.Overview.HasHeroAction);
		Assert.False(vm.Overview.CanPrimary);
		Assert.Equal(Text.CannotStartUsb(), vm.Overview.PrimaryTip);
	}

	[AvaloniaFact]
	public void Battery_ShowsTheLastKnownReading_WhenThereIsNoLiveOne()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		Assert.True(vm.Overview.HasBattery);
		Assert.False(vm.Overview.HasLastBattery);

		fake.LastKnownBattery = new LastKnownBattery(62, false, Now.AddHours(-2));
		Disconnect(fake, Report(noPad: true));
		Settle();
		Assert.False(vm.Overview.HasBattery);
		Assert.Equal(Text.LastKnownBattery(62, Text.TimeHoursAgo(2)), vm.Overview.LastBatteryText);
		Assert.True(vm.Overview.ShowsLastBatteryInHero);

		fake.LastKnownBattery = null;
		fake.RaiseStateChanged();
		Settle();
		Assert.Equal(string.Empty, vm.Overview.LastBatteryText);
		Assert.False(vm.Overview.ShowsLastBatteryInHero);
	}

	[AvaloniaTheory]
	[InlineData(FixAction.InstallDriver, SetupStepId.Drivers)]
	[InlineData(FixAction.InstallHidHide, SetupStepId.Drivers)]
	[InlineData(FixAction.PairController, SetupStepId.PairController)]
	public async Task PageFixes_OpenTheSetupStep(FixAction fix, SetupStepId step)
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create(wizard: true);
		await fake.ExecuteFix(fix);
		Settle();
		Assert.NotNull(vm.Setup);
		Assert.Equal(step, vm.Setup!.FocusStep);
		Assert.Same(vm.Setup, vm.CurrentPage);
	}

	[AvaloniaFact]
	public async Task AdapterTestFix_RunsTheTestInSettings()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		await fake.ExecuteFix(FixAction.RunAdapterTest);
		Settle();
		Assert.Same(vm.SettingsPage, vm.CurrentPage);
		for (int i = 0; i < 50 && vm.SettingsPage.ToolResult != Text.AdapterAllGood(fake.InputRateHz); i++)
		{
			await Task.Delay(20);
			Settle();
		}
		Assert.Equal(Text.AdapterAllGood(fake.InputRateHz), vm.SettingsPage.ToolResult);
	}
}
