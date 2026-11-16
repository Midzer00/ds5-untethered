// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Kopru.Core.Bridge;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// Input after a running bridge swaps the virtual pad type: the controller's reports must reach the NEW pad's interrupt IN
// endpoint in that pad's format, end to end (Bluetooth report -> ControllerBridge -> virtual pad -> USB/IP).
public class PadSwapInputTests
{
	// An attacher that remembers where each virtual pad's USB/IP server listens, so the test can act as the host.
	private sealed class ServerAttacher : IUsbIpAttacher
	{
		private int next;

		public ConcurrentQueue<(int TcpPort, string BusId)> Attached { get; } = new();

		public Task<int> AttachAsync(int tcpPort, string busId, CancellationToken cancel)
		{
			Attached.Enqueue((tcpPort, busId));
			return Task.FromResult(Interlocked.Increment(ref next));
		}

		public Task DetachAsync(int port) => Task.CompletedTask;
	}

	private static byte[] Submit(uint seq, uint endpoint, int length)
	{
		byte[] h = new byte[48];
		UsbIpWire.W32(h, 0, UsbIpCodes.CmdSubmit);
		UsbIpWire.W32(h, 4, seq);
		UsbIpWire.W32(h, 8, 0x10001);
		UsbIpWire.W32(h, 12, UsbIpCodes.DirIn);
		UsbIpWire.W32(h, 16, endpoint);
		UsbIpWire.W32(h, 24, (uint)length);
		UsbIpWire.W32(h, 32, unchecked((uint)-1));
		return h;
	}

	private static async Task<NetworkStream> ImportAsync(TcpClient client, (int TcpPort, string BusId) server)
	{
		await client.ConnectAsync("127.0.0.1", server.TcpPort);
		NetworkStream s = client.GetStream();
		byte[] busid = new byte[32];
		Encoding.ASCII.GetBytes(server.BusId, busid);
		await s.WriteAsync((byte[])[.. UsbIpWire.OpHeader(UsbIpCodes.OpReqImport, 0), .. busid]);
		Assert.Equal(0u, UsbIpWire.U32(await UsbIpWire.ReadExactlyAsync(s, 8 + 312, default), 4));
		return s;
	}

	// Feeds controller reports with the given buttons byte (DualSense byte 8) and reads interrupt IN transfers until one
	// carries them (reports fed for an earlier step may still be on their way), then returns that report.
	private static async Task<byte[]> ReadInputAsync(NetworkStream s, RemovableLink link, uint seq, uint endpoint, int length, byte buttons,
		Func<byte[], bool> carries)
	{
		using CancellationTokenSource timeout = new(5000);
		while (true)
		{
			await s.WriteAsync(Submit(seq, endpoint, length));
			Task<byte[]> read = UsbIpWire.ReadExactlyAsync(s, 48 + length, timeout.Token);
			while (!read.IsCompleted)
			{
				link.Feed(RemovableLink.Input(0x05, buttons: buttons));
				await Task.WhenAny(read, Task.Delay(10));
			}
			byte[] reply = await read;
			Assert.Equal(seq, UsbIpWire.U32(reply, 4));
			Assert.Equal(0u, UsbIpWire.U32(reply, 20));
			byte[] report = reply[48..];
			if (carries(report))
			{
				return report;
			}
			seq++;
		}
	}

