// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Hid;
using Kopru.Core.Setup;

namespace Kopru.App.Tests;

// A Bluetooth link that answers the info reports with its own made-up address and blocks reads until closed.
internal sealed class CableBtLink(string name, ulong address) : IHidLink
{
	private readonly CancellationTokenSource closed = new();

	public HidDevice Device { get; } = new(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_" + name, 0x054C, 0x0CE6, 78, 547, 64);

	public int Writes;

	public int Read(Span<byte> buffer)
	{
		closed.Token.WaitHandle.WaitOne();
		throw new IOException("closed");
	}

	public void Write(ReadOnlySpan<byte> report) => Interlocked.Increment(ref Writes);

	public byte[] GetFeature(byte reportId)
	{
		if (reportId == 0x20)
		{
			return InfoHidLink.Firmware();
		}
		byte[] r = new byte[64];
		r[0] = reportId;
		if (reportId == 0x09)
		{
			for (int i = 0; i < 6; i++)
			{
				r[1 + i] = (byte)(address >> (8 * i));
			}
		}
		return r;
	}

	public void CancelRead() => closed.Cancel();
	public void Dispose() => closed.Cancel();
}

// Bluetooth controllers that come and go by name (removing one drops it from the listing and closes its open link),
// plus a settable list of USB pads.
internal sealed class CableMultiSource(Dictionary<string, ulong> addresses) : IControllerSource, IControllerFinder
{
	private readonly object gate = new();
	private readonly List<string> bluetooth = [];
	private readonly List<HidDevice> usb = [];

	public ConcurrentDictionary<string, CableBtLink> Open { get; } = new();

	public void AddBluetooth(string id)
	{
		lock (gate)
		{
			bluetooth.Add(id);
		}
	}

	public void RemoveBluetooth(string id)
	{
		lock (gate)
		{
			bluetooth.Remove(id);
		}
		if (Open.TryRemove(id, out CableBtLink? link))
		{
			link.CancelRead();
		}
	}

	public void SetUsb(params HidDevice[] pads)
	{
		lock (gate)
		{
			usb.Clear();
			usb.AddRange(pads);
		}
	}

	public IReadOnlyList<ControllerPresence> Enumerate()
	{
		lock (gate)
		{
			return [.. bluetooth.Select(id => new ControllerPresence(id, new CableBtLink(id, addresses[id]).Device))];
		}
	}

	public IReadOnlyList<HidDevice> Find() => [.. Enumerate().Select(p => p.Device), .. UsbPads()];

	public IHidLink? TryOpen() => null;

	public IHidLink? TryOpen(string id)
	{
		lock (gate)
		{
			if (!bluetooth.Contains(id) || Open.ContainsKey(id))
			{
				return null;
			}
		}
		CableBtLink link = new(id, addresses[id]);
		Open[id] = link;
		return link;
	}

	private List<HidDevice> UsbPads()
	{
		lock (gate)
		{
			return [.. usb];
		}
	}
}

// The cable switch with several bridged controllers: each bridged-and-cabled controller's own USB twin is hidden, and
// one losing Bluetooth while the other stays up unhides only its own twin (the bridge never stops while any Bluetooth
// DualSense is listed).
public sealed class CableMultiTests
{
	private const ulong AddressA = 0xA0B1C2D3E4F5;
	private const ulong AddressB = 0x665544332211;

	private readonly CableMultiSource controllers = new(new() { ["a"] = AddressA, ["b"] = AddressB });

	private static HidDevice Usb(string name) => new(@"\\?\hid#vid_054c&pid_0ce6&mi_03#" + name, 0x054C, 0x0CE6, 64, 48, 64);
	private static string UsbIdOf(string name) => @"HID\VID_054C&PID_0CE6&MI_03\" + name;

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private static List<string> Calls(TestHiding hiding)
	{
		lock (hiding.Calls)
		{
			return [.. hiding.Calls];
		}
	}

	private BridgeHost Host() => new(controllers, new TestIdentity(true), new TestAttacher())
	{
		PollInterval = TimeSpan.FromMilliseconds(10),
		MonitorInterval = TimeSpan.FromMilliseconds(10),
		NoInputTimeout = Timeout.InfiniteTimeSpan
	};

