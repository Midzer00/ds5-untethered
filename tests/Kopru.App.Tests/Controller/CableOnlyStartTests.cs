// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Health;
using Kopru.Core.Hid;

namespace Kopru.App.Tests;

// The app started with a remembered controller on the cable alone (no Bluetooth): in cable mode Wired it counts as
// connected, so the automatic start runs the bridge and the bridge takes it over, also when its record was saved before
// the address was kept (the address then comes from Windows' record of its paired Bluetooth node). Charge Only and a
// controller no remembered one matches are left to games as before. Made-up address; no address in any log line.
[Collection("ControllerLook")]
public sealed class CableOnlyStartTests
{
	private const ulong Address = 0xA0B1C2D3E4F5;
	private const ulong OtherAddress = 0xA0B1C2D3E4F6;
	private const string OtherId = @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\cable-test-other";
	private static readonly DateTimeOffset Seen = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
	private static readonly HidDevice UsbPad = new(InfoHidLink.UsbPath, 0x054C, 0x0CE6, 64, 48, 64);

	private static BridgeHost Host(IControllerSource source) => new(source, new TestIdentity(true), new TestAttacher())
	{
		PollInterval = TimeSpan.FromMilliseconds(10),
		MonitorInterval = TimeSpan.FromMilliseconds(10),
		NoInputTimeout = Timeout.InfiniteTimeSpan,
		OpenWired = _ => null,
		OpenWiredAudio = _ => null,
		OpenWiredMic = (_, _) => null
	};

	private sealed record Rig(CablePads Pads, BridgeManager Manager, RealControllerService Service, TestHiding Hiding, StepTime Time);

	// usbAnswers: the 0x09 address the USB pad gives; windows: the address Windows has for a remembered id (or null).
	private static Rig Build(ulong usbAnswers, Func<string, ulong?> windows)
	{
		CablePads pads = new(Address);
		CableControllerSource source = new(pads, _ => new InfoHidLink(Transport.Usb, usbAnswers));
		StepTime time = new();
		BridgeHost primary = Host(source);
		BridgeManager manager = new(source, primary, _ => Host(source));
		GameModeController games = new(FakeControllerService.SampleCatalog, new FakeGameWatcher(null), manager);
		TestHiding hiding = new();
		RealControllerService service = new(primary, hiding, pads, a => a(), CablePads.InstanceId, timers: false, games: games, time: time)
		{
			OpenUsbReadOnly = _ => new InfoHidLink(Transport.Usb, usbAnswers),
			AddressOfId = windows
		};
		service.UseManager(manager, watch: false);
		return new Rig(pads, manager, service, hiding, time);
	}

	private static SettingsState Settings(RealControllerService service, CableMode mode, KnownController known) =>
		Settings(service, mode, out _, known);

	private static SettingsState Settings(RealControllerService service, CableMode mode, out MemorySettingsStore store, params KnownController[] known)
	{
		store = new MemorySettingsStore(new AppSettings { CableMode = mode, AutoStartBridge = true, KnownControllers = [.. known] });
		return new SettingsState(service, store);
	}

	// One presence tick with everything it sets off: the posted UI work, the health report, the manager's look.
	private static async Task Tick(Rig rig)
	{
		rig.Service.DeviceTick();
		Dispatcher.UIThread.RunJobs();
		await rig.Service.RefreshHealthAsync();
		Dispatcher.UIThread.RunJobs();
		await rig.Manager.RefreshAsync();
		await Task.Delay(20);
		Dispatcher.UIThread.RunJobs();
	}

	private static async Task TickUntil(Rig rig, Func<bool> condition)
	{
		for (int i = 0; i < 60 && !condition(); i++)
		{
			await Tick(rig);
		}
		Assert.True(condition(), "timed out");
	}

	private static Action<string> Into(List<string> lines) => line =>
	{
		lock (lines)
		{
			lines.Add(line);
		}
	};

	private static List<string> Copy(List<string> lines)
	{
		lock (lines)
		{
			return [.. lines];
		}
	}

