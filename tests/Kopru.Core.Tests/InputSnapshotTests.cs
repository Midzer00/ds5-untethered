// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using Kopru.Core.Bridge;
using Kopru.Core.Input;
using Kopru.Core.Protocol;

namespace Kopru.Core.Tests;

public class InputSnapshotTests
{
	private static byte[] Body()
	{
		byte[] body = new byte[InputState.BodySize];
		body[0] = body[1] = body[2] = body[3] = 128;
		body[InputSnapshot.Buttons0] = 0x08; // hat released
		body[InputSnapshot.TouchOffset] = 0x80; // both fingers up
		body[InputSnapshot.TouchOffset + 4] = 0x80;
		return body;
	}

	public static TheoryData<int, byte, PadButtons> Bits => new()
	{
		{ 7, 0x18, PadButtons.Square }, { 7, 0x28, PadButtons.Cross }, { 7, 0x48, PadButtons.Circle }, { 7, 0x88, PadButtons.Triangle },
		{ 8, 0x01, PadButtons.L1 }, { 8, 0x02, PadButtons.R1 }, { 8, 0x04, PadButtons.L2 }, { 8, 0x08, PadButtons.R2 },
		{ 8, 0x10, PadButtons.Create }, { 8, 0x20, PadButtons.Options }, { 8, 0x40, PadButtons.L3 }, { 8, 0x80, PadButtons.R3 },
		{ 9, 0x01, PadButtons.PS }, { 9, 0x02, PadButtons.Touchpad }, { 9, 0x04, PadButtons.Mute },
		{ 9, 0x10, PadButtons.LeftFunction }, { 9, 0x20, PadButtons.RightFunction },
		{ 9, 0x40, PadButtons.LeftPaddle }, { 9, 0x80, PadButtons.RightPaddle },
	};

	[Theory]
	[MemberData(nameof(Bits))]
	public void EachButtonBit_IsThatButtonOnly(int offset, byte value, PadButtons expected)
	{
		byte[] body = Body();
		body[offset] = value;
		Assert.Equal(expected, InputSnapshot.Parse(body).Buttons);
	}

	[Theory]
	[InlineData(0, PadButtons.DpadUp)]
	[InlineData(1, PadButtons.DpadUp | PadButtons.DpadRight)]
	[InlineData(2, PadButtons.DpadRight)]
	[InlineData(3, PadButtons.DpadDown | PadButtons.DpadRight)]
	[InlineData(4, PadButtons.DpadDown)]
	[InlineData(5, PadButtons.DpadDown | PadButtons.DpadLeft)]
	[InlineData(6, PadButtons.DpadLeft)]
	[InlineData(7, PadButtons.DpadUp | PadButtons.DpadLeft)]
	[InlineData(8, PadButtons.None)]
	[InlineData(15, PadButtons.None)]
	public void Hat_DecodesToDpad(byte hat, PadButtons expected)
	{
		byte[] body = Body();
		body[InputSnapshot.Buttons0] = hat;
		Assert.Equal(expected, InputSnapshot.Parse(body).Buttons);
	}

	[Fact]
	public void SticksTriggersAndBattery()
	{
		byte[] body = Body();
		body[0] = 10; body[1] = 20; body[2] = 30; body[3] = 240; body[4] = 77; body[5] = 255;
		body[BatteryStatus.StatusOffset] = 0x16;
		InputSnapshot s = InputSnapshot.Parse(body);
		Assert.Equal((10, 20, 30, 240, 77, 255), (s.LeftX, s.LeftY, s.RightX, s.RightY, s.L2, s.R2));
		Assert.Equal(new BatteryStatus(60, ChargeState.Charging), s.Battery);
		Assert.True(s.IsFull);
	}

	[Fact]
	public void TouchPoints_WithInactiveFlag()
	{
		byte[] body = Body();
		// Finger 1 down, id 5, X = 0x77F (1919), Y = 0x437 (1079): bytes 05 7F 77 43.
		body[32] = 0x05; body[33] = 0x7F; body[34] = 0x77; body[35] = 0x43;
		// Finger 2 up (bit 7), id 6, X = 0x123, Y = 0x456: bytes 86 23 61 45.
		body[36] = 0x86; body[37] = 0x23; body[38] = 0x61; body[39] = 0x45;
		InputSnapshot s = InputSnapshot.Parse(body);
		Assert.Equal(new TouchPoint(true, 5, 1919, 1079), s.Touch1);
		Assert.Equal(new TouchPoint(false, 6, 0x123, 0x456), s.Touch2);
	}

