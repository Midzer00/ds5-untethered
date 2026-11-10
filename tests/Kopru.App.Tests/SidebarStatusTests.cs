// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Bridge;
using Kopru.Core.Health;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The virtual controller's state beside the battery in the sidebar's controller card, and the bridge command's busy guard.
public class SidebarStatusTests
{
	private static (MainWindowViewModel Vm, FakeControllerService Fake) Create()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings())));
		return (vm, fake);
	}

	private static void Settle() => Dispatcher.UIThread.RunJobs();

	[AvaloniaFact]
	public void State_FollowsTheBridge()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		Assert.Equal(Text.RowVirtualPad(), vm.Overview.VirtualLabel);
		Assert.Equal(Text.HealthOff(), vm.Overview.VirtualStateText);
		Assert.False(vm.Overview.IsVirtualStateOn);
		Assert.Equal(Text.BridgeStatusStopped(), vm.Overview.VirtualStatusTip);

		// Started elsewhere (the tray, the automatic start): the card follows.
		fake.StartBridgeAsync();
		Settle();
		Assert.Equal(Text.HealthOn(), vm.Overview.VirtualStateText);
		Assert.True(vm.Overview.IsVirtualStateOn);
		Assert.Equal(Text.BridgeStatusRunning(), vm.Overview.VirtualStatusTip);

		fake.IsConnected = false;
		fake.RaiseStateChanged();
		Settle();
		Assert.Equal(Text.HealthBridgeWaiting(), vm.Overview.VirtualStatusTip);
	}

	[AvaloniaFact]
	public void Error_IsRedWithTheReasonInTheTooltip()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		fake.Health = OverviewHealthTests.Report(bridge: BridgeState.Error, error: BridgeError.UsbIpMissing);
		fake.RaiseStateChanged();
		Settle();
		Assert.Equal(Text.HealthError(), vm.Overview.VirtualStateText);
		Assert.True(vm.Overview.IsVirtualStateProblem);
		Assert.False(vm.Overview.IsVirtualStateOn);
		Assert.Equal(fake.Health.Chip(HealthChipKind.Bridge).Reason, vm.Overview.VirtualStatusTip);
	}

	[AvaloniaFact]
	public void Blocked_SaysWhyInTheTooltip_AndTheBatteryIsADash()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		fake.IsConnected = false;
		fake.Health = OverviewHealthTests.Report(paired: false, noPad: true);
		fake.RaiseStateChanged();
		Settle();
		Assert.Equal(Text.HealthOff(), vm.Overview.VirtualStateText);
		Assert.Equal(fake.CanStartBridgeReason, vm.Overview.VirtualStatusTip);
		Assert.Equal("—", vm.BatteryRowText);
		Assert.Equal(Text.BatteryUnknown(), vm.BatteryRowTip);
	}

	// A start under way: the card says it is connecting, and a second press of the Overview button or the primary action
	// neither stops nor starts again.
	[AvaloniaFact]
	public async Task BridgeCommand_WhileBusy_TakesNoSecondPress()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		fake.StartGate = new TaskCompletionSource();
		Task start = vm.Overview.ToggleBridgeCommand.ExecuteAsync(null);
		Settle();
		Assert.True(vm.Overview.IsBridgeRunning);
		Assert.False(vm.Overview.CanToggleBridge);
		Assert.False(vm.Overview.CanPrimary);
		Assert.Equal(Text.BridgeConnecting(), vm.Overview.VirtualStateText);
		Assert.Equal(Text.HealthBridgeConnecting(), vm.Overview.VirtualStatusTip);

		await vm.Overview.PrimaryCommand.ExecuteAsync(null);
		Settle();
		Assert.Equal(0, fake.Stops);
		Assert.Equal(1, fake.Starts);

		fake.StartGate.SetResult();
		await start;
		Settle();
		Assert.True(vm.Overview.CanToggleBridge);
		Assert.Equal(Text.HealthOn(), vm.Overview.VirtualStateText);

		await vm.Overview.PrimaryCommand.ExecuteAsync(null);
		Assert.Equal(1, fake.Stops);
	}

	// Renders the card in every state in both languages and checks no text in it is cut off or wider than its room.
	// Writes sidebar-{off,starting,running,error,blocked}-{tr,en}.png to KOPRU_SIDEBAR_RENDER_DIR when it is set.
	[AvaloniaFact]
	public void Card_FitsEveryStateInBothLanguages()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_SIDEBAR_RENDER_DIR");
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
				foreach (string state in new[] { "off", "starting", "running", "error", "blocked" })
				{
					var fake = new FakeControllerService(animate: false) { BatteryPercent = 100 };
					var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { Language = code })));
					var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
					window.Show();
					Task? pending = null;
					switch (state)
					{
						case "starting":
							fake.StartGate = new TaskCompletionSource();
							pending = vm.Overview.ToggleBridgeCommand.ExecuteAsync(null);
							break;
						case "running":
							vm.Overview.ToggleBridgeCommand.Execute(null);
							fake.Tick();
							break;
						case "error":
							fake.Health = OverviewHealthTests.Report(bridge: BridgeState.Error, error: BridgeError.UsbIpMissing);
							fake.RaiseStateChanged();
							break;
						case "blocked":
							fake.IsConnected = false;
							fake.Health = OverviewHealthTests.Report(paired: false, noPad: true);
							fake.RaiseStateChanged();
							break;
					}
					RenderSettle();
					AssertNoTextCut(window, "VirtualLabel", "VirtualState");
					AssertOnOneLine(window, "VirtualLabel", "VirtualState");
					using (var frame = window.CaptureRenderedFrame())
					{
						Assert.NotNull(frame);
						if (outDir != null)
						{
							frame!.Save(Path.Combine(outDir, $"sidebar-{state}-{code}.png"));
						}
					}
					fake.StartGate?.TrySetResult();
					pending?.GetAwaiter().GetResult();
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The label and the state each fit on one line within the card.
	// The virtual controller's name and its state sit side by side (same line), the state to the right.
	private static void AssertOnOneLine(Window window, string left, string right)
	{
		TextBlock a = window.FindControl<TextBlock>(left)!, b = window.FindControl<TextBlock>(right)!;
		Avalonia.Point pa = Avalonia.VisualExtensions.TranslatePoint(a, new Avalonia.Point(0, a.Bounds.Height / 2), window)!.Value;
		Avalonia.Point pb = Avalonia.VisualExtensions.TranslatePoint(b, new Avalonia.Point(0, b.Bounds.Height / 2), window)!.Value;
		Assert.True(Math.Abs(pa.Y - pb.Y) < 1, $"{a.Text} / {b.Text} are not on one line");
		Assert.True(pb.X > pa.X, "the state is not to the right of the name");
	}

	private static void AssertNoTextCut(Window window, params string[] names)
	{
		foreach (string name in names)
		{
			TextBlock block = window.FindControl<TextBlock>(name)!;
			string text = block.Text ?? "";
			Assert.True(block.IsEffectivelyVisible && text.Length > 0, name);
			Assert.Single(block.TextLayout.TextLines);
			Assert.True(block.TextLayout.WidthIncludingTrailingWhitespace <= block.Bounds.Width + 0.5, $"{text} is wider than its room");
			Assert.Equal(TextTrimming.None, block.TextTrimming);
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
