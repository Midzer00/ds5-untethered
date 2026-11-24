// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The Overview's status line ends in one connection label, and on the cable the live card says "Wired" (no verdict, no
// endless "Measuring") with a chart scale that starts at 2 ms.
public sealed class OverviewConnectionLabelTests
{
	private static (FakeControllerService Fake, MainWindowViewModel Vm) Open()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now })));
		return (fake, vm);
	}

	[AvaloniaFact]
	public void StatusLine_EndsInTheConnectionLabel()
	{
		(FakeControllerService fake, MainWindowViewModel vm) = Open();
		Assert.True(fake.IsConnected);
		Assert.EndsWith(" · " + "Bluetooth", vm.Overview.StatusLine);
		fake.CablePluggedIn = true;
		Assert.EndsWith(" · " + Text.ConnectionBluetoothCable(), vm.Overview.StatusLine);
		fake.CablePluggedIn = false;
		fake.IsWired = true;
		Assert.EndsWith(" · " + Text.ConnectionWired(), vm.Overview.StatusLine);
	}

	[AvaloniaFact]
	public void OnTheCable_TheLiveCardSaysWired_AndNeverMeasuring()
	{
		(FakeControllerService fake, MainWindowViewModel vm) = Open();
		fake.IsWired = true;
		Assert.Equal(Text.LinkWired(), vm.Overview.LinkStateText);
		Assert.False(vm.Overview.IsLinkMeasuring);
		Assert.True(vm.Overview.IsLinkMuted);
		Assert.Equal(0, vm.Overview.LinkLevel);
		Assert.Equal(Text.LiveTypicalDelayWired(), vm.Overview.Live.DelayCaption);
	}

	[Fact]
	public void TheWiredChart_StartsAtTwoMilliseconds()
	{
		Assert.Equal(10, LivePanelViewModel.ChartTopFor([0.5, 1.0]));
		Assert.Equal(2, LivePanelViewModel.ChartTopFor([0.5, 1.0], wired: true));
		Assert.Equal(3, LivePanelViewModel.ChartTopFor([2.0], wired: true));
	}
}
