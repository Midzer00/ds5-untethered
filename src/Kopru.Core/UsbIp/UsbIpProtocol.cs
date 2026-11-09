// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Kopru.Core.UsbIp;

// The USB/IP wire format (protocol version 1.1.1), all fields big-endian: the operations a client sends before a
// device is attached (list, import), then URB commands (submit, unlink) and their replies.
public static class UsbIpCodes
{
	public const ushort Version = 0x0111;
	public const ushort OpReqDevlist = 0x8005;
	public const ushort OpRepDevlist = 0x0005;
	public const ushort OpReqImport = 0x8003;
	public const ushort OpRepImport = 0x0003;

	public const uint CmdSubmit = 1;
	public const uint CmdUnlink = 2;
	public const uint RetSubmit = 3;
	public const uint RetUnlink = 4;

	public const uint DirOut = 0;
	public const uint DirIn = 1;

	public const uint SpeedFull = 2;

	// Linux error numbers the protocol carries (negated).
	public const int ConnectionReset = -104;
	public const int BrokenPipe = -32;

	public const int DeviceSize = 312;
	public const int HeaderSize = 48;
	public const int IsoDescriptorSize = 16;
}

public sealed record UsbIpDeviceInfo(string Path, string BusId, uint BusNumber, uint DeviceNumber, uint Speed, ushort VendorId, ushort ProductId,
	ushort BcdDevice, byte DeviceClass, byte DeviceSubClass, byte DeviceProtocol, byte ConfigurationValue, byte ConfigurationCount,
	IReadOnlyList<(byte Class, byte SubClass, byte Protocol)> Interfaces)
{
	public byte[] Encode(bool withInterfaces)
	{
		byte[] buffer = new byte[UsbIpCodes.DeviceSize + (withInterfaces ? 4 * Interfaces.Count : 0)];
		Encoding.ASCII.GetBytes(Path, buffer.AsSpan(0, 256));
		Encoding.ASCII.GetBytes(BusId, buffer.AsSpan(256, 32));
		Span<byte> rest = buffer.AsSpan(288);
		BinaryPrimitives.WriteUInt32BigEndian(rest, BusNumber);
		BinaryPrimitives.WriteUInt32BigEndian(rest[4..], DeviceNumber);
		BinaryPrimitives.WriteUInt32BigEndian(rest[8..], Speed);
		BinaryPrimitives.WriteUInt16BigEndian(rest[12..], VendorId);
		BinaryPrimitives.WriteUInt16BigEndian(rest[14..], ProductId);
		BinaryPrimitives.WriteUInt16BigEndian(rest[16..], BcdDevice);
		rest[18] = DeviceClass;
		rest[19] = DeviceSubClass;
		rest[20] = DeviceProtocol;
		rest[21] = ConfigurationValue;
		rest[22] = ConfigurationCount;
		rest[23] = (byte)Interfaces.Count;
		if (withInterfaces)
		{
			for (int i = 0; i < Interfaces.Count; i++)
			{
				buffer[UsbIpCodes.DeviceSize + 4 * i] = Interfaces[i].Class;
				buffer[UsbIpCodes.DeviceSize + 4 * i + 1] = Interfaces[i].SubClass;
				buffer[UsbIpCodes.DeviceSize + 4 * i + 2] = Interfaces[i].Protocol;
			}
		}
		return buffer;
	}
}

public readonly record struct IsoPacket(int Offset, int Length, int ActualLength, int Status);

public sealed record SubmitCommand(uint Sequence, uint DeviceId, uint Direction, uint Endpoint, uint TransferFlags, int Length,
	int StartFrame, int PacketCount, int Interval, byte[] Setup, byte[] Data, IsoPacket[] Packets)
{
	public bool IsIn => Direction == UsbIpCodes.DirIn;

	public bool IsIsochronous => Packets.Length > 0;
}

public sealed record UnlinkCommand(uint Sequence, uint DeviceId, uint Direction, uint Endpoint, uint TargetSequence);

// What a device answers to a submit. Data is what goes back for an IN transfer (for isochronous IN, the packets' data
// back to back, ActualLength in total).
public sealed record SubmitResult(int Status, int ActualLength, byte[] Data, int StartFrame = 0, IsoPacket[]? Packets = null, int ErrorCount = 0)
{
	public static SubmitResult Ok(int actualLength = 0) => new(0, actualLength, []);

