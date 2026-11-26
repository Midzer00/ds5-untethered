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

namespace Kopru.App.Tests;

// Wired mode taking over a controller that is on the cable alone: it is the same controller as over Bluetooth, found by
// its own Bluetooth address (feature 0x09), so it comes back under its Bluetooth id with its look, name and own profile.
// A controller that does not answer 0x09 over USB is never taken over and never becomes a second entry. Made-up address.
[Collection("ControllerLook")]
public sealed class CableOnlyIdentityTests
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

	private static BridgeHost Host(IControllerSource source) => new(source, new TestIdentity(true), new TestAttacher())
	{
		PollInterval = TimeSpan.FromMilliseconds(10),
		MonitorInterval = TimeSpan.FromMilliseconds(10),
		NoInputTimeout = Timeout.InfiniteTimeSpan,
		OpenWired = _ => null,
		OpenWiredAudio = _ => null,
		OpenWiredMic = (_, _) => null
	};

	private sealed record Rig(CablePads Pads, BridgeManager Manager, RealControllerService Service, TestHiding Hiding, StepTime Time,
		List<InfoHidLink> UsbOpens);

	private static Rig Build(ulong usbAnswers)
	{
		CablePads pads = new(Address);
		List<InfoHidLink> usbOpens = [];
		CableControllerSource source = new(pads, _ =>
		{
			InfoHidLink link = new(Transport.Usb, Address);
			lock (usbOpens)
			{
				usbOpens.Add(link);
			}
			return link;
		});
		StepTime time = new();
		BridgeHost primary = Host(source);
		BridgeManager manager = new(source, primary, _ => Host(source));
		GameModeController games = new(FakeControllerService.SampleCatalog, new FakeGameWatcher(null), manager);
		TestHiding hiding = new();
		RealControllerService service = new(primary, hiding, pads, a => a(), CablePads.InstanceId, timers: false, games: games, time: time)
		{
			OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, usbAnswers)
		};
		service.UseManager(manager, watch: false);
		return new Rig(pads, manager, service, hiding, time, usbOpens);
	}

	// Ticks the cable switch and the manager until the condition holds (the hide, then the offer, then the bind).
	private static async Task TickUntil(Rig rig, Func<bool> condition)
	{
		for (int i = 0; i < 50 && !condition(); i++)
		{
			rig.Service.DeviceTick();
			await rig.Manager.RefreshAsync();
			await Task.Delay(20);
		}
		Assert.True(condition(), "timed out");
	}

	[AvaloniaFact]
	public async Task SeenOverBluetoothFirst_ThenOnTheCableAlone_ComesBackUnderTheSameId_WithItsLookAndProfile()
	{
		using IDisposable pack = PadPack.Override([new PadVariant(Red, false, "Metallic Red", "Metalik Kırmızı", Colors.DarkRed)]);
		Rig rig = Build(Address);
		using RealControllerService service = rig.Service;
		rig.Pads.Set(BtPad);
		await service.StartBridgeAsync();
		WaitUntil(() => service.IsGameConnected && service.Details?.Address == Address);
		var settings = new SettingsState(service);
		Dispatcher.UIThread.RunJobs();
		settings.KnownControllers.Observe(service.Controllers);
		Assert.Equal(Address, settings.KnownControllers.Find(CablePads.BtId)?.Address);
		Assert.True(settings.SetLook(CablePads.BtId, Red));
		Assert.True(settings.RenameController(CablePads.BtId, "Red One"));
		service.SetControllerProfile(CablePads.BtId, new Kopru.Core.Bridge.ControllerSettings { LightbarColour = 0xFF0000 });

		// The controller goes off Bluetooth; later it is on the cable alone, with wired mode chosen.
		await service.StopBridgeAsync();
		rig.Pads.Set(UsbPad);
		service.CableMode = CableMode.Wired;
		await service.StartBridgeAsync();
		await TickUntil(rig, () => service.IsGameConnected && rig.Manager.Primary.IsUsbOnly);

		Assert.Contains("hide usb " + CablePads.UsbId, HidingCalls(rig.Hiding));
		ControllerEntry only = Assert.Single(service.Controllers);
		Assert.Equal(CablePads.BtId, only.Id);
		Assert.True(only.HasOwnProfile);
		Assert.Equal(Address, only.Address);
		Dispatcher.UIThread.RunJobs();
		settings.KnownControllers.Observe(service.Controllers);
		KnownController known = Assert.Single(settings.KnownControllers.Items);
		Assert.Equal(Red, known.Look);
		Assert.Equal("Red One", settings.ControllerName(only));
		Assert.False(service.IsPausedForCable);

		// The cable is pulled: the controller is gone, the bridge waits; the twin is unhidden after the delay.
		rig.Pads.Set();
		lock (rig.UsbOpens)
		{
			rig.UsbOpens[^1].Drop();
		}
		service.DeviceTick();
		WaitUntil(() => !service.IsGameConnected);
		rig.Time.Advance(RealControllerService.CableResumeDelay);
		service.DeviceTick();
		WaitUntil(() => HidingCalls(rig.Hiding).Contains("unhide usb " + CablePads.UsbId));
		Assert.True(service.IsBridgeRunning);
		await service.StopBridgeAsync();
	}

	[AvaloniaFact]
	public async Task AppStartedWithTheControllerOnTheCable_IsTakenOver_FromTheRememberedAddress()
	{
		Rig rig = Build(Address);
		using RealControllerService service = rig.Service;
		DateTimeOffset seen = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
		var store = new MemorySettingsStore(new AppSettings
		{
			CableMode = CableMode.Wired,
			KnownControllers = [new KnownController(CablePads.BtId, ControllerModel.DualSense, "Blue One", seen, seen, Address: Address)]
		});
		var settings = new SettingsState(service, store);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(CableMode.Wired, service.CableMode);
		rig.Pads.Set(UsbPad);
		await service.StartBridgeAsync();
		await TickUntil(rig, () => service.IsGameConnected && rig.Manager.Primary.IsUsbOnly);

		Assert.Equal(CablePads.BtId, Assert.Single(service.Controllers).Id);
		Dispatcher.UIThread.RunJobs();
		settings.KnownControllers.Observe(service.Controllers);
		Assert.Equal("Blue One", Assert.Single(settings.KnownControllers.Items).Name);

		// Charge only chosen while it is held: it is let go and left to games (the bridge stands aside for the cable).
		service.CableMode = CableMode.ChargeOnly;
		await TickUntil(rig, () => !service.IsGameConnected);
		Assert.False(rig.Manager.Primary.IsUsbOnly);
		await service.StopBridgeAsync();
	}

	[AvaloniaFact]
	public async Task AUsbControllerThatDoesNotAnswer0x09_IsNotTakenOver_AndMakesNoNewEntry()
	{
		List<string> lines = [];
		void Watch(string line)
		{
			if (line.StartsWith("cable:", StringComparison.Ordinal))
			{
				lock (lines)
				{
					lines.Add(line);
				}
			}
		}
		Kopru.Core.Diagnostics.AppLog.Written += Watch;
		try
		{
			Rig rig = Build(0);
			using RealControllerService service = rig.Service;
			DateTimeOffset seen = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
			var settings = new SettingsState(service, new MemorySettingsStore(new AppSettings
			{
				CableMode = CableMode.Wired,
				KnownControllers = [new KnownController(CablePads.BtId, ControllerModel.DualSense, null, seen, seen, Address: Address)]
			}));
			Dispatcher.UIThread.RunJobs();
			rig.Pads.Set(UsbPad);
			await service.StartBridgeAsync();
			for (int i = 0; i < 5; i++)
			{
				service.DeviceTick();
				await rig.Manager.RefreshAsync();
				rig.Time.Advance(TimeSpan.FromSeconds(2));
			}
			await Task.Delay(100);

			Assert.False(service.IsGameConnected);
			Assert.Empty(service.Controllers);
			Assert.DoesNotContain(HidingCalls(rig.Hiding), c => c.StartsWith("hide usb", StringComparison.Ordinal));
			lock (rig.UsbOpens)
			{
				Assert.Empty(rig.UsbOpens);
			}
			Dispatcher.UIThread.RunJobs();
			settings.KnownControllers.Observe(service.Controllers);
			Assert.Equal(CablePads.BtId, Assert.Single(settings.KnownControllers.Items).Id);
			lock (lines)
			{
				Assert.Single(lines, l => l.Contains("did not answer with its Bluetooth address (0x09)", StringComparison.Ordinal));
			}
			await service.StopBridgeAsync();
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= Watch;
		}
	}
}
