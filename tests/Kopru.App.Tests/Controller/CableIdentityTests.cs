// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Hid;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

// The pads Windows lists, like the real sources: every DualSense for the presence look (IControllerFinder), only the
// Bluetooth ones as controllers (IControllerSource.Enumerate, keyed by their Bluetooth id like WindowsControllerSource).
internal sealed class CablePads(ulong address) : IControllerSource, IControllerFinder
{
	public const string BtId = @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\cable-test";
	public const string UsbId = @"HID\VID_054C&PID_0CE6&MI_03\cable-test";

	// The instance id the service hides for a listed pad.
	public static string InstanceId(HidDevice d) => d.Transport == Transport.Usb ? UsbId : BtId;

	private readonly object gate = new();
	private readonly List<HidDevice> listed = [];

	public void Set(params HidDevice[] devices)
	{
		lock (gate)
		{
			listed.Clear();
			listed.AddRange(devices);
		}
	}

	public IReadOnlyList<HidDevice> Find()
	{
		lock (gate)
		{
			return [.. listed];
		}
	}

	public IReadOnlyList<ControllerPresence> Enumerate() =>
		[.. Find().Where(d => d.Transport == Transport.Bluetooth).Select(d => new ControllerPresence(BtId, d))];

	public IHidLink? TryOpen() => null;

	public IHidLink? TryOpen(string id) =>
		id == BtId && Find().Any(d => d.Transport == Transport.Bluetooth) ? new InfoHidLink(Transport.Bluetooth, address) : null;
}

// One physical controller, one identity: plugged in by cable while it stays connected over Bluetooth (what the owner's
// hardware does), it is still the one remembered controller with its look, name and own profile; the cable only charges
// (the bridge keeps running, the USB twin is hidden) and a USB pad that drops out of the listing for one tick is not
// unhidden for that. Made-up address, no real one.
[Collection("ControllerLook")]
public sealed class CableIdentityTests
{
	private const ulong Address = 0xA0B1C2D3E4F5;
	private const string Red = "metallic-red";

	private static readonly HidDevice BtPad = new(TestLink.BtPath, 0x054C, 0x0CE6, 78, 547, 64);
	private static readonly HidDevice UsbPad = new(InfoHidLink.UsbPath, 0x054C, 0x0CE6, 64, 48, 64);

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private static List<string> HidingCalls(TestHiding hiding)
	{
		lock (hiding.Calls)
		{
			return [.. hiding.Calls];
		}
	}

	private static BridgeHost Host(CablePads pads) => new(pads, new TestIdentity(true), new TestAttacher())
	{
		PollInterval = TimeSpan.FromMilliseconds(10),
		MonitorInterval = TimeSpan.FromMilliseconds(10),
		NoInputTimeout = Timeout.InfiniteTimeSpan,
		// Wired mode may be chosen in a test; none opens a real USB link or audio endpoint.
		OpenWired = _ => null,
		OpenWiredAudio = _ => null,
		OpenWiredMic = (_, _) => null
	};