	[Fact]
	public void GyroAndAccel_AreSignedLittleEndian()
	{
		byte[] body = Body();
		short[] values = [-1, 300, short.MinValue, 8192, -8192, short.MaxValue];
		for (int i = 0; i < 6; i++)
		{
			BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(InputSnapshot.Gyro + 2 * i), values[i]);
		}
		InputSnapshot s = InputSnapshot.Parse(body);
		Assert.Equal(values, new[] { s.GyroX, s.GyroY, s.GyroZ, s.AccelX, s.AccelY, s.AccelZ });
	}

	[Fact]
	public void SimpleBluetoothReport()
	{
		byte[] report = [0x01, 1, 2, 3, 4, 0x26, 0x81, 0xF5, 90, 91];
		InputSnapshot s = InputSnapshot.FromReport(report, bluetooth: true)!;
		Assert.False(s.IsFull);
		Assert.Equal((1, 2, 3, 4, 90, 91), (s.LeftX, s.LeftY, s.RightX, s.RightY, s.L2, s.R2));
		// Hat 6 (left) + cross, L1 + R3, PS + mute (the counter in the high bits is ignored).
		Assert.Equal(PadButtons.DpadLeft | PadButtons.Cross | PadButtons.L1 | PadButtons.R3 | PadButtons.PS | PadButtons.Mute, s.Buttons);
		Assert.Null(s.Battery);
	}

	[Fact]
	public void FullReports_UsbAndBluetooth_BadCrcIgnored()
	{
		byte[] body = Body();
		body[0] = 200;
		byte[] usb = new byte[64];
		usb[0] = 0x01;
		body.CopyTo(usb, 1);
		Assert.Equal(200, InputSnapshot.FromReport(usb, bluetooth: false)!.LeftX);

		byte[] bt = new byte[78];
		bt[0] = 0x31;
		body.CopyTo(bt, 2);
		BinaryPrimitives.WriteUInt32LittleEndian(bt.AsSpan(74), Crc.Compute(Crc.InputSeed, bt.AsSpan(0, 74)));
		Assert.Equal(200, InputSnapshot.FromReport(bt, bluetooth: true)!.LeftX);
		bt[5] ^= 0xFF;
		Assert.Null(InputSnapshot.FromReport(bt, bluetooth: true));
		Assert.Null(InputSnapshot.FromReport([0x05, 1, 2], bluetooth: true));
	}

	[Fact]
	public void TransformBody_KeepsSticksTriggersAndTriggerBits()
	{
		byte[] body = Body();
		body[0] = 9; body[4] = 50; body[8] = 0x0C;
		byte[] t = InputSnapshot.Parse(body).ToTransformBody();
		Assert.Equal(9, t[InputTransform.LeftX]);
		Assert.Equal(50, t[InputTransform.L2Analog]);
		Assert.Equal(0x0C, t[InputTransform.Buttons1]);
	}
}

// The read-only live stream for the test page (BridgeHost.StreamInputAsync).
public class LiveInputStreamTests
{
	private readonly ConcurrentQueue<string> log = new();
	private readonly QueueControllers controllers = new();
	private readonly ProbeSource source = new();

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 5000), "timed out");

	private BridgeHost Host() =>
		new(controllers, new FakeIdentity(Kopru.Core.Virtual.DeviceIdentity.Create(
			BridgeTests.Device, BridgeTests.Configuration,
			[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
			new Dictionary<int, string> { [1] = "S", [2] = "D" },
			new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] })), new FakeAttacher(log))
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
			BatteryProbe = new BatteryProbe(source)
		};

	[Fact]
	public async Task Streams_SimpleThenFull_ReadsFeatureOnce_NeverWrites_ReleasesOnCancel()
	{
		await using BridgeHost host = Host();
		ProbeLink link = new() { Status = 0x05 };
		link.FeedSimple();
		link.FeedSimple();
		source.Links.Enqueue(link);
		ConcurrentQueue<InputSnapshot> seen = new();
		using CancellationTokenSource cancel = new();

		Task<bool> stream = host.StreamInputAsync(seen.Enqueue, cancel.Token);
		WaitUntil(() => seen.Any(s => s.IsFull));
		cancel.Cancel();

		Assert.True(await stream);
		Assert.True(link.Disposed);
		Assert.Empty(link.Writes);
		Assert.Equal([(byte)0x05], link.Features.ToArray());
		Assert.Equal(new BatteryStatus(50, ChargeState.Discharging), seen.Last(s => s.IsFull).Battery);
	}

	[Fact]
	public async Task BridgeStart_EndsTheStream()
	{
		await using BridgeHost host = Host();
		ProbeLink link = new();
		link.FeedFull();
		source.Links.Enqueue(link);
		int count = 0;
		Task<bool> stream = host.StreamInputAsync(_ => Interlocked.Increment(ref count), CancellationToken.None);
		WaitUntil(() => Volatile.Read(ref count) > 0);

		await host.StartAsync();

		Assert.True(stream.IsCompleted, "StartAsync returned before the stream released the controller");
		Assert.True(link.Disposed);
		Assert.Empty(link.Writes);
		WaitUntil(() => host.State != BridgeState.Stopped);
		Assert.False(await host.StreamInputAsync(_ => { }, CancellationToken.None));
		await host.StopAsync();
	}

	[Fact]
	public async Task NoProbeSourceOrNoPad_ReturnsFalse()
	{
		await using BridgeHost host = Host();
		Assert.False(await host.StreamInputAsync(_ => { }, CancellationToken.None));
		host.BatteryProbe = null;
		Assert.False(await host.StreamInputAsync(_ => { }, CancellationToken.None));
	}
}
