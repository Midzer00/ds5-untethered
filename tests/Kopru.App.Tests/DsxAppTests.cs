// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Localization;
using Kopru.Core.Mods;

namespace Kopru.App.Tests;

// DSX Mod Support: off by default, saved, pushed to the service; the service starts the listener while it is on and stops
// it when it goes off; the Settings row shows the hint while off and the listener's state while on.
public class DsxAppTests
{
	private readonly TestControllers controllers = new();

	[Fact]
	public void Setting_IsOffByDefault_Saved_AndPushed()
	{
		FakeControllerService fake = new();
		SettingsState settings = new(fake);
		Assert.False(settings.DsxModSupport);
		Assert.False(fake.Settings.DsxModSupport);
		Assert.False(new AppSettings().DsxModSupport);

		settings.DsxModSupport = true;
		Assert.True(fake.Settings.DsxModSupport);
		Assert.True(settings.ToAppSettings().DsxModSupport);
	}

	[Fact]
	public void Service_StartsTheListenerWhileOn_AndStopsItWhenOff()
	{
		BridgeHost host = new(controllers, new TestIdentity(true), new TestAttacher());
		using RealControllerService service = new(host, new TestHiding(), controllers, a => a(), _ => null, timers: false) { DsxPort = 0 };
		int changes = 0;
		service.DsxStatusChanged += (_, _) => changes++;
		Assert.Null(service.Dsx);
		Assert.Equal(DsxListenerState.Off, service.DsxStatus.State);

		service.ApplySettings(new ControllerSettings { DsxModSupport = true });
		DsxListener listener = Assert.IsType<DsxListener>(service.Dsx);
		Assert.Equal(DsxListenerState.Listening, service.DsxStatus.State);
		Assert.Equal(listener.BoundPort, service.DsxStatus.Port);
		Assert.True(changes > 0);

		// The same settings again: the same listener.
		service.ApplySettings(new ControllerSettings { DsxModSupport = true, RumbleLevel = 90 });
		Assert.Same(listener, service.Dsx);

		service.ApplySettings(new ControllerSettings { DsxModSupport = false });
		Assert.Null(service.Dsx);
		Assert.Equal(DsxListenerState.Off, listener.Status.State);
		Assert.Equal(DsxListenerState.Off, service.DsxStatus.State);
	}

	[Fact]
	public void Service_ModOutputOfIndexZero_ReachesTheFirstHost()
	{
		BridgeHost host = new(controllers, new TestIdentity(true), new TestAttacher());
		using RealControllerService service = new(host, new TestHiding(), controllers, a => a(), _ => null, timers: false) { DsxPort = 0 };
		service.ApplySettings(new ControllerSettings { DsxModSupport = true });
		service.Dsx!.Handle("{\"instructions\":[{\"type\":2,\"parameters\":[0,1,2,3]}]}"u8,
			new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 50000));
		Assert.Equal(((byte)1, (byte)2, (byte)3), host.ModOutput!.Lightbar);

		// Turning the setting off ends the mod's output.
		service.ApplySettings(new ControllerSettings { DsxModSupport = false });
		Assert.Null(host.ModOutput);
	}

	[AvaloniaFact]
	public void SettingsRow_HintWhileOff_StateWhileOn_PortInUseAsAProblem()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			FakeControllerService fake = new();
			SettingsViewModel vm = new(fake, new SettingsState(fake));
			Assert.Equal("DSX Mod Support", vm.DsxModSupportLabel);
			Assert.Equal("Takes trigger and light commands from game mods made for DSX.", vm.DsxModSupportShort);
			Assert.True(vm.ShowsDsxHint);
			Assert.False(vm.DsxStatusIsFine || vm.DsxStatusIsProblem);

			List<string?> changed = [];
			vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
			fake.DsxStatus = new DsxStatus(DsxListenerState.Listening, 6969, false, null);
			vm.Settings.DsxModSupport = true;
			Assert.Contains(nameof(SettingsViewModel.DsxStatusText), changed);
			Assert.False(vm.ShowsDsxHint);
			Assert.True(vm.DsxStatusIsFine);
			Assert.Equal("Listening on port 6969.", vm.DsxStatusText);

			fake.DsxStatus = new DsxStatus(DsxListenerState.Listening, 6969, true, DateTimeOffset.UtcNow.AddSeconds(-5));
			changed.Clear();
			fake.RaiseDsxStatusChanged();
			Assert.Contains(nameof(SettingsViewModel.DsxStatusText), changed);
			Assert.StartsWith("Listening on port 6969; last message from a mod ", vm.DsxStatusText);

			fake.DsxStatus = new DsxStatus(DsxListenerState.PortInUse, 6969, false, null);
			fake.RaiseDsxStatusChanged();
			Assert.True(vm.DsxStatusIsProblem);
			Assert.False(vm.DsxStatusIsFine);
			Assert.Equal("Port 6969 is in use (DSX may be running); not listening.", vm.DsxStatusText);

			Localizer.Chosen = Language.Turkish;
			Assert.Equal("DSX Mod Desteği", vm.DsxModSupportLabel);
			Assert.Equal("DSX için yapılmış oyun modlarının tetik ve ışık komutlarını alır.", vm.DsxModSupportShort);
			Assert.Equal("Port 6969 kullanımda (DSX açık olabilir); dinlenmiyor.", vm.DsxStatusText);
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}
}
