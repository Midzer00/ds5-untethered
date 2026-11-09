// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Kopru.Core.UsbIp;

namespace Kopru.Core.Tests;

internal sealed class FakeUsbIpDevice : IUsbIpDevice
{
	private readonly Dictionary<uint, Action<SubmitResult>> waiting = [];

	public UsbIpDeviceInfo Info { get; } = new("/kopru/virtual", "1-1", 1, 1, UsbIpCodes.SpeedFull, 0x054C, 0x0CE6, 0x0100, 0, 0, 0, 1, 1,
		[(1, 1, 0), (1, 2, 0), (1, 2, 0), (3, 0, 0)]);

	public List<SubmitCommand> Submitted { get; } = [];

	public bool WasReleased { get; private set; }

	public void Submit(SubmitCommand command, Action<SubmitResult> complete)
	{
		lock (Submitted)
		{
			Submitted.Add(command);
		}
		if (command.Endpoint == 0 && command.IsIn)
		{
			complete(SubmitResult.In([0x12, 0x01, 0x00, 0x02]));
		}
		else if (command.Endpoint == 0)
		{
			complete(SubmitResult.Ok(command.Length));
		}
		else
		{
			lock (waiting)
			{
				waiting[command.Sequence] = complete;
			}
		}
	}

	public void CompleteWaiting(uint sequence, byte[] data)
	{
		Action<SubmitResult> complete;
		lock (waiting)
		{
			complete = waiting[sequence];
			waiting.Remove(sequence);
		}
		complete(SubmitResult.In(data));
	}

	public bool Unlink(uint sequence)
	{
		lock (waiting)
		{
			return waiting.Remove(sequence);
		}
	}

	public void Released() => WasReleased = true;
}

public class UsbIpServerTests
{
	private static byte[] Submit(uint seq, uint direction, uint endpoint, int length, byte[]? setup = null, byte[]? data = null, int packets = -1)
	{
		byte[] h = new byte[48];
		UsbIpWire.W32(h, 0, UsbIpCodes.CmdSubmit);
		UsbIpWire.W32(h, 4, seq);
		UsbIpWire.W32(h, 8, 0x10001);
		UsbIpWire.W32(h, 12, direction);
		UsbIpWire.W32(h, 16, endpoint);
		UsbIpWire.W32(h, 24, (uint)length);
		UsbIpWire.W32(h, 32, (uint)packets);
		(setup ?? new byte[8]).CopyTo(h, 40);
		return [.. h, .. data ?? []];
	}

	private static async Task<NetworkStream> ImportAsync(UsbIpServer server, string busId = "1-1")
	{
		TcpClient client = new();
		await client.ConnectAsync("127.0.0.1", server.Port);
		NetworkStream stream = client.GetStream();
		byte[] busid = new byte[32];
		Encoding.ASCII.GetBytes(busId, busid);
		await stream.WriteAsync((byte[])[.. UsbIpWire.OpHeader(UsbIpCodes.OpReqImport, 0), .. busid]);
		return stream;
	}

