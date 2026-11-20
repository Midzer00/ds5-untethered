// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Bridge;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// Where the stick and trigger settings live: one controller page (no separate sticks, triggers or test page). Its Test tab
// fixes what it shows through the page's own editors, and every setting is on one tab only.
public class PageLayoutTests
{
	private static (MainWindowViewModel Vm, FakeControllerService Fake) NewViewModel()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		vm.Sticks.StopPreview();
		return (vm, fake);
	}

	[AvaloniaFact]
	public void Sidebar_HasOneControllerEntry_WithTestOutputsAndRemapTabs()
	{
		(MainWindowViewModel vm, _) = NewViewModel();
		Assert.Equal(6, vm.NavItems.Count);
		Assert.Equal([vm.Overview, vm.SupportedGames, vm.OtherGames, vm.Controller, vm.Light, vm.SettingsPage], vm.Pages);
		Assert.Equal(Text.NavController(), vm.Controller.Title);
		Assert.Same(vm.Sticks.Triggers, vm.Triggers);
		Assert.Same(vm.Triggers, vm.Tester.Triggers);
		Assert.Equal(["Test", Text.TesterOutputs(), Text.RemapTab()], vm.Controller.Tabs.Select(t => t.Name));
		Assert.True(vm.Controller.IsTestTab);
		vm.Controller.SelectedTab = ControllerPageViewModel.RemapTab;
		// Nothing selected (-1) and a tab that does not exist are ignored.
		vm.Controller.SelectedTab = -1;
		vm.Controller.SelectedTab = 3;
		Assert.True(vm.Controller.IsRemapTab);
		vm.Controller.SelectedTab = ControllerPageViewModel.OutputsTab;
		Assert.True(vm.Controller.IsOutputsTab);
		// Opening a tab that does not exist opens Test.
		vm.OpenController(3);
		Assert.True(vm.Controller.IsTestTab);
		Assert.Same(vm.Controller, vm.CurrentPage);
		vm.OpenController(ControllerPageViewModel.RemapTab);
		Assert.True(vm.Controller.IsRemapTab);
	}

	// Every setting shows on one tab only: the stick settings and trigger ranges on the Test tab (beside the live test), the
	// trigger effects and strength on the Output Tests tab, remapping and gyro on their own tab.
	[AvaloniaFact]
	public void EverySetting_IsOnOneTabOnly()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			(MainWindowViewModel vm, _) = NewViewModel();
			var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
			window.Show();
			vm.OpenController(ControllerPageViewModel.TestTab);
			Dispatcher.UIThread.RunJobs();
			var seen = new Dictionary<string, int>();
			ControllerView page = window.GetVisualDescendants().OfType<ControllerView>().Single();
			foreach (int tab in new[] { ControllerPageViewModel.TestTab, ControllerPageViewModel.OutputsTab, ControllerPageViewModel.RemapTab })
			{
				vm.Controller.SelectedTab = tab;
				Dispatcher.UIThread.RunJobs();
				window.UpdateLayout();
				foreach (string label in page.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").Distinct())
				{
					seen[label] = seen.GetValueOrDefault(label) + (label.Length > 0 ? 1 : 0);
				}
			}
			foreach (string label in new[] { Text.InnerDeadzoneLabel(), Text.OuterDeadzoneLabel(), Text.TriggerStartLabel(), Text.TriggerEndLabel(),
				Text.DriftCalibration(), Text.AntiDeadzoneLabel(), Text.ResponseCurveLabel(), Text.TriggerStrength(), Text.TesterTriggerEffects() })
			{
				Assert.True(seen.GetValueOrDefault(label) == 1, $"{label}: {seen.GetValueOrDefault(label)} tabs");
			}
			window.Close();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The Test tab fits the window and opens at the top; the Output Tests tab holds the trigger effect picker, and picking an
	// effect plays it on the controller.
	[AvaloniaFact]
	public void TestTab_OpensAtTheTop_AndTheTriggerEffectPickerPlaysTheEffect()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		vm.OpenController(ControllerPageViewModel.TestTab);
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
		ControllerView page = window.GetVisualDescendants().OfType<ControllerView>().Single();
		ScrollViewer scroll = page.GetVisualDescendants().OfType<ScrollViewer>().First();
		Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 0.5, $"extent {scroll.Extent.Height} > viewport {scroll.Viewport.Height}");
		Assert.Equal(0, scroll.Offset.Y);
		Assert.DoesNotContain(page.GetVisualDescendants().OfType<ListBox>(), l => l.Name == "TriggerEffects" && l.IsEffectivelyVisible);

		vm.Controller.SelectedTab = ControllerPageViewModel.OutputsTab;
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
		Assert.False(vm.Tester.IsVisible); // the live input is held only on the Test tab
		ListBox effects = page.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TriggerEffects");
		Assert.True(effects.IsEffectivelyVisible);
		Assert.Equal(4, effects.ItemCount);
		int tests = fake.TestCount;
		effects.SelectedIndex = 2;
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(TriggerTestEffect.Weapon, vm.Triggers.ActiveEffect);
		Assert.Equal(tests + 1, fake.TestCount);
		window.Close();
	}

	[AvaloniaFact]
	public void EveryLink_ResolvesToAPageInTheSidebar()
	{
		(MainWindowViewModel vm, _) = NewViewModel();
		foreach (AppPage page in Enum.GetValues<AppPage>())
		{
			vm.Navigate(page);
			Assert.Contains(vm.CurrentPage, vm.Pages);
		}
		vm.Overview.Quick.AdvancedCommand.Execute(null);
		Assert.Same(vm.SettingsPage, vm.CurrentPage);
	}

	[AvaloniaFact]
	public void Tester_EditsThePagesEditors_AndTheChangeReachesTheControllerOnce()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		TesterViewModel tester = vm.Tester;
		Assert.Same(vm.Sticks, tester.Tuning);
		Assert.Same(vm.Sticks.LeftStick, tester.LeftPanel.Tune);
		Assert.Same(vm.Sticks.RightStick, tester.RightPanel.Tune);
		Assert.Same(vm.Sticks.L2, tester.L2Panel.Tune);
		Assert.Same(vm.Sticks.R2, tester.R2Panel.Tune);

		int applies = fake.Applies;
		tester.LeftPanel.Tune.Inner = 14;
		Assert.Equal(applies + 1, fake.Applies);
		Assert.Equal(14, fake.Settings.Tuning.LeftStick.InnerDeadzone);
		Assert.Equal(14, vm.Sticks.LeftStick.Inner);
		Assert.Equal(Text.PercentValue(14), vm.Sticks.LeftStick.InnerText);
		// The tester's preview shows the new deadzone at once, even before any input arrived.
		Assert.Equal(14, tester.LeftStick.Inner);

		// With input shown, a trigger start above the press reads 0 for games while the raw level stays.
		vm.OpenController(ControllerPageViewModel.TestTab);
		fake.LiveHandler!(TesterTests.Sample);
		tester.Tick();
		Assert.True(tester.L2.GameLevel > 30);
		applies = fake.Applies;
		tester.L2Panel.Tune.Start = 50;
		Assert.Equal(applies + 1, fake.Applies);
		Assert.Equal(50, vm.Sticks.L2.Start);
		Assert.Equal(0, tester.L2.GameLevel);
		Assert.Equal(96 / 255.0 * 100, tester.L2.Level, 3);

		// And the other way round: a change elsewhere (a profile, reset) moves the tester's dot.
		double raw = tester.RightStick.RawX;
		vm.Sticks.RightStick.Inner = 50;
		Assert.Equal(50, tester.RightStick.Inner);
		Assert.Equal(raw, tester.RightStick.RawX);
		Assert.True(Math.Abs(tester.RightStick.TunedX) < Math.Abs(raw));
	}

	[AvaloniaFact]
	public async Task DriftCalibration_RunsOnTheTestTab()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		vm.OpenController(ControllerPageViewModel.TestTab);
		Assert.Same(vm.Sticks.CalibrateCommand, vm.Tester.Tuning.CalibrateCommand);

		await vm.Tester.Tuning.CalibrateCommand.ExecuteAsync(null);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(1, fake.Calibrations);
		Assert.Equal(3, fake.Settings.Tuning.LeftStick.DriftX);
		Assert.Equal(6, vm.Tester.LeftStick.Inner);
		Assert.Equal(4, vm.Tester.RightStick.Inner);
		Assert.Equal(Text.CalibrationDone(), vm.Sticks.CalibrationText);
		// Still on the Test tab.
		Assert.Same(vm.Controller, vm.CurrentPage);
		Assert.True(vm.Controller.IsTestTab);
	}

	[AvaloniaFact]
	public void SetupPage_StopsTheControllerPagesLiveInput_WhileItCoversThePage()
	{
		var probes = new RealServiceTests.Probes(drivers: false);
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings()), null,
			() => new Kopru.Core.Setup.SetupWizard(new Kopru.Core.Setup.SystemCheck(probes, probes, probes, probes), new RealServiceTests.Links())));
		vm.OpenController(ControllerPageViewModel.TestTab);
		Assert.NotNull(fake.LiveHandler);
		vm.ShowSetup();
		Assert.Null(fake.LiveHandler);
		vm.CloseSetup();
		Assert.Same(vm.Overview, vm.CurrentPage);
		Assert.Null(fake.LiveHandler);
		vm.Navigate(vm.Controller);
		Assert.NotNull(fake.LiveHandler);
	}

	[AvaloniaFact]
	public void ControllerTabs_KeepTheSelection_WhenTheLanguageChanges()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.Turkish;
			(MainWindowViewModel vm, _) = NewViewModel();
			var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
			window.Show();
			vm.OpenController(ControllerPageViewModel.RemapTab);
			Dispatcher.UIThread.RunJobs();
			ListBox tabs = window.GetVisualDescendants().OfType<ListBox>().Single(l => ReferenceEquals(l.ItemsSource, vm.Controller.Tabs));
			Assert.Equal(ControllerPageViewModel.RemapTab, tabs.SelectedIndex);

			Localizer.Chosen = Language.English;
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(ControllerPageViewModel.RemapTab, vm.Controller.SelectedTab);
			Assert.Equal(Text.RemapTab(Language.English), vm.Controller.Tabs[ControllerPageViewModel.RemapTab].Name);
			Assert.Equal(ControllerPageViewModel.RemapTab, tabs.SelectedIndex);
			Assert.True(window.GetVisualDescendants().OfType<RemapView>().Single().IsEffectivelyVisible);
			window.Close();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}


	// The real service calibrates from a direct read when the bridge is stopped (the test page works without it).
	[Fact]
	public async Task RealService_CalibratesDrift_WithoutTheBridge()
	{
		var controllers = new TestControllers();
		var source = new LiveSource();
		using var service = new RealControllerService(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
			BatteryProbe = new BatteryProbe(source)
		}, new TestHiding(), controllers, a => a(), _ => null, timers: false);

		using var feeding = new CancellationTokenSource();
		Task feed = Task.Run(async () =>
		{
			while (!feeding.IsCancellationRequested)
			{
				foreach (LiveLink link in source.Opened.Where(l => !l.Disposed))
				{
					for (int i = 0; i < 10; i++)
					{
						link.Feed(200); // the same position the source opens with: the stick never moved
					}
				}
				await Task.Delay(5);
			}
		});
		DriftCalibrationResult result = await service.CalibrateDriftAsync(TimeSpan.FromMilliseconds(400));
		feeding.Cancel();
		await feed;

		Assert.Equal(DriftCalibrationStatus.Success, result.Status);
		Assert.True(result.LeftX > 0);
		// The read-only handle opened for it is released afterwards, and nothing was written to the pad.
		Assert.True(SpinWait.SpinUntil(() => source.Opened.All(l => l.Disposed), 5000));
		Assert.All(source.Opened, l => Assert.Empty(l.Writes));
	}
}
