// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;

namespace Kopru.App.Tests;

// Page switching stays cheap: a page's view is built once and kept, the long game list only builds the rows in view.
public class NavigationTests
{
	private static (MainWindowViewModel Vm, MainWindow Window, PageHost Host) Open(FakeControllerService service, AppEnvironment? environment = null)
	{
		var vm = new MainWindowViewModel(service, environment);
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		return (vm, window, window.GetVisualDescendants().OfType<PageHost>().Single());
	}

	private static void Go(MainWindowViewModel vm, Window window, NavItem item)
	{
		vm.SelectedNav = item;
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
	}

	private static Control Shown(PageHost host) => Assert.Single(host.Children, c => c.IsVisible);

	[AvaloniaFact]
	public void PageViews_AreBuiltOnce_AndReusedWhenShownAgain()
	{
		var service = new FakeControllerService(animate: false);
		(MainWindowViewModel vm, MainWindow window, PageHost host) = Open(service);
		service.StartBridgeAsync();
		var first = new Dictionary<PageViewModel, Control>();
		for (int round = 0; round < 3; round++)
		{
			foreach (NavItem item in vm.NavItems)
			{
				Go(vm, window, item);
				Control view = Shown(host);
				Assert.Same(item.Page, view.DataContext);
				if (round == 0)
				{
					first[item.Page] = view;
				}
				else
				{
					Assert.Same(first[item.Page], view);
				}
			}
		}
		Assert.Equal(vm.NavItems.Count, host.ViewsBuilt);
		Assert.Equal(vm.NavItems.Count, host.Children.Count);
		// The live pages still follow their visibility (the controller page's Test tab holds the controller only while shown).
		Go(vm, window, vm.NavItems.Single(n => n.Page == vm.Controller));
		Assert.NotNull(service.LiveHandler);
		Go(vm, window, vm.NavItems[0]);
		Assert.Null(service.LiveHandler);
		window.Close();
	}

	// The cached controller page view stays in the tree while hidden; its live readouts follow the page and the tab.
	[AvaloniaFact]
	public void ControllerPage_ReadsLiveOnlyWhileShown_AndOnTheTabsThatShowIt()
	{
		var service = new FakeControllerService(animate: false);
		(MainWindowViewModel vm, MainWindow window, PageHost host) = Open(service);
		Assert.Null(service.LiveHandler);
		Assert.False(vm.Sticks.IsPreviewRunning);

		vm.OpenController(ControllerPageViewModel.TestTab);
		Dispatcher.UIThread.RunJobs();
		Control page = Shown(host);
		Assert.IsType<ControllerView>(page);
		Assert.NotNull(service.LiveHandler);
		Assert.True(vm.Tester.IsVisible);
		Assert.False(vm.Sticks.IsPreviewRunning);

		// Remap tab: the tester lets go of the controller, the gyro preview polls the virtual controller.
		vm.Controller.SelectedTab = ControllerPageViewModel.RemapTab;
		Assert.Null(service.LiveHandler);
		Assert.False(vm.Tester.IsVisible);
		Assert.True(vm.Sticks.IsPreviewRunning);

		// Another page: the view is kept but hidden, nothing runs; coming back lands on the same tab.
		vm.Controller.SelectedTab = ControllerPageViewModel.TestTab;
		Go(vm, window, vm.NavItems.Single(n => n.Page == vm.Light));
		Assert.False(page.IsVisible);
		Assert.Contains(page, host.Children);
		Assert.Null(service.LiveHandler);
		Assert.False(vm.Tester.IsVisible);
		vm.Controller.SelectedTab = ControllerPageViewModel.RemapTab;
		Assert.False(vm.Sticks.IsPreviewRunning);
		Go(vm, window, vm.NavItems.Single(n => n.Page == vm.Controller));
		Assert.Same(page, Shown(host));
		Assert.Equal(ControllerPageViewModel.RemapTab, vm.Controller.SelectedTab);
		Assert.True(vm.Sticks.IsPreviewRunning);
		Assert.Null(service.LiveHandler);

		vm.Controller.SelectedTab = ControllerPageViewModel.TestTab;
		Assert.NotNull(service.LiveHandler);
		Go(vm, window, vm.NavItems[0]);
		Assert.Null(service.LiveHandler);
		Assert.False(vm.Sticks.IsPreviewRunning);
		window.Close();
	}

	[AvaloniaFact]
	public void SetupPage_IsDroppedOnceLeft()
	{
		var probes = new RealServiceTests.Probes(drivers: false);
		(MainWindowViewModel vm, MainWindow window, PageHost host) = Open(new FakeControllerService(animate: false), new AppEnvironment(new MemorySettingsStore(), null,
			() => new Kopru.Core.Setup.SetupWizard(new Kopru.Core.Setup.SystemCheck(probes, probes, probes, probes), new RealServiceTests.Links())));
		Assert.IsType<SetupView>(Shown(host));
		Go(vm, window, vm.NavItems[1]);
		Assert.DoesNotContain(host.Children, c => c is SetupView);
		vm.ShowSetup();
		Dispatcher.UIThread.RunJobs();
		Assert.IsType<SetupView>(Shown(host));
		Assert.Single(host.Children, c => c is SetupView);
		window.Close();
	}

	[AvaloniaFact]
	public void SupportedGameList_BuildsOnlyTheRowsInView()
	{
		var service = new FakeControllerService(animate: false);
		service.Games.SetCatalog(Kopru.Core.Games.GameCatalog.LoadEmbedded());
		(MainWindowViewModel vm, MainWindow window, _) = Open(service);
		Go(vm, window, vm.NavItems.Single(n => n.Page == vm.SupportedGames));
		ItemsControl list = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.ItemCount == vm.SupportedGames.Games.Count);
		Assert.True(list.ItemCount > 100, $"catalog has {list.ItemCount} games");
		Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
		Assert.InRange(list.GetRealizedContainers().Count(), 1, 40);
		window.Close();
	}

	[AvaloniaFact]
	public void LightPreview_TicksOnlyWhileTheLightPageIsShown()
	{
		(MainWindowViewModel vm, MainWindow window, PageHost host) = Open(new FakeControllerService(animate: false));
		Go(vm, window, vm.NavItems.Single(n => n.Page == vm.Light));
		LightView light = Assert.IsType<LightView>(Shown(host));
		Assert.True(light.IsPreviewRunning);
		Go(vm, window, vm.NavItems[0]);
		Assert.False(light.IsPreviewRunning);
		Go(vm, window, vm.NavItems.Single(n => n.Page == vm.Light));
		Assert.True(light.IsPreviewRunning);
		window.Close();
		Assert.False(light.IsPreviewRunning);
	}
}
