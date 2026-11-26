// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;
using Xunit;

namespace Kopru.Core.Tests;

// Wired mode taking over a controller that is on the cable alone (never on Bluetooth in this session): the session
// runs on its USB link from the start, the cable source offers it under its Bluetooth id, the cable switch hides it
// instead of standing aside.
public sealed class CableOnlyTests
{
	private const ulong Address = 0xA0B1C2D3E4F5;
	private const string BtId = @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\cable-only";

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private static byte[] UsbInput(byte l2)
	{
		byte[] report = new byte[UsbReports.InputSize];
		report[0] = UsbReports.InputId;
		report[1 + 4] = l2;
		report[1 + 52] = 0x08;
		return report;
	}

	[Fact]
	public void Session_OnAUsbLink_RunsWiredFromTheStart_AndAFailedLinkIsTheControllerLost()
	{
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		using BluetoothSession session = new(usb);
		List<byte[]> bodies = [];
		session.InputBodyReceived += b =>
		{
			lock (bodies)
			{
				bodies.Add(b.ToArray());
			}
		};
		int lost = 0;
		session.Lost += () => Interlocked.Increment(ref lost);
		Assert.True(session.IsUsbOnly);
		Assert.True(session.IsWired);
		Assert.True(session.IsBluetoothGone);

		session.Start();
		// Start's light set-up went out as USB 0x02; no Bluetooth report was ever written.
		Assert.All(usb.Writes, w => Assert.Equal(UsbReports.OutputId, w[0]));
		Assert.NotEmpty(usb.Writes);
		usb.Feed(UsbInput(77));
		WaitUntil(() => { lock (bodies) return bodies.Count == 1; });
		Assert.Equal(77, bodies[0][4]);
		session.UpdateOutput(b => OutputBlock.SetLightbar(b, 1, 2, 3));
		Assert.Equal(1, usb.Writes.Last()[1 + OutputBlock.Red]);

		// Haptics without a USB audio sink: nothing is written anywhere (there is no Bluetooth).
		long writes = usb.Writes.Count;
		session.Haptics.Push(60, 60);
		session.WakeHaptics();
		Thread.Sleep(100);
		Assert.Equal(writes, usb.Writes.Count);

		usb.FailReads = true;
		WaitUntil(() => Volatile.Read(ref lost) == 1);
		Assert.False(session.IsWired);
	}

	[Fact]
	public void Session_OnAUsbLink_PlaysHapticsOnTheSink()
	{
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		using BluetoothSession session = new(usb);
		session.Start();
		FakeWiredAudioSink sink = new();
		Assert.True(session.AttachWiredAudio(usb.Device, sink));
		session.Haptics.Push(60, -60);
		session.WakeHaptics();
		WaitUntil(() => { lock (sink.Writes) return sink.Writes.Count >= 1; });
		Assert.All(usb.Writes, w => Assert.Equal(UsbReports.OutputId, w[0]));
	}

	private sealed class BluetoothList(params ControllerPresence[] present) : IControllerSource
	{
		public IHidLink? TryOpen() => null;

		public IReadOnlyList<ControllerPresence> Enumerate() => present;

		public IHidLink? TryOpen(string id) => null;
	}

	[Fact]
	public void CableSource_AddsTheOfferedControllers_UnderTheirBluetoothId_AndOpensTheirUsbLink()
	{
		FakeLink usbLink = new(Transport.Usb, UsbReports.OutputSize);
		List<HidDevice> opened = [];
		HidDevice other = new("bt-other", 0x054C, 0x0CE6, 78, 547, 64);
		CableControllerSource source = new(new BluetoothList(new ControllerPresence("other", other)), d =>
		{
			opened.Add(d);
			return usbLink;
		});
		Assert.Null(source.TryOpen(BtId));
		Assert.Single(source.Enumerate());

		source.Offer([new ControllerPresence(BtId, usbLink.Device)]);
		Assert.Equal(["other", BtId], source.Enumerate().Select(p => p.Id));
		Assert.Same(usbLink, source.TryOpen(BtId));
		Assert.Same(usbLink, source.TryOpen());
		Assert.Equal([usbLink.Device, usbLink.Device], opened);

		// The same id over Bluetooth wins: listed once, from the inner source.
		CableControllerSource both = new(new BluetoothList(new ControllerPresence(BtId, other)), _ => usbLink);
		both.Offer([new ControllerPresence(BtId, usbLink.Device)]);
		Assert.Same(other, Assert.Single(both.Enumerate()).Device);

		source.Offer([]);
		Assert.Null(source.TryOpen(BtId));
	}

