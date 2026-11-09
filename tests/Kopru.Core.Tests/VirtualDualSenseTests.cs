// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

public class VirtualDualSenseTests
{
	private static readonly byte[] Device = [18, 1, 0x00, 0x02, 0, 0, 0, 64, 0x4C, 0x05, 0xE6, 0x0C, 0x00, 0x01, 1, 2, 0, 1];

	// Config: audio control (if 0), speaker stream alt 1 with iso OUT 0x01 (interval 4), HID (if 3) with int IN 0x84 /
	// OUT 0x03 (interval 6).
	private static readonly byte[] Configuration =
	[
		9, 2, 59, 0, 3, 1, 0, 0xC0, 0xFA,
		9, 4, 0, 0, 0, 1, 1, 0, 0,
		9, 4, 1, 1, 1, 1, 2, 0, 0,
		7, 5, 0x01, 9, 0x88, 0x01, 4,
		9, 4, 3, 0, 2, 3, 0, 0, 0,
		9, 0x21, 0x11, 0x01, 0, 1, 0x22, 0x08, 0x00,
		7, 5, 0x84, 3, 64, 0, 6,
	];

	private static VirtualDualSense Create() => new(DeviceIdentity.Create(Device, Configuration, [0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
		new Dictionary<int, string> { [1] = "Sony Interactive Entertainment", [2] = "DualSense Wireless Controller" },
		new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3], [0x09] = [0x09, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 7] }));

	private static SubmitCommand Command(uint seq, uint direction, uint endpoint, byte[]? setup = null, byte[]? data = null, IsoPacket[]? packets = null, int startFrame = 0) =>
		new(seq, 0, direction, endpoint, 0, data?.Length ?? 64, startFrame, packets?.Length ?? -1, 1, setup ?? new byte[8], data ?? [], packets ?? []);

	private static SubmitResult Run(VirtualDualSense pad, byte[] setup, byte[]? data = null)
	{
		SubmitResult? result = null;
		pad.Submit(Command(1, (uint)(setup[0] >> 7), 0, setup, data), r => result = r);
		return result!;
	}

	[Fact]
	public void Identity_FullSpeedIntervals_AndOwnAddress()
	{
		using VirtualDualSense pad = Create();
		byte[] config = Run(pad, [0x80, 6, 0, 2, 0, 0, 255, 0]).Data;
		Assert.Equal(59, config.Length);
		Assert.Equal(1, config[27 + 6]);
		Assert.Equal(1, config[52 + 6]);

		byte[] pairing = Run(pad, [0xA1, 1, 0x09, 3, 3, 0, 64, 0]).Data;
		Assert.Equal(DeviceIdentity.VirtualAddress, pairing[1..7]);
		Assert.Equal((0x054C, 0x0CE6, 4), (pad.Info.VendorId, pad.Info.ProductId, pad.Info.Interfaces.Count + 1));
	}

	[Fact]
	public void Descriptors_DeviceStringsAndHid()
	{
		using VirtualDualSense pad = Create();
		Assert.Equal(Device[..8], Run(pad, [0x80, 6, 0, 1, 0, 0, 8, 0]).Data);
		Assert.Equal(new byte[] { 4, 3, 0x09, 0x04 }, Run(pad, [0x80, 6, 0, 3, 0, 0, 255, 0]).Data);
		byte[] product = Run(pad, [0x80, 6, 2, 3, 0x09, 0x04, 255, 0]).Data;
		Assert.Equal("DualSense Wireless Controller", System.Text.Encoding.Unicode.GetString(product, 2, product.Length - 2));
		Assert.Equal(8, Run(pad, [0x81, 6, 0, 0x22, 3, 0, 255, 0]).Data.Length);
		Assert.Equal(UsbIpCodes.BrokenPipe, Run(pad, [0x80, 6, 0, 7, 0, 0, 8, 0]).Status);
	}

	[Fact]
	public void Hid_FeatureAndOutput()
	{
		using VirtualDualSense pad = Create();
		List<byte[]> outputs = [];
		pad.OutputReceived += outputs.Add;

		Assert.Equal(new byte[] { 0x05, 1, 2, 3 }, Run(pad, [0xA1, 1, 0x05, 3, 3, 0, 64, 0]).Data);
		Assert.Equal(UsbIpCodes.BrokenPipe, Run(pad, [0xA1, 1, 0x77, 3, 3, 0, 64, 0]).Status);
		Run(pad, [0x21, 9, 0x02, 2, 3, 0, 3, 0], [0x02, 0x0C, 0x00]);
		pad.Submit(Command(5, UsbIpCodes.DirOut, VirtualDualSense.HidOutEndpoint, data: [0x02, 0x04]), _ => { });

		Assert.Equal([[0x02, 0x0C, 0x00], [0x02, 0x04]], outputs);
	}

	[Fact]
	public void InterruptIn_WaitsForInput_AndCanBeUnlinked()
	{
		using VirtualDualSense pad = Create();
		List<byte[]> answered = [];
		pad.Submit(Command(10, UsbIpCodes.DirIn, VirtualDualSense.HidInEndpoint), r => answered.Add(r.Data));
		pad.Submit(Command(11, UsbIpCodes.DirIn, VirtualDualSense.HidInEndpoint), r => answered.Add(r.Data));
		Assert.Empty(answered);

		byte[] report = new byte[64];
		report[0] = 0x01;
		report[5] = 200;
		pad.PushInput(report);

		Assert.Single(answered);
		Assert.Equal(200, answered[0][5]);
		Assert.True(pad.Unlink(11));
		Assert.False(pad.Unlink(11));
	}

	[Fact]
	public void Audio_ArrivesAtOnce_AndCompletesOnTheFrameClock()
	{
		using VirtualDualSense pad = Create();
		int audioBytes = 0;
		pad.AudioReceived += a => audioBytes += a.Length;
		IsoPacket[] tenMs = Enumerable.Range(0, 10).Select(i => new IsoPacket(i * 384, 384, 0, 0)).ToArray();
		List<(int Start, long AtMs)> done = [];
		System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

		pad.Submit(Command(1, UsbIpCodes.DirOut, VirtualDualSense.AudioOutEndpoint, data: new byte[3840], packets: tenMs, startFrame: 100),
			r => { lock (done) { done.Add((r.StartFrame, clock.ElapsedMilliseconds)); } });
		pad.Submit(Command(2, UsbIpCodes.DirOut, VirtualDualSense.AudioOutEndpoint, data: new byte[3840], packets: tenMs, startFrame: 110),
			r => { lock (done) { done.Add((r.StartFrame, clock.ElapsedMilliseconds)); } });

		Assert.Equal(7680, audioBytes);
		Assert.True(SpinWait.SpinUntil(() => done.Count == 2, 2000));
		Assert.Equal(done[0].Start + 10, done[1].Start);
		Assert.InRange(done[1].AtMs, 15, 60);
	}

	[Fact]
	public void Microphone_IsSilence_PackedPerFrame()
	{
		using VirtualDualSense pad = Create();
		SubmitResult? result = null;
		IsoPacket[] packets = Enumerable.Range(0, 4).Select(i => new IsoPacket(i * 196, 196, 0, 0)).ToArray();

		pad.Submit(Command(3, UsbIpCodes.DirIn, VirtualDualSense.MicInEndpoint, packets: packets), r => result = r);

		Assert.True(SpinWait.SpinUntil(() => result != null, 2000));
		Assert.Equal(4 * 192, result!.ActualLength);
		Assert.All(result.Packets!, p => Assert.Equal(192, p.ActualLength));
		Assert.All(result.Data, b => Assert.Equal(0, b));
	}

	[Fact]
	public void AudioControls_RememberSetCur()
	{
		using VirtualDualSense pad = Create();
		Run(pad, [0x21, 1, 0, 2, 0, 2, 2, 0], [0x00, 0xF0]);
		Assert.Equal(new byte[] { 0x00, 0xF0 }, Run(pad, [0xA1, 0x81, 0, 2, 0, 2, 2, 0]).Data);
		Assert.Equal(new byte[] { 0x80, 0xBB, 0x00 }, Run(pad, [0xA2, 0x81, 0, 1, 0x01, 0, 3, 0]).Data);
	}

	[Fact]
	public void SetInterface_IsRemembered()
	{
		using VirtualDualSense pad = Create();
		Run(pad, [0x01, 11, 1, 0, 1, 0, 0, 0]);
		Assert.Equal(1, pad.AlternateSetting(1));
	}
}
