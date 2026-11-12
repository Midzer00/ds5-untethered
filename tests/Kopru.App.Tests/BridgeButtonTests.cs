// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Health;
using Kopru.Core.Hid;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

// The virtual controller starts by itself, so the Overview header only offers what the user needs while it is off (start,
// or the blocking issue's fix) and never a stop; stopping lives in Settings → General ("Virtual Controller" row) and the
// tray menu, on the same command and busy guard.
public class BridgeButtonTests
{
	private static (MainWindowViewModel Vm, FakeControllerService Fake) Create(string? language = null)
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(language == null ? new AppSettings() : new AppSettings { Language = language })));
		return (vm, fake);
	}

	private static void Settle() => Dispatcher.UIThread.RunJobs();

	private static readonly HidDevice BluetoothPad = new(@"\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_fake", 0x054C, 0x0CE6, 78, 547, 64);

	// A report with HidHide missing (everything else ready, controller on Bluetooth, bridge off).
	private static HealthReport HidHideMissing() =>
		HealthReport.Build(
			new HealthEnvironment(new ComponentInfo(true, "1.0", null, true), ComponentInfo.Missing, RadioState.On,
				[new PairedController("DualSense Wireless Controller", 1, true, false)], null, null),
			new HealthRuntime([BluetoothPad], false, BridgeState.Stopped, BridgeError.None, null), OverviewHealthTests.Now);

	private static void SetHealth(FakeControllerService fake, HealthReport report, bool connected)
	{
		fake.IsConnected = connected;
		fake.Health = report;
		fake.RaiseStateChanged();
		Settle();
	}

	[AvaloniaFact]
	public async Task Header_HasNoStopButton_WhileRunningOrStarting()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		Assert.True(vm.Overview.ShowsPrimary);
		Assert.Equal(Text.BridgeStart(), vm.Overview.PrimaryText);

		// Starting: the state text says so, no button.
		fake.StartGate = new TaskCompletionSource();
		Task start = vm.Overview.ToggleBridgeCommand.ExecuteAsync(null);
		Settle();
		Assert.False(vm.Overview.ShowsPrimary);
		Assert.False(vm.Overview.CanPrimary);

		// Running: still no button, and the header's command never stops it.
		fake.StartGate.SetResult();
		await start;
		Settle();
		Assert.True(vm.Overview.IsBridgeRunning);
		Assert.False(vm.Overview.ShowsPrimary);
		Assert.False(vm.Overview.CanPrimary);
		await vm.Overview.PrimaryCommand.ExecuteAsync(null);
		Assert.Equal(0, fake.Stops);
		Assert.True(fake.IsBridgeRunning);

		// The rendered header: no visible stop anywhere on the Overview.
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		Settle();
		Button primary = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PrimaryAction");
		Assert.False(primary.IsVisible);
		Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),
			b => b.IsEffectivelyVisible && (b.Content as string) is { } s && (s == Text.BridgeStop() || s == Text.BridgeRowStop()));

		// Stopped again (from Settings or the tray): the start comes back.
		await vm.Overview.ToggleBridgeCommand.ExecuteAsync(null);
		Settle();
		Assert.True(primary.IsVisible);
		Assert.Equal(Text.BridgeStart(), primary.Content);
		window.Close();
	}

	[AvaloniaFact]
	public async Task Header_ShowsStartOrTheFix_InEveryStoppedOrBlockedState()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		foreach ((string name, HealthReport report, bool connected, string label, bool enabled, FixAction fix) in new[]
		{
			("stopped", OverviewHealthTests.Report(), true, Text.BridgeStart(), true, FixAction.None),
			("driver", OverviewHealthTests.Report(driver: false), true, Text.FixInstallDriver(), true, FixAction.InstallDriver),
			("hidhide", HidHideMissing(), true, Text.FixInstallHidHide(), true, FixAction.InstallHidHide),
			("bluetooth off", OverviewHealthTests.Report(radio: RadioState.Off, noPad: true), false, Text.FixOpenBluetooth(), true, FixAction.OpenBluetoothSettings),
			("not paired", OverviewHealthTests.Report(paired: false, noPad: true), false, Text.PairController(), true, FixAction.PairController),
			("no radio", OverviewHealthTests.Report(radio: RadioState.Missing, noPad: true), false, Text.BridgeStart(), false, FixAction.None),
			("cable", OverviewHealthTests.Report(pad: OverviewHealthTests.UsbPad), false, Text.BridgeStart(), false, FixAction.None),
			("asleep", OverviewHealthTests.Report(noPad: true), false, Text.BridgeStart(), true, FixAction.None),
		})
		{
			SetHealth(fake, report, connected);
			fake.Fixes.Clear();
			Assert.True(vm.Overview.ShowsPrimary, name);
			Assert.Equal(label, vm.Overview.PrimaryText);
			Assert.Equal(enabled, vm.Overview.CanPrimary);
			if (fix != FixAction.None)
			{
				await vm.Overview.PrimaryCommand.ExecuteAsync(null);
				Assert.Equal([fix], fake.Fixes);
				Assert.False(fake.IsBridgeRunning);
			}
		}

		// Stopped with nothing blocking: the header starts it.
		SetHealth(fake, OverviewHealthTests.Report(), true);
		await vm.Overview.PrimaryCommand.ExecuteAsync(null);
		Assert.Equal(1, fake.Starts);
		Assert.True(fake.IsBridgeRunning);
	}

	[AvaloniaFact]
	public async Task SettingsRow_ShowsTheStateAndStopsOrStarts_WithTheBusyGuard()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		OverviewViewModel row = vm.SettingsPage.Bridge!;
		Assert.Same(vm.Overview, row);
		Assert.True(vm.SettingsPage.HasBridge);
		Assert.Equal(Text.RowVirtualPad(), row.BridgeRowLabel);
		Assert.Equal(Text.BridgeRowHint(), row.BridgeRowHint);

		// Stopped: "Off", Start.
		Assert.Equal(Text.HealthOff(), row.BridgeRowState);
		Assert.Equal(Text.BridgeRowStart(), row.BridgeRowButtonText);
		Assert.True(row.CanToggleBridge);
		Assert.Null(row.BridgeRowTip);

		// Starting: "Connecting", Stop disabled; a second press does nothing.
		fake.StartGate = new TaskCompletionSource();
		Task start = row.ToggleBridgeCommand.ExecuteAsync(null);
		Settle();
		Assert.Equal(Text.BridgeConnecting(), row.BridgeRowState);
		Assert.Equal(Text.BridgeRowStop(), row.BridgeRowButtonText);
		Assert.False(row.CanToggleBridge);
		await row.ToggleBridgeCommand.ExecuteAsync(null);
		Assert.Equal(1, fake.Starts);
		Assert.Equal(0, fake.Stops);
		fake.StartGate.SetResult();
		await start;
		Settle();

		// Running: "On", Stop; pressing it stops.
		Assert.Equal(Text.HealthOn(), row.BridgeRowState);
		Assert.Equal(Text.BridgeRowStop(), row.BridgeRowButtonText);
		Assert.True(row.CanToggleBridge);
		await row.ToggleBridgeCommand.ExecuteAsync(null);
		Settle();
		Assert.Equal(1, fake.Stops);
		Assert.Equal(Text.HealthOff(), row.BridgeRowState);
		Assert.Equal(Text.BridgeRowStart(), row.BridgeRowButtonText);

		// Blocked: Start disabled, the reason in the tooltip.
		SetHealth(fake, OverviewHealthTests.Report(radio: RadioState.Off, noPad: true), false);
		Assert.Equal(Text.HealthOff(), row.BridgeRowState);
		Assert.Equal(Text.BridgeRowStart(), row.BridgeRowButtonText);
		Assert.False(row.CanToggleBridge);
		Assert.Equal(fake.CanStartBridgeReason, row.BridgeRowTip);
		Assert.False(string.IsNullOrEmpty(row.BridgeRowTip));

		// An error: "Error".
		SetHealth(fake, OverviewHealthTests.Report(bridge: BridgeState.Error, error: BridgeError.UsbIpMissing), true);
		Assert.Equal(Text.HealthError(), row.BridgeRowState);
	}

	// The row in the rendered Settings page is bound to the Overview's command and follows the state.
	[AvaloniaFact]
	public async Task SettingsRow_Rendered_IsBoundToTheSameCommand()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		vm.Navigate(AppPage.SettingsGeneral);
		Assert.True(vm.SettingsPage.IsGeneralTab);
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		Settle();
		Button button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BridgeRowButton");
		TextBlock state = window.GetVisualDescendants().OfType<TextBlock>().Single(b => b.Name == "BridgeRowState");
		Assert.Same(vm.Overview.ToggleBridgeCommand, button.Command);
		Assert.Equal(Text.BridgeRowStart(), button.Content);
		Assert.Equal(Text.HealthOff(), state.Text);
		Assert.True(button.IsEnabled);

		await vm.Overview.ToggleBridgeCommand.ExecuteAsync(null);
		Settle();
		Assert.Equal(Text.BridgeRowStop(), button.Content);
		Assert.Equal(Text.HealthOn(), state.Text);

		SetHealth(fake, OverviewHealthTests.Report(radio: RadioState.Off, noPad: true), false);
		await vm.Overview.ToggleBridgeCommand.ExecuteAsync(null);
		Settle();
		Assert.Equal(Text.BridgeRowStart(), button.Content);
		Assert.False(button.IsEnabled);
		Assert.Equal(fake.CanStartBridgeReason, ToolTip.GetTip(button));
		window.Close();
	}

	// Overview running / stopped / blocked and the Settings general group, TR and EN, at the default size and 1680x1050:
	// the header button and the row fit. PNGs only with KOPRU_RENDER_SCREENS=1, to KOPRU_BRIDGE_RENDER_DIR (or the temp
	// folder's kopru-bridge-button).
	[AvaloniaFact]
	public void Render_OverviewStatesAndSettingsRow()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1"
			? Environment.GetEnvironmentVariable("KOPRU_BRIDGE_RENDER_DIR") ?? Path.Combine(Path.GetTempPath(), "kopru-bridge-button")
			: null;
		if (outDir != null)
		{
			Directory.CreateDirectory(outDir);
		}
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height, string size) in new[] { (1100.0, 700.0, "default"), (1680.0, 1050.0, "large") })
				{
					foreach (string state in new[] { "running", "stopped", "blocked" })
					{
						(MainWindowViewModel vm, FakeControllerService fake) = Create(code);
						switch (state)
						{
							case "running":
								vm.Overview.ToggleBridgeCommand.Execute(null);
								fake.Tick();
								break;
							case "blocked":
								SetHealth(fake, HidHideMissing(), true);
								break;
						}
						var window = new MainWindow { DataContext = vm, Width = width, Height = height };
						window.Show();
						RenderSettle();
						Button primary = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PrimaryAction");
						Assert.Equal(state != "running", primary.IsEffectivelyVisible);
						if (primary.IsEffectivelyVisible)
						{
							AssertInside(primary, window);
						}
						Save(window, outDir, $"bridge-overview-{state}-{size}-{code}.png");

						if (state != "blocked")
						{
							vm.Navigate(AppPage.SettingsGeneral);
							RenderSettle();
							Grid row = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "BridgeRow");
							Assert.True(row.IsEffectivelyVisible);
							Button button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BridgeRowButton");
							TextBlock stateText = window.GetVisualDescendants().OfType<TextBlock>().Single(b => b.Name == "BridgeRowState");
							Assert.True(button.IsEffectivelyVisible);
							Assert.Single(stateText.TextLayout.TextLines);
							AssertInside(button, row);
							// The button ends on the group's shared right edge (the switch above it).
							ToggleSwitch autoStart = window.GetVisualDescendants().OfType<ToggleSwitch>()
								.Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == Text.AutoStartBridge());
							double buttonRight = button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X;
							double switchRight = autoStart.TranslatePoint(new Point(autoStart.Bounds.Width, 0), window)!.Value.X;
							Assert.True(Math.Abs(buttonRight - switchRight) < 1, $"button ends at {buttonRight}, the switch at {switchRight}");
							Save(window, outDir, $"bridge-settings-{state}-{size}-{code}.png");
						}
						window.Close();
					}
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The control ends inside its container (nothing pushed out on the right).
	private static void AssertInside(Control control, Visual container)
	{
		Point? right = control.TranslatePoint(new Point(control.Bounds.Width, 0), container);
		Assert.NotNull(right);
		Assert.True(right!.Value.X <= container.Bounds.Width + 0.5, $"{control.Name} ends outside its container");
	}

	private static void Save(Window window, string? outDir, string file)
	{
		using var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		if (outDir != null)
		{
			frame!.Save(Path.Combine(outDir, file));
		}
	}

	private static void RenderSettle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Thread.Sleep(320);
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
	}
}