	[Fact]
	public async Task Host_OpensTheOfferedUsbLink_RunsOnItAlone_AndWaitsAgainWhenTheCableIsPulled()
	{
		ConcurrentQueue<string> log = new();
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize) { FeatureAnswer = id => id == PairingInfo.ReportId ? Pairing() : null };
		CableControllerSource source = new(new BluetoothList(), _ => usb);
		FakeWiredAudioSink sink = new();
		await using BridgeHost host = new(source, new FakeIdentity(Identity()), new FakeAttacher(log))
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
			OpenWiredAudio = _ => sink,
			OpenWiredMic = (_, _) => null,
			Power = new FakePower { Address = null }
		};
		await host.StartAsync();
		// The supervisor sets the state on the thread pool: wait for it (a fixed 50 ms failed under a loaded full run).
		WaitUntil(() => host.State == BridgeState.WaitingForController);
		Thread.Sleep(50);
		Assert.Equal(BridgeState.WaitingForController, host.State);

		source.Offer([new ControllerPresence(BtId, usb.Device)]);
		WaitUntil(() => host.State == BridgeState.Running);
		Assert.True(host.IsUsbOnly);
		Assert.True(host.IsWired);
		Assert.True(host.IsWiredAudio);
		Assert.Equal(Address, host.Details?.Address);
		Assert.Equal(Transport.Usb, host.Details?.Transport);
		Assert.Single(log, e => e.StartsWith("attach", StringComparison.Ordinal));
		Assert.All(usb.Writes, w => Assert.Equal(UsbReports.OutputId, w[0]));

		source.Offer([]);
		usb.FailReads = true;
		WaitUntil(() => host.State == BridgeState.WaitingForController);
		Assert.False(host.IsUsbOnly);
		WaitUntil(() => log.Any(e => e.StartsWith("detach", StringComparison.Ordinal)));
		await host.StopAsync();
	}

	private static DeviceIdentity Identity() => DeviceIdentity.Create(
		BridgeTests.Device, BridgeTests.Configuration,
		[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
		new Dictionary<int, string> { [1] = "Sony Interactive Entertainment", [2] = "DualSense Wireless Controller" },
		new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] });

	private static byte[] Pairing()
	{
		byte[] r = new byte[20];
		r[0] = PairingInfo.ReportId;
		for (int i = 0; i < 6; i++)
		{
			r[1 + i] = (byte)(Address >> (8 * i));
		}
		return r;
	}

	[Fact]
	public void CableSwitch_WiredMode_TakesOverACableOnlyController_ChargeOnlyLeavesItToGames()
	{
		TimeSpan delay = TimeSpan.FromSeconds(5);
		DateTimeOffset t = new(2026, 11, 24, 16, 0, 0, TimeSpan.Zero);
		CableSwitch wired = new(delay) { Mode = CableMode.Wired };
		Assert.Equal(CableDecision.None, wired.Next(true, [Address], [], [Address], false, t, [Address]));
		Assert.Equal([Address], wired.CableOnlyTwins);
		Assert.Equal([Address], wired.HiddenTwins);
		// Long after the delay: no stop, still wanted hidden; once the bridge holds it, it is wired like a twin.
		Assert.Equal(CableDecision.None, wired.Next(true, [Address], [], [Address], false, t.AddSeconds(30), [Address]));
		Assert.False(wired.PausedForCable);
		Assert.Equal(CableDecision.None, wired.Next(true, [Address], [Address], [Address], false, t.AddSeconds(32), [Address]));
		Assert.Empty(wired.CableOnlyTwins);
		Assert.Equal([Address], wired.WiredTwins);
		Assert.Equal([Address], wired.HiddenTwins);

		// Charge only: the same controller is left to games (the bridge stops for the cable after the delay).
		CableSwitch chargeOnly = new(delay);
		Assert.Equal(CableDecision.None, chargeOnly.Next(true, [Address], [], [Address], false, t, [Address]));
		Assert.Empty(chargeOnly.CableOnlyTwins);
		Assert.Empty(chargeOnly.HiddenTwins);
		Assert.Equal(CableDecision.StopForCable, chargeOnly.Next(true, [Address], [], [Address], false, t.AddSeconds(6), [Address]));

		// Stopped for the cable, then wired mode chosen: it resumes at once to take the controller over.
		chargeOnly.Mode = CableMode.Wired;
		Assert.Equal(CableDecision.Resume, chargeOnly.Next(false, [Address], [], [Address], false, t.AddSeconds(8), [Address]));
		Assert.False(chargeOnly.PausedForCable);
	}
}
