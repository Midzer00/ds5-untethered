// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;
using Xunit;

namespace Kopru.Core.Tests;

// The battery while wired: USB input report 0x01 (64 bytes: report id, then the 63-byte input body) carries the status
// byte at report byte 53 (body 52), Bluetooth 0x31 at report byte 54 (body 52). Over USB the controller was seen sending
// "charging, level 0" (0x10) for a whole session: that is charging with the level unknown, never "0 %".
[Collection("AppLog")]
public sealed class WiredBatteryTests
{
	private const ulong Address = 0xA0B1C2D3E4F5;
	private const string BtId = @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\wired-battery";

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	// A USB input report as the controller sends it: sticks centred, sequence, sensor timestamp, the status byte at
	// report byte 53 and the plug flags after it (USB data and power while on the cable).
	private static byte[] UsbInput(byte status, byte sequence = 1)
	{
		byte[] report = new byte[UsbReports.InputSize];
		report[0] = UsbReports.InputId;
		report[1] = report[2] = report[3] = report[4] = 0x80;
		report[1 + 6] = sequence;
		report[1 + 7] = 0x08;
		report[1 + 27] = 0x10;
		report[1 + 28] = 0x27;
		report[53] = status;
		report[54] = 0x18;
		return report;
	}

	private static BatteryStatus FromUsb(byte status) =>
		BatteryStatus.From(InputState.Parse(InputState.Body(UsbInput(status))), overUsb: true);

	[Fact]
	public void UsbReport_StatusIsReportByte53_TheSameBodyByteAsBluetooth()
	{
		byte[] usb = UsbInput(0x16);
		byte[] bt = RemovableLink.Input(0x16);
		Assert.Equal(0x16, InputState.Body(usb)[BatteryStatus.StatusOffset]);
		Assert.Equal(0x16, bt[54]);
		Assert.Equal(BatteryStatus.Parse(InputState.Body(bt)), BatteryStatus.Parse(InputState.Body(usb)));
	}

	[Theory]
	[InlineData(0x16, 60, ChargeState.Charging)]
	[InlineData(0x1A, 100, ChargeState.Charging)]
	[InlineData(0x2A, 100, ChargeState.Full)]
	[InlineData(0x20, 100, ChargeState.Full)]
	[InlineData(0x07, 70, ChargeState.Discharging)]
	[InlineData(0x00, 0, ChargeState.Discharging)]
	public void UsbReport_ChargingFullAndDischarging_GiveTheRealLevel(byte status, int percent, ChargeState state) =>
		Assert.Equal(new BatteryStatus(percent, state), FromUsb(status));

	[Fact]
	public void UsbReport_ChargingAtLevel0_IsChargingWithTheLevelUnknown()
	{
		BatteryStatus b = FromUsb(0x10);
		Assert.Equal(ChargeState.Charging, b.State);
		Assert.False(b.LevelKnown);
		Assert.Equal(0, b.Percent);
	}

	[Fact]
	public void BluetoothReport_ChargingAtLevel0_StaysA0PercentReading()
	{
		byte[] bt = RemovableLink.Input(0x10);
		Assert.Equal(new BatteryStatus(0, ChargeState.Charging), BatteryStatus.From(InputState.Parse(InputState.Body(bt)), overUsb: false));
	}

	[Theory]
	[InlineData(0x10)]
	[InlineData(0x16)]
	[InlineData(0x2A)]
	[InlineData(0x07)]
	public void StatusByte_RoundTrips(byte status) =>
		Assert.Equal(status, BatteryStatus.StatusByte(InputState.Parse(InputState.Body(UsbInput(status)))));

	[Fact]
	public void Session_MarksUsbInput()
	{
		FakeLink usb = new(Transport.Usb, UsbReports.OutputSize);
		using BluetoothSession session = new(usb);
		session.Start();
		usb.Feed(UsbInput(0x16));
		WaitUntil(() => session.InputReports == 1);
		Assert.True(session.LastInputOverUsb);
	}

	private sealed class NoBluetooth : IControllerSource
	{
		public IHidLink? TryOpen() => null;

		public IReadOnlyList<ControllerPresence> Enumerate() => [];

		public IHidLink? TryOpen(string id) => null;
	}

	[Fact]
	public async Task Takeover_OnTheCableAlone_ShowsChargingThenTheRealLevel_AndLogsTheStatusByte()
	{
		ConcurrentQueue<string> lines = new();
		void OnLine(string line) => lines.Enqueue(line);
		Diagnostics.AppLog.Written += OnLine;
		try
		{
			ConcurrentQueue<string> log = new();
			FakeLink usb = new(Transport.Usb, UsbReports.OutputSize) { FeatureAnswer = id => id == PairingInfo.ReportId ? Pairing() : null };
			CableControllerSource source = new(new NoBluetooth(), _ => usb);
			await using BridgeHost host = new(source, new FakeIdentity(Identity()), new FakeAttacher(log))
			{
				PollInterval = TimeSpan.FromMilliseconds(10),
				MonitorInterval = TimeSpan.FromMilliseconds(10),
				NoInputTimeout = Timeout.InfiniteTimeSpan,
				OpenWiredAudio = _ => new FakeWiredAudioSink(),
				OpenWiredMic = (_, _) => null,
				Power = new FakePower { Address = null }
			};
			int low = 0;
			host.LowBattery += _ => Interlocked.Increment(ref low);
			await host.StartAsync();
			source.Offer([new ControllerPresence(BtId, usb.Device)]);
			WaitUntil(() => host.State == BridgeState.Running);
			Assert.True(host.IsUsbOnly);
			// Nothing read yet after the takeover: no reading, not a stale or zero one.
			Assert.Null(host.Status.Battery);

			usb.Feed(UsbInput(0x10));
			WaitUntil(() => host.Status.Battery is { State: ChargeState.Charging, LevelKnown: false });
			WaitUntil(() => lines.Any(l => l.Contains("wired: battery status over USB 0x10 (Charging, level not reported", StringComparison.Ordinal)));

			usb.Feed(UsbInput(0x16, 2));
			WaitUntil(() => host.Status.Battery == new BatteryStatus(60, ChargeState.Charging));
			WaitUntil(() => lines.Any(l => l.Contains("wired: battery status over USB 0x16 (Charging, level 60 %)", StringComparison.Ordinal)));

			usb.Feed(UsbInput(0x2A, 3));
			WaitUntil(() => host.Status.Battery == new BatteryStatus(100, ChargeState.Full));

			usb.Feed(UsbInput(0x01, 4));
			WaitUntil(() => host.Status.Battery == new BatteryStatus(10, ChargeState.Discharging));
			WaitUntil(() => Volatile.Read(ref low) == 1);

			// The same byte again is not logged twice.
			usb.Feed(UsbInput(0x01, 5));
			Thread.Sleep(50);
			Assert.Single(lines, l => l.Contains("over USB 0x01", StringComparison.Ordinal));
			await host.StopAsync();
		}
		finally
		{
			Diagnostics.AppLog.Written -= OnLine;
		}
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
}