	// The owner's report: Metallic Red chosen through the app's own view models, then the cable goes in and the look is
	// gone, in memory and in settings.json. The whole view model graph runs here, with a store.
	[AvaloniaFact]
	public async Task LookChosenInThePicker_SurvivesTheCable_InMemoryAndSaved()
	{
		using IDisposable pack = PadPack.Override([new PadVariant(Red, false, "Metallic Red", "Metalik Kırmızı", Colors.DarkRed)]);
		CablePads pads = new(Address);
		pads.Set(BtPad);
		StepTime time = new();
		BridgeHost primary = Host(pads);
		BridgeManager manager = new(pads, primary, _ => Host(pads));
		GameModeController games = new(FakeControllerService.SampleCatalog, new FakeGameWatcher(null), manager);
		using RealControllerService service = new(primary, new TestHiding(), pads, a => a(), CablePads.InstanceId, timers: false, games: games,
			time: time);
		service.UseManager(manager, watch: false);
		var store = new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now });
		var vm = new MainWindowViewModel(service, new AppEnvironment(store));
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);
		ControllerLookTests.Settle();

		ControllerLookViewModel look = vm.Controller.Look;
		look.OpenCommand.Execute(null);
		look.Choose(look.Tiles.Single(t => t.Key == Red));
		look.CloseCommand.Execute(null);
		ControllerLookTests.Settle();
		Assert.Equal(Red, vm.Settings.KnownControllers.Find(CablePads.BtId)?.Look);
		vm.Settings.SaveNow();
		Assert.Equal(Red, store.Saved!.KnownControllers!.Single().Look);

		// Cable in: it only charges, the bridge keeps running.
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		pads.Set(BtPad, UsbPad);
		service.DeviceTick();
		await manager.RefreshAsync();
		WaitUntil(() => service.CablePluggedIn);
		Assert.True(service.IsBridgeRunning);
		for (int i = 0; i < 5; i++)
		{
			time.Advance(TimeSpan.FromSeconds(2));
			service.RefreshPresence();
			await manager.RefreshAsync();
			ControllerLookTests.Settle();
		}

		Assert.Equal(Red, vm.Settings.KnownControllers.Find(CablePads.BtId)?.Look);
		Assert.Equal(PadLook.Variant(Red), look.Current);
		vm.Settings.SaveNow();
		Assert.Equal(Red, store.Saved!.KnownControllers!.Single().Look);
		await service.StopBridgeAsync();
	}

	[AvaloniaFact]
	public async Task SameController_OnBluetoothAndCable_KeepsOneIdentity_ItsLookNameAndProfile()
	{
		using IDisposable pack = PadPack.Override([new PadVariant(Red, false, "Metallic Red", "Metalik Kırmızı", Colors.DarkRed)]);
		CablePads pads = new(Address);
		pads.Set(BtPad);
		StepTime time = new();
		BridgeHost primary = Host(pads);
		BridgeManager manager = new(pads, primary, _ => Host(pads));
		GameModeController games = new(FakeControllerService.SampleCatalog, new FakeGameWatcher(null), manager);
		TestHiding hiding = new();
		using RealControllerService service = new(primary, hiding, pads, a => a(), CablePads.InstanceId, timers: false, games: games,
			time: time);
		service.UseManager(manager, watch: false);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);

		var settings = new SettingsState(service);
		Dispatcher.UIThread.RunJobs();
		Assert.True(settings.SetLook(CablePads.BtId, Red));
		Assert.True(settings.RenameController(CablePads.BtId, "Red One"));
		service.SetControllerProfile(CablePads.BtId, new ControllerSettings { LightbarColour = 0xFF0000 });
		var look = new ControllerLookViewModel(service, settings);
		Assert.Equal(PadLook.Variant(Red), look.Current);

		// Cable in, Bluetooth stays: the USB pad answers 0x09 with the same address, so it is hidden as the controller's
		// USB twin and the bridge keeps running on Bluetooth.
		service.OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, Address);
		pads.Set(BtPad, UsbPad);
		service.DeviceTick();
		await manager.RefreshAsync();
		WaitUntil(() => HidingCalls(hiding).Contains("hide usb " + CablePads.UsbId));
		Assert.True(service.CablePluggedIn);
		Assert.False(service.IsPausedForCable);
		Assert.True(service.IsBridgeRunning);
		Dispatcher.UIThread.RunJobs();
		look.Refresh();

		// Still one controller, under its Bluetooth id: the USB pad never becomes a second one.
		ControllerEntry only = Assert.Single(service.Controllers);
		Assert.Equal(CablePads.BtId, only.Id);
		Assert.True(only.HasOwnProfile);
		Assert.True(service.IsConnected);
		Assert.Equal(CablePads.BtId, service.SelectedController!.Id);
		settings.KnownControllers.Observe(service.Controllers);
		Assert.Single(settings.KnownControllers.Items);
		Assert.True(look.IsAvailable);
		Assert.Equal(PadLook.Variant(Red), look.Current);
		Assert.Equal(PadLook.Variant(Red), settings.ActiveLook);
		Assert.Equal("Red One", settings.ControllerName(only));

		// The USB pad drops out of the listing for one tick and comes back: it is not unhidden for that.
		pads.Set(BtPad);
		service.DeviceTick();
		time.Advance(TimeSpan.FromSeconds(2));
		pads.Set(BtPad, UsbPad);
		service.DeviceTick();
		time.Advance(TimeSpan.FromSeconds(2));
		service.DeviceTick();
		await Task.Delay(100);
		Assert.True(service.IsBridgeRunning);
		Assert.True(service.CablePluggedIn);
		Assert.DoesNotContain("unhide usb " + CablePads.UsbId, HidingCalls(hiding));
		Assert.Equal(CablePads.BtId, Assert.Single(service.Controllers).Id);

		// Unplugged for good: the USB twin is unhidden; the same controller stays bridged, still with its look.
		pads.Set(BtPad);
		service.DeviceTick();
		time.Advance(RealControllerService.CableResumeDelay);
		service.DeviceTick();
		WaitUntil(() => HidingCalls(hiding).Contains("unhide usb " + CablePads.UsbId));
		Assert.False(service.CablePluggedIn);
		Assert.True(service.IsGameConnected);
		Assert.DoesNotContain("restore", HidingCalls(hiding));
		Dispatcher.UIThread.RunJobs();
		look.Refresh();
		Assert.Equal(CablePads.BtId, service.SelectedController!.Id);
		Assert.Equal(PadLook.Variant(Red), look.Current);
		Assert.Single(settings.KnownControllers.Items);
		await service.StopBridgeAsync();
	}
}