	private static void NoAddressIn(List<string> lines)
	{
		foreach (string line in Copy(lines))
		{
			Assert.DoesNotContain("A0B1C2D3E4F5", line, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("A0:B1:C2", line, StringComparison.OrdinalIgnoreCase);
		}
	}

	[AvaloniaFact]
	public async Task Wired_OldRecordWithoutAddress_CableAlone_AddressFilledIn_ShownConnected_AutoStarts_AndTakesItOver()
	{
		List<string> lines = [];
		Action<string> watch = Into(lines);
		Kopru.Core.Diagnostics.AppLog.Written += watch;
		try
		{
			List<string> asked = [];
			Rig rig = Build(Address, id =>
			{
				lock (asked)
				{
					asked.Add(id);
				}
				return id == CablePads.BtId ? Address : null;
			});
			using RealControllerService service = rig.Service;
			rig.Pads.Set(UsbPad);
			// Remembered by an older build: no address.
			SettingsState settings = Settings(service, CableMode.Wired, out MemorySettingsStore store,
				new KnownController(CablePads.BtId, ControllerModel.DualSense, "Blue One", Seen, Seen));
			// Filled in while the settings load, before any presence tick can match against the record.
			Assert.Equal(Address, settings.KnownControllers.Find(CablePads.BtId)?.Address);
			Dispatcher.UIThread.RunJobs();
			// ...and saved, so the next start needs no lookup.
			for (int i = 0; i < 100 && store.Saved?.KnownControllers?.FirstOrDefault()?.Address == null; i++)
			{
				await Task.Delay(20);
			}
			Assert.Equal(Address, store.Saved?.KnownControllers?.FirstOrDefault()?.Address);

			// Stopped: the controller on the cable counts as connected, as wired, and nothing blocks the start.
			await TickUntil(rig, () => service.IsConnected && service.Connection.Kind == ConnectionKind.ConnectedWired);
			Assert.True(service.IsCableOnly);
			Assert.True(service.CanStartBridge);
			Assert.False(service.IsBridgeRunning);
			var overview = new OverviewViewModel(service, settings);
			Assert.False(overview.IsOnCable);
			Assert.True(overview.IsConnected);
			Assert.True(overview.CanToggleBridge);
			Assert.Equal(Kopru.Core.Localization.Text.ConnectionWired(), overview.ConnectionLabel);

			// The automatic start runs the bridge, which takes the controller over under its Bluetooth id.
			settings.AttachAutoStart();
			await TickUntil(rig, () => service.IsGameConnected && rig.Manager.Primary.IsUsbOnly);
			Assert.Equal(CablePads.BtId, Assert.Single(service.Controllers).Id);
			Assert.True(service.IsCableOnly);
			Assert.Equal(ConnectionKind.ConnectedWired, (await service.RefreshHealthAsync()).Connection.Kind);
			Dispatcher.UIThread.RunJobs();
			settings.KnownControllers.Observe(service.Controllers);
			KnownController known = Assert.Single(settings.KnownControllers.Items);
			Assert.Equal("Blue One", known.Name);
			Assert.Equal(Address, known.Address);
			lock (asked)
			{
				Assert.Equal([CablePads.BtId], asked);
			}

			List<string> seen = Copy(lines);
			Assert.Single(seen, l => l.StartsWith("controllers: Bluetooth address of a controller remembered without one found in Windows", StringComparison.Ordinal));
			Assert.Single(seen, l => l.StartsWith("auto start: wired mode, a remembered controller is on the cable alone", StringComparison.Ordinal));
			Assert.Single(seen, l => l.StartsWith("cable: wired mode, a remembered controller is on the cable alone", StringComparison.Ordinal));
			Assert.Contains(seen, l => l == "cable: wired mode, taking over the controller on the cable alone (no Bluetooth)");

			// Stopped by hand: the controller stays shown as connected throughout, and the automatic start does not undo
			// the stop.
			bool flickered = false;
			void Watch(object? s, EventArgs e) => flickered |= !service.IsConnected;
			service.StateChanged += Watch;
			await service.StopBridgeAsync();
			for (int i = 0; i < 5; i++)
			{
				await Tick(rig);
				rig.Time.Advance(TimeSpan.FromSeconds(2));
			}
			service.StateChanged -= Watch;
			Assert.False(service.IsBridgeRunning);
			Assert.True(service.IsConnected);
			Assert.False(flickered);
			Assert.Single(Copy(lines), l => l.StartsWith("auto start:", StringComparison.Ordinal));
			NoAddressIn(lines);
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= watch;
		}
	}

	// Two controllers remembered by an older build: each gets the address Windows has for its own id, and the one whose
	// address the cable gives is the one taken over (never the other, never by elimination).
	[AvaloniaFact]
	public async Task Wired_TwoOldRecords_EachGetsItsOwnAddress_TheMatchingOneIsTakenOver()
	{
		List<string> lines = [];
		Action<string> watch = Into(lines);
		Kopru.Core.Diagnostics.AppLog.Written += watch;
		try
		{
			Rig rig = Build(Address, id => id == CablePads.BtId ? Address : id == OtherId ? OtherAddress : null);
			using RealControllerService service = rig.Service;
			rig.Pads.Set(UsbPad);
			// The other one was seen last: a guess by recency would pick it.
			SettingsState settings = Settings(service, CableMode.Wired, out _,
				new KnownController(OtherId, ControllerModel.DualSense, "Other One", Seen, Seen.AddDays(1)),
				new KnownController(CablePads.BtId, ControllerModel.DualSense, "Blue One", Seen, Seen));
			Assert.Equal(OtherAddress, settings.KnownControllers.Find(OtherId)?.Address);
			Assert.Equal(Address, settings.KnownControllers.Find(CablePads.BtId)?.Address);

			settings.AttachAutoStart();
			await TickUntil(rig, () => service.IsGameConnected && rig.Manager.Primary.IsUsbOnly);
			Assert.Equal(CablePads.BtId, Assert.Single(service.Controllers).Id);
			Assert.Equal(2, Copy(lines).Count(l => l.StartsWith("controllers: Bluetooth address of a controller remembered without one found in Windows", StringComparison.Ordinal)));
			NoAddressIn(lines);
			Assert.DoesNotContain(Copy(lines), l => l.Contains("A0B1C2D3E4F6", StringComparison.OrdinalIgnoreCase));
			await service.StopBridgeAsync();
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= watch;
		}
	}

	// Two controllers remembered without an address and Windows has neither: the cable's controller is not given to
	// either of them (no guess), it is left to games and nothing starts.
	[AvaloniaFact]
	public async Task Wired_TwoOldRecordsWindowsKnowsNeither_NoGuess_LeftToGames()
	{
		Rig rig = Build(Address, _ => null);
		using RealControllerService service = rig.Service;
		rig.Pads.Set(UsbPad);
		SettingsState settings = Settings(service, CableMode.Wired, out _,
			new KnownController(OtherId, ControllerModel.DualSense, null, Seen, Seen),
			new KnownController(CablePads.BtId, ControllerModel.DualSense, null, Seen, Seen));
		settings.AttachAutoStart();
		for (int i = 0; i < 5; i++)
		{
			await Tick(rig);
			rig.Time.Advance(TimeSpan.FromSeconds(2));
		}

		Assert.False(service.IsBridgeRunning);
		Assert.False(service.IsCableOnly);
		Assert.Empty(service.Controllers);
		Assert.All(settings.KnownControllers.Items, k => Assert.Null(k.Address));
	}

	[AvaloniaFact]
	public async Task ChargeOnly_RememberedControllerOnTheCableAlone_IsLeftToGames_NoAutoStart()
	{
		List<string> lines = [];
		Action<string> watch = Into(lines);
		Kopru.Core.Diagnostics.AppLog.Written += watch;
		try
		{
			Rig rig = Build(Address, _ => null);
			using RealControllerService service = rig.Service;
			rig.Pads.Set(UsbPad);
			SettingsState settings = Settings(service, CableMode.ChargeOnly,
				new KnownController(CablePads.BtId, ControllerModel.DualSense, null, Seen, Seen, Address: Address));
			Dispatcher.UIThread.RunJobs();
			settings.AttachAutoStart();
			for (int i = 0; i < 5; i++)
			{
				await Tick(rig);
				rig.Time.Advance(TimeSpan.FromSeconds(2));
			}

			Assert.False(service.IsBridgeRunning);
			Assert.False(service.IsConnected);
			Assert.False(service.IsCableOnly);
			Assert.Equal(ConnectionKind.ConnectedUsb, service.Connection.Kind);
			Assert.False(service.CanStartBridge);
			Assert.DoesNotContain(Copy(lines), l => l.StartsWith("auto start:", StringComparison.Ordinal));
			lock (rig.Hiding.Calls)
			{
				Assert.DoesNotContain(rig.Hiding.Calls, c => c.StartsWith("hide usb", StringComparison.Ordinal));
			}
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= watch;
		}
	}

	[AvaloniaFact]
	public async Task Wired_AUsbControllerNoRememberedOneMatches_IsLeftToGames_NoAutoStart_NoNewEntry()
	{
		List<string> lines = [];
		Action<string> watch = Into(lines);
		Kopru.Core.Diagnostics.AppLog.Written += watch;
		try
		{
			// The remembered controller is another one (its record has no address and Windows has none for it).
			Rig rig = Build(Address, _ => null);
			using RealControllerService service = rig.Service;
			rig.Pads.Set(UsbPad);
			SettingsState settings = Settings(service, CableMode.Wired,
				new KnownController(CablePads.BtId, ControllerModel.DualSense, null, Seen, Seen));
			Dispatcher.UIThread.RunJobs();
			settings.AttachAutoStart();
			for (int i = 0; i < 5; i++)
			{
				await Tick(rig);
				rig.Time.Advance(TimeSpan.FromSeconds(2));
			}

			Assert.False(service.IsBridgeRunning);
			Assert.False(service.IsConnected);
			Assert.False(service.IsCableOnly);
			Assert.Equal(ConnectionKind.ConnectedUsb, service.Connection.Kind);
			Assert.Empty(service.Controllers);
			Dispatcher.UIThread.RunJobs();
			settings.KnownControllers.Observe(service.Controllers);
			KnownController only = Assert.Single(settings.KnownControllers.Items);
			Assert.Null(only.Address);
			List<string> seen = Copy(lines);
			Assert.DoesNotContain(seen, l => l.StartsWith("auto start:", StringComparison.Ordinal));
			Assert.Single(seen, l => l.StartsWith("controllers: no Bluetooth address in Windows for a controller remembered without one", StringComparison.Ordinal));
			Assert.Single(seen, l => l.Contains("never saw over Bluetooth is not taken over", StringComparison.Ordinal));
			NoAddressIn(lines);
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= watch;
		}
	}
}