	public static SubmitResult In(byte[] data) => new(0, data.Length, data);

	public static SubmitResult Stall() => new(UsbIpCodes.BrokenPipe, 0, []);
}

public static class UsbIpWire
{
	public static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken cancel)
	{
		byte[] buffer = new byte[count];
		await stream.ReadExactlyAsync(buffer, cancel);
		return buffer;
	}

	// The 8-byte header of OP_REQ_* / OP_REP_* messages.
	public static byte[] OpHeader(ushort code, uint status)
	{
		byte[] header = new byte[8];
		BinaryPrimitives.WriteUInt16BigEndian(header, UsbIpCodes.Version);
		BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), code);
		BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), status);
		return header;
	}

	// Reads one URB command (after the import): a submit with its OUT data and iso descriptors, or an unlink.
	public static async Task<object> ReadCommandAsync(Stream stream, CancellationToken cancel)
	{
		byte[] h = await ReadExactlyAsync(stream, UsbIpCodes.HeaderSize, cancel);
		uint command = U32(h, 0);
		uint sequence = U32(h, 4);
		uint devId = U32(h, 8);
		uint direction = U32(h, 12);
		uint endpoint = U32(h, 16);
		if (command == UsbIpCodes.CmdUnlink)
		{
			return new UnlinkCommand(sequence, devId, direction, endpoint, U32(h, 20));
		}
		if (command != UsbIpCodes.CmdSubmit)
		{
			throw new InvalidDataException($"Unknown USB/IP command {command}");
		}
		int length = (int)U32(h, 24);
		int packetCount = (int)U32(h, 32);
		byte[] data = direction == UsbIpCodes.DirOut && length > 0 ? await ReadExactlyAsync(stream, length, cancel) : [];
		IsoPacket[] packets = [];
		if (packetCount > 0 && packetCount != -1)
		{
			byte[] iso = await ReadExactlyAsync(stream, packetCount * UsbIpCodes.IsoDescriptorSize, cancel);
			packets = new IsoPacket[packetCount];
			for (int i = 0; i < packetCount; i++)
			{
				int at = i * UsbIpCodes.IsoDescriptorSize;
				packets[i] = new IsoPacket((int)U32(iso, at), (int)U32(iso, at + 4), (int)U32(iso, at + 8), (int)U32(iso, at + 12));
			}
		}
		return new SubmitCommand(sequence, devId, direction, endpoint, U32(h, 20), length, (int)U32(h, 28), packetCount, (int)U32(h, 36),
			h[40..48], data, packets);
	}

	public static byte[] RetSubmit(SubmitCommand command, SubmitResult result)
	{
		IsoPacket[] packets = result.Packets ?? [];
		byte[] data = command.IsIn ? result.Data : [];
		byte[] reply = new byte[UsbIpCodes.HeaderSize + data.Length + packets.Length * UsbIpCodes.IsoDescriptorSize];
		W32(reply, 0, UsbIpCodes.RetSubmit);
		W32(reply, 4, command.Sequence);
		W32(reply, 20, (uint)result.Status);
		W32(reply, 24, (uint)result.ActualLength);
		W32(reply, 28, (uint)result.StartFrame);
		W32(reply, 32, (uint)(command.IsIsochronous ? packets.Length : -1));
		W32(reply, 36, (uint)result.ErrorCount);
		data.CopyTo(reply, UsbIpCodes.HeaderSize);
		for (int i = 0; i < packets.Length; i++)
		{
			int at = UsbIpCodes.HeaderSize + data.Length + i * UsbIpCodes.IsoDescriptorSize;
			W32(reply, at, (uint)packets[i].Offset);
			W32(reply, at + 4, (uint)packets[i].Length);
			W32(reply, at + 8, (uint)packets[i].ActualLength);
			W32(reply, at + 12, (uint)packets[i].Status);
		}
		return reply;
	}

	public static byte[] RetUnlink(UnlinkCommand command, int status)
	{
		byte[] reply = new byte[UsbIpCodes.HeaderSize];
		W32(reply, 0, UsbIpCodes.RetUnlink);
		W32(reply, 4, command.Sequence);
		W32(reply, 20, (uint)status);
		return reply;
	}

	public static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));

	public static void W32(byte[] b, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), value);
}