	[Fact]
	public async Task DeviceList_DescribesTheDevice()
	{
		FakeUsbIpDevice device = new();
		await using UsbIpServer server = new(device);
		server.Start();
		using TcpClient client = new();
		await client.ConnectAsync("127.0.0.1", server.Port);
		NetworkStream stream = client.GetStream();

		await stream.WriteAsync(UsbIpWire.OpHeader(UsbIpCodes.OpReqDevlist, 0));
		byte[] reply = await UsbIpWire.ReadExactlyAsync(stream, 8 + 4 + 312 + 16, default);

		Assert.Equal(UsbIpCodes.OpRepDevlist, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2)));
		Assert.Equal(1u, UsbIpWire.U32(reply, 8));
		Assert.Equal("1-1", Encoding.ASCII.GetString(reply, 12 + 256, 32).TrimEnd('\0'));
		Assert.Equal((0x054C, 0x0CE6), (BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(12 + 300)), BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(12 + 302))));
		Assert.Equal(4, reply[12 + 311]);
		Assert.Equal(3, reply[12 + 312 + 12]);
	}

	[Fact]
	public async Task Import_ThenControlInAndOut_AreAnswered()
	{
		FakeUsbIpDevice device = new();
		await using UsbIpServer server = new(device);
		server.Start();
		NetworkStream stream = await ImportAsync(server);
		byte[] importReply = await UsbIpWire.ReadExactlyAsync(stream, 8 + 312, default);
		Assert.Equal(0u, UsbIpWire.U32(importReply, 4));

		await stream.WriteAsync(Submit(7, UsbIpCodes.DirIn, 0, 18, setup: [0x80, 6, 0, 1, 0, 0, 18, 0]));
		byte[] ret = await UsbIpWire.ReadExactlyAsync(stream, 48 + 4, default);
		Assert.Equal((UsbIpCodes.RetSubmit, 7u, 0u, 4u), (UsbIpWire.U32(ret, 0), UsbIpWire.U32(ret, 4), UsbIpWire.U32(ret, 20), UsbIpWire.U32(ret, 24)));
		Assert.Equal(new byte[] { 0x12, 0x01, 0x00, 0x02 }, ret[48..]);

		await stream.WriteAsync(Submit(8, UsbIpCodes.DirOut, 0, 3, setup: [0x21, 9, 0, 2, 3, 0, 3, 0], data: [1, 2, 3]));
		byte[] ret2 = await UsbIpWire.ReadExactlyAsync(stream, 48, default);
		Assert.Equal((8u, 3u), (UsbIpWire.U32(ret2, 4), UsbIpWire.U32(ret2, 24)));
		Assert.Equal(new byte[] { 1, 2, 3 }, device.Submitted.Last().Data);
	}

	[Fact]
	public async Task PendingInterruptIn_CompletesLater_OrIsUnlinked()
	{
		FakeUsbIpDevice device = new();
		await using UsbIpServer server = new(device);
		server.Start();
		NetworkStream stream = await ImportAsync(server);
		await UsbIpWire.ReadExactlyAsync(stream, 8 + 312, default);

		await stream.WriteAsync(Submit(20, UsbIpCodes.DirIn, 4, 64));
		await stream.WriteAsync(Submit(21, UsbIpCodes.DirIn, 4, 64));
		Assert.True(SpinWait.SpinUntil(() => device.Submitted.Count == 2, 2000));
		device.CompleteWaiting(20, [0x01, 0x80]);
		byte[] ret = await UsbIpWire.ReadExactlyAsync(stream, 48 + 2, default);
		Assert.Equal((20u, 2u), (UsbIpWire.U32(ret, 4), UsbIpWire.U32(ret, 24)));

		byte[] unlink = new byte[48];
		UsbIpWire.W32(unlink, 0, UsbIpCodes.CmdUnlink);
		UsbIpWire.W32(unlink, 4, 22);
		UsbIpWire.W32(unlink, 20, 21);
		await stream.WriteAsync(unlink);
		byte[] unlinked = await UsbIpWire.ReadExactlyAsync(stream, 48, default);
		Assert.Equal((UsbIpCodes.RetUnlink, 22u, unchecked((uint)UsbIpCodes.ConnectionReset)),
			(UsbIpWire.U32(unlinked, 0), UsbIpWire.U32(unlinked, 4), UsbIpWire.U32(unlinked, 20)));
	}

	[Fact]
	public async Task WrongBusId_IsRefused_AndClosingReleasesTheDevice()
	{
		FakeUsbIpDevice device = new();
		await using UsbIpServer server = new(device);
		server.Start();

		NetworkStream wrong = await ImportAsync(server, "9-9");
		byte[] refused = await UsbIpWire.ReadExactlyAsync(wrong, 8, default);
		Assert.Equal(1u, UsbIpWire.U32(refused, 4));

		NetworkStream right = await ImportAsync(server);
		await UsbIpWire.ReadExactlyAsync(right, 8 + 312, default);
		right.Close();
		Assert.True(SpinWait.SpinUntil(() => device.WasReleased, 2000));
	}

	[Fact]
	public async Task IsochronousOut_IsParsedWithItsPackets()
	{
		byte[] data = new byte[20];
		byte[] iso = new byte[2 * 16];
		UsbIpWire.W32(iso, 4, 10);
		UsbIpWire.W32(iso, 16, 10);
		UsbIpWire.W32(iso, 20, 10);
		byte[] message = [.. Submit(5, UsbIpCodes.DirOut, 1, 20, data: data, packets: 2), .. iso];

		SubmitCommand command = (SubmitCommand)await UsbIpWire.ReadCommandAsync(new MemoryStream(message), default);

		Assert.True(command.IsIsochronous);
		Assert.Equal([new IsoPacket(0, 10, 0, 0), new IsoPacket(10, 10, 0, 0)], command.Packets);
	}
}