	private (RealControllerService Service, BridgeManager Manager) Service(TestHiding hiding, StepTime time)
	{
		BridgeHost primary = Host();
		BridgeManager manager = new(controllers, primary, _ => Host());
		GameModeController games = new(FakeControllerService.SampleCatalog, new FakeGameWatcher(null), manager);
		RealControllerService service = new(primary, hiding, controllers, a => a(),
			d => d.Transport == Transport.Usb ? UsbIdOf(d.Path[(d.Path.LastIndexOf('#') + 1)..]) : d.Path,
			timers: false, games: games, time: time);
		service.UseManager(manager, watch: false);
		return (service, manager);
	}

	[Fact]
	public async Task TwoBridgedControllers_BothCabled_EachTwinIsHidden_AndOnlyTheUnpluggedOneIsUnhidden()
	{
		TestHiding hiding = new();
		StepTime time = new();
		(RealControllerService service, BridgeManager manager) = Service(hiding, time);
		using RealControllerService _ = service;
		controllers.AddBluetooth("a");
		controllers.AddBluetooth("b");
		await service.StartBridgeAsync();
		WaitUntil(() => manager.Host(1).State == BridgeState.Running && manager.Host(2).State == BridgeState.Running
			&& manager.Host(1).Details?.Address == AddressA && manager.Host(2).Details?.Address == AddressB);

		HidDevice cableA = Usb("a-cable"), cableB = Usb("b-cable");
		Dictionary<string, ulong> answers = new() { [cableA.Path] = AddressA, [cableB.Path] = AddressB };
		service.OpenUsbReadOnly = d => new InfoHidLink(Transport.Usb, answers[d.Path]);
		controllers.SetUsb(cableA, cableB);
		service.DeviceTick();
		WaitUntil(() => Calls(hiding).Contains("hide usb " + UsbIdOf("a-cable")) &&
			Calls(hiding).Contains("hide usb " + UsbIdOf("b-cable")));

		controllers.SetUsb(cableB); // a's cable out, b's stays in
		service.DeviceTick();
		time.Advance(RealControllerService.CableResumeDelay);
		service.DeviceTick();
		WaitUntil(() => Calls(hiding).Contains("unhide usb " + UsbIdOf("a-cable")));
		Assert.DoesNotContain(Calls(hiding), c => c.StartsWith("unhide usb " + UsbIdOf("b-cable")));
		await service.StopBridgeAsync();
	}

	[Fact]
	public async Task OneOfTwoLosesBluetoothWhileCabled_ItsTwinIsUnhidden_TheOtherStaysBridged()
	{
		TestHiding hiding = new();
		StepTime time = new();
		(RealControllerService service, BridgeManager manager) = Service(hiding, time);
		using RealControllerService _ = service;
		controllers.AddBluetooth("a");
		controllers.AddBluetooth("b");
		await service.StartBridgeAsync();
		WaitUntil(() => manager.Host(1).State == BridgeState.Running && manager.Host(2).State == BridgeState.Running
			&& manager.Host(1).Details?.Address == AddressA && manager.Host(2).Details?.Address == AddressB);

		HidDevice cableA = Usb("a-cable");
		service.OpenUsbReadOnly = d => new InfoHidLink(Transport.Usb, AddressA);
		controllers.SetUsb(cableA);
		service.DeviceTick();
		WaitUntil(() => Calls(hiding).Contains("hide usb " + UsbIdOf("a-cable")));

		controllers.RemoveBluetooth("a");
		service.DeviceTick();
		time.Advance(RealControllerService.CableResumeDelay);
		service.DeviceTick();
		WaitUntil(() => Calls(hiding).Contains("unhide usb " + UsbIdOf("a-cable")));
		Assert.True(service.IsBridgeRunning);
		Assert.False(service.IsPausedForCable);
		Assert.Equal(BridgeState.Running, manager.Host(2).State);
		Assert.DoesNotContain(Calls(hiding), c => c == "restore");
		await service.StopBridgeAsync();
	}
}