	private static async Task<(BridgeHost Host, ServerAttacher Attacher, RemovableLink Link, ConcurrentQueue<VirtualPadType> Changed)> StartAsync()
	{
		ConcurrentQueue<string> log = new();
		QueueControllers controllers = new();
		ServerAttacher attacher = new();
		DeviceIdentity identity = DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
			[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
			new Dictionary<int, string> { [1] = "Sony Interactive Entertainment" }, new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] });
		BridgeHost host = new(controllers, new FakeIdentity(identity), attacher, new ManualTime())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan
		};
		ConcurrentQueue<VirtualPadType> changed = new();
		host.PadTypeChanged += changed.Enqueue;
		RemovableLink link = new(log, "a");
		controllers.Links.Enqueue(link);
		await host.StartAsync();
		Assert.True(SpinWait.SpinUntil(() => host.State == BridgeState.Running, 5000));
		return (host, attacher, link, changed);
	}

	[Fact]
	public async Task AfterASwapToDualShock4_ControllerInputReachesTheGameAsDs4_AndTheSwapBackResumesDualSenseInput()
	{
		(BridgeHost host, ServerAttacher attacher, RemovableLink link, ConcurrentQueue<VirtualPadType> changed) = await StartAsync();
		await using (host)
		{
			Assert.Single(attacher.Attached);

			host.UsePad(VirtualPadType.DualShock4);
			Assert.True(SpinWait.SpinUntil(() => changed.Count == 1, 5000));
			Assert.Equal(2, attacher.Attached.Count);
			using (TcpClient client = new())
			{
				NetworkStream s = await ImportAsync(client, attacher.Attached.ElementAt(1));
				// Cross held, D-pad released.
				byte[] first = await ReadInputAsync(s, link, 1, VirtualDualShock4.InEndpoint, DualShock4Reports.InputSize, 0x28,
					r => r[DualShock4Reports.Buttons0] == 0x28);
				Assert.Equal(DualShock4Reports.InputSize, first.Length);
				Assert.Equal(DualShock4Reports.InputId, first[0]);
				Assert.Equal([0x80, 0x80, 0x80, 0x80], first[1..5]);
				Assert.Equal(0x28, first[DualShock4Reports.Buttons0]);
				Assert.Equal(0x05, first[DualShock4Reports.Battery]);
				// The next report follows the controller: Cross released.
				byte[] second = await ReadInputAsync(s, link, 1000, VirtualDualShock4.InEndpoint, DualShock4Reports.InputSize, 0x08,
					r => r[DualShock4Reports.Buttons0] == 0x08);
				Assert.Equal(0x08, second[DualShock4Reports.Buttons0]);
			}

			host.UsePad(VirtualPadType.DualSense);
			Assert.True(SpinWait.SpinUntil(() => changed.Count == 2, 5000));
			Assert.Equal(3, attacher.Attached.Count);
			using (TcpClient client = new())
			{
				NetworkStream s = await ImportAsync(client, attacher.Attached.ElementAt(2));
				byte[] report = await ReadInputAsync(s, link, 1, VirtualDualSense.HidInEndpoint, UsbReports.InputSize, 0x48, r => r[8] == 0x48);
				Assert.Equal(UsbReports.InputSize, report.Length);
				Assert.Equal(UsbReports.InputId, report[0]);
				Assert.Equal(0x48, report[8]);
			}
			Assert.Equal([VirtualPadType.DualShock4, VirtualPadType.DualSense], changed);
		}
	}

	[Fact]
	public async Task AfterASwapToXbox360_ControllerInputReachesTheGameAsXbox()
	{
		(BridgeHost host, ServerAttacher attacher, RemovableLink link, ConcurrentQueue<VirtualPadType> changed) = await StartAsync();
		await using (host)
		{
			host.UsePad(VirtualPadType.Xbox360);
			Assert.True(SpinWait.SpinUntil(() => changed.Count == 1, 5000));
			using TcpClient client = new();
			NetworkStream s = await ImportAsync(client, attacher.Attached.ElementAt(1));
			byte[] report = await ReadInputAsync(s, link, 1, VirtualXbox360.InEndpoint, 20, 0x28, r => r[3] == XboxReports.A);
			Assert.Equal((0x00, 0x14, XboxReports.A), (report[0], report[1], report[3]));
		}
	}

	// A real pad reports a change at the next poll even if no poll was waiting when it happened; a release that came
	// between two IN transfers must not be lost (the button would stay held in the game).
	[Fact]
	public void Xbox360_AChangeWhileNoInTransferWaits_GoesOutWithTheNextOne()
	{
		using VirtualXbox360 pad = new();
		List<byte[]> got = [];
		SubmitCommand In(uint seq) => new(seq, 0, UsbIpCodes.DirIn, VirtualXbox360.InEndpoint, 0, 32, 0, -1, 1, new byte[8], [], []);
		byte[] Ds(byte buttons)
		{
			byte[] r = new byte[UsbReports.InputSize];
			r[0] = UsbReports.InputId;
			r[1] = r[2] = r[3] = r[4] = 0x80;
			r[8] = buttons;
			return r;
		}

		pad.Submit(In(1), r => got.Add(r.Data));
		pad.PushInput(Ds(0x28));
		Assert.Single(got);
		Assert.Equal(XboxReports.A, got[0][3]);

		// Released while the host had no IN transfer waiting, then nothing changes any more.
		pad.PushInput(Ds(0x08));
		pad.PushInput(Ds(0x08));
		pad.Submit(In(2), r => got.Add(r.Data));
		Assert.Equal(2, got.Count);
		Assert.Equal(0, got[1][3]);

		// Nothing new: the next IN waits for a change, like a real pad.
		pad.Submit(In(3), r => got.Add(r.Data));
		Assert.Equal(2, got.Count);
		pad.PushInput(Ds(0x88));
		Assert.Equal(3, got.Count);
		Assert.Equal(XboxReports.Y, got[2][3]);
	}
}
