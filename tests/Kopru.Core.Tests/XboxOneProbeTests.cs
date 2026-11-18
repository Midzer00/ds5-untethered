// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// The experimental Xbox One (GIP) pad, against the byte layouts of Microsoft's open specification [MS-GIPUSB] 1.0.
public class XboxOneProbeTests
{
	// [MS-GIPUSB] 2.2.2: the compiled metadata blob of the specification's example gamepad JSON.
	private static readonly byte[] SpecExampleMetadata =
	[
		0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xB6, 0x00,
		0x77, 0x00, 0x16, 0x00, 0x1B, 0x00, 0x1C, 0x00, 0x23, 0x00, 0x29, 0x00, 0x46, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x06, 0x01, 0x02, 0x03,
		0x04, 0x06, 0x07, 0x05, 0x01, 0x04, 0x05, 0x06, 0x0A, 0x01, 0x1A, 0x00, 0x57, 0x69, 0x6E, 0x64,
		0x6F, 0x77, 0x73, 0x2E, 0x58, 0x62, 0x6F, 0x78, 0x2E, 0x49, 0x6E, 0x70, 0x75, 0x74, 0x2E, 0x47,
		0x61, 0x6D, 0x65, 0x70, 0x61, 0x64, 0x03, 0x56, 0xFF, 0x76, 0x97, 0xFD, 0x9B, 0x81, 0x45, 0xAD,
		0x45, 0xB6, 0x45, 0xBB, 0xA5, 0x26, 0xD6, 0x2C, 0x40, 0x2E, 0x08, 0xDF, 0x07, 0xE1, 0x45, 0xA5,
		0xAB, 0xA3, 0x12, 0x7A, 0xF1, 0x97, 0xB5, 0xE7, 0x1F, 0xF3, 0xB8, 0x86, 0x73, 0xE9, 0x40, 0xA9,
		0xF8, 0x2F, 0x21, 0x26, 0x3A, 0xCF, 0xB7, 0x02, 0x17, 0x00, 0x20, 0x0E, 0x00, 0x01, 0x00, 0x10,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x17,
		0x00, 0x09, 0x09, 0x00, 0x01, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00
	];

	private static readonly byte[] MetadataRequest = [Gip.Metadata, Gip.FlagSystem, 0x01, 0x00];

	private static byte[] SetState(byte state, byte sequence = 0x02) => [Gip.SetDeviceState, Gip.FlagSystem, sequence, 0x01, state];

	private static byte[] Motor(byte bitmap, byte lt, byte rt, byte lm, byte rm, byte duration, byte delay = 0, byte repeat = 0, byte sequence = 0x10) =>
		[Gip.DirectMotor, 0x00, sequence, 0x09, 0x00, bitmap, lt, rt, lm, rm, duration, delay, repeat];

	// The host's ACK: its payload layout is not published, so any Protocol Control message counts.
	private static readonly byte[] HostAck = [Gip.ProtocolControl, Gip.FlagSystem, 0x03, 0x09, 0x01, 0x00, 0x04, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00];

	private static byte[] Setup(byte requestType, byte request, ushort value, ushort index, ushort length) =>
		[requestType, request, (byte)value, (byte)(value >> 8), (byte)index, (byte)(index >> 8), (byte)length, (byte)(length >> 8)];

	// A DualSense USB input report: sticks centred, D-pad released, plus the given changes.
	private static byte[] Ds(Action<byte[]>? change = null)
	{
		byte[] r = new byte[UsbReports.InputSize];
		r[0] = UsbReports.InputId;
		r[1] = r[2] = r[3] = r[4] = 0x80;
		r[8] = 0x08;
		change?.Invoke(r);
		return r;
	}

	// ---- USB descriptors ----

	[Fact]
	public void DeviceDescriptor_IsTheGipDevice()
	{
		using VirtualXboxOne pad = new(autoTick: false);
		byte[] d = pad.DeviceDescriptor;
		Assert.Equal(18, d.Length);
		Assert.Equal(d.Length, d[0]);
		Assert.Equal(1, d[1]);
		Assert.Equal(0x0200, d[2] | (d[3] << 8));
		Assert.Equal((0xFF, 0x47, 0xD0), (d[4], d[5], d[6]));
		Assert.Equal(64, d[7]);
		Assert.Equal((0x1209, 0x0001), (d[8] | (d[9] << 8), d[10] | (d[11] << 8)));
		Assert.Equal((1, 2, 3, 1), (d[14], d[15], d[16], d[17]));
		Assert.Equal((0x1209, 0x0001, 0xFF, 0x47, 0xD0), (pad.Info.VendorId, pad.Info.ProductId, pad.Info.DeviceClass, pad.Info.DeviceSubClass, pad.Info.DeviceProtocol));
		(byte Class, byte SubClass, byte Protocol) iface = Assert.Single(pad.Info.Interfaces);
		Assert.Equal((0xFF, 0x47, 0xD0), (iface.Class, iface.SubClass, iface.Protocol));
	}

	[Theory]
	[InlineData("one", 0x045E, 0x02EA)]
	[InlineData("series", 0x045E, 0x0B12)]
	[InlineData("1", 0x1209, 0x0001)]
	public void Identity_FromTheSwitch_IsInDescriptorAndHello(string value, int vid, int pid)
	{
		GipIdentity identity = ExperimentalPads.ParseXboxOne(value)!;
		using VirtualXboxOne pad = new(identity: identity, autoTick: false);
		Assert.Equal((vid, pid), (pad.DeviceDescriptor[8] | (pad.DeviceDescriptor[9] << 8), pad.DeviceDescriptor[10] | (pad.DeviceDescriptor[11] << 8)));
		GipDevice gip = new(identity, 0x0000FFFB12345678);
		byte[] hello = gip.HelloPacket();
		Assert.Equal((vid, pid), (BinaryPrimitives.ReadUInt16LittleEndian(hello.AsSpan(12)), BinaryPrimitives.ReadUInt16LittleEndian(hello.AsSpan(14))));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("0")]
	[InlineData("off")]
	public void Switch_Off_GivesNoPad(string? value) => Assert.Null(ExperimentalPads.ParseXboxOne(value));

	[Fact]
	public void ConfigurationDescriptor_OneGipInterface_TwoInterruptEndpointsOf64BytesEvery4ms()
	{
		byte[] c = VirtualXboxOne.ConfigurationDescriptor;
		Assert.Equal(c.Length, c[2] | (c[3] << 8));
		Assert.Equal((9, 2), (c[0], c[1]));
		Assert.Equal(1, c[4]);
		Assert.Equal(1, c[5]);
		Assert.Equal(0xA0, c[7]);
		Assert.InRange(c[8], 1, 0xFA);
		List<byte[]> endpoints = [];
		int interfaces = 0, at = 0;
		while (at < c.Length)
		{
			Assert.True(c[at] > 0);
			if (c[at + 1] == 4)
			{
				interfaces++;
				Assert.Equal((0, 0, 2), (c[at + 2], c[at + 3], c[at + 4]));
				Assert.Equal((0xFF, 0x47, 0xD0), (c[at + 5], c[at + 6], c[at + 7]));
			}
			if (c[at + 1] == 5)
			{
				Assert.Equal(7, c[at]);
				endpoints.Add(c[at..(at + 7)]);
			}
			at += c[at];
		}
		Assert.Equal(c.Length, at);
		Assert.Equal(1, interfaces);
		Assert.Equal([0x01, 0x81], endpoints.Select(e => e[2]));
		Assert.All(endpoints, e => Assert.Equal((3, 64, 4), (e[3], e[4] | (e[5] << 8), e[6])));
	}

	[Fact]
	public void MicrosoftOsDescriptors_NameTheXgip10CompatibleId()
	{
		using VirtualXboxOne pad = new(autoTick: false);
		SubmitResult os = pad.Control(Setup(0x80, 6, 0x03EE, 0, 0xFF), []);
		Assert.Equal(0, os.Status);
		Assert.Equal(18, os.Data.Length);
		Assert.Equal((18, 3), (os.Data[0], os.Data[1]));
		Assert.Equal("MSFT100", Encoding.Unicode.GetString(os.Data, 2, 14));
		Assert.Equal((0x90, 0x00), (os.Data[16], os.Data[17]));

		SubmitResult header = pad.Control(Setup(0xC0, 0x90, 0, 4, 0x10), []);
		Assert.Equal(16, header.Data.Length);
		Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(header.Data));
		SubmitResult full = pad.Control(Setup(0xC0, 0x90, 0, 4, 0x28), []);
		byte[] d = full.Data;
		Assert.Equal(40, d.Length);
		Assert.Equal(0x0100, BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(4)));
		Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6)));
		Assert.Equal(1, d[8]);
		Assert.Equal((0, 1), (d[16], d[17]));
		Assert.Equal("XGIP10\0\0", Encoding.ASCII.GetString(d, 18, 8));
		Assert.All(d[26..], b => Assert.Equal(0, b));

		// Extended properties, other vendor requests, class requests and the device qualifier: stalled.
		Assert.NotEqual(0, pad.Control(Setup(0xC1, 0x90, 0, 5, 0x0A), []).Status);
		Assert.NotEqual(0, pad.Control(Setup(0xC0, 0x01, 0, 0, 0x10), []).Status);
		Assert.NotEqual(0, pad.Control(Setup(0xA1, 0x01, 0x0100, 0, 0x10), []).Status);
		Assert.NotEqual(0, pad.Control(Setup(0x80, 6, 0x0600, 0, 10), []).Status);
	}

	[Fact]
	public void Strings_SerialIs32HexDigitsWithTheDeviceId()
	{
		using VirtualXboxOne pad = new(autoTick: false);
		Assert.Equal([4, 3, 0x09, 0x04], pad.Control(Setup(0x80, 6, 0x0300, 0, 0xFF), []).Data);
		byte[] serial = pad.Control(Setup(0x80, 6, 0x0303, 0x0409, 0xFF), []).Data;
		string text = Encoding.Unicode.GetString(serial, 2, serial.Length - 2);
		Assert.Equal(32, text.Length);
		Assert.All(text, c => Assert.True(Uri.IsHexDigit(c)));
		ulong id = pad.DeviceId;
		Assert.EndsWith(id.ToString("X16"), text);
		Assert.Equal(0x0000FFFBUL, id >> 32);
		Assert.NotEqual(0UL, id & 0xFFFFFFFF);
		// A new random ID per pad (2.2.1.3: random at boot-up).
		using VirtualXboxOne other = new(autoTick: false);
		Assert.NotEqual(id, other.DeviceId);
		Assert.NotEmpty(pad.Control(Setup(0x80, 6, 0x0302, 0x0409, 0xFF), []).Data);
	}

	// ---- Metadata ----

	[Fact]
	public void Metadata_BuiltFromTheSpecExampleJson_IsTheSpecExampleBlob()
	{
		byte[] built = GipMetadata.Build([(1, 0)], [1, 2, 3, 4, 6, 7], [1, 4, 5, 6, 10], ["Windows.Xbox.Input.Gamepad"],
			[GipMetadata.IController, GipMetadata.IGamepad, GipMetadata.INavigationController],
			[new GipMetadataMessage(32, 14, true), new GipMetadataMessage(9, 9, false)]);
		Assert.Equal(SpecExampleMetadata, built);
		Assert.Equal(SpecExampleMetadata, GipMetadata.Gamepad(securityOptOut: false));
	}

	[Fact]
	public void Metadata_OursAddsTheSecurityOptOutInterface()
	{
		byte[] blob = GipMetadata.Gamepad(securityOptOut: true);
		Assert.Equal(SpecExampleMetadata.Length + 16, blob.Length);
		Assert.Equal(blob.Length, BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(14)));
		int device = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(16));
		Assert.Equal(0x77 + 16, device);
		int interfaces = 16 + BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(16 + 12));
		Assert.Equal(4, blob[interfaces]);
		Assert.Equal(GipMetadata.SecurityOptOut.ToByteArray(), blob[(interfaces + 1 + 48)..(interfaces + 1 + 64)]);
		Assert.Equal([0x77, 0xCE, 0x34, 0x7A], blob[(interfaces + 49)..(interfaces + 53)]);
		// The message list follows the device block unchanged.
		Assert.Equal(SpecExampleMetadata[0x87..], blob[(16 + device)..]);
	}

	[Fact]
	public void Metadata_Fragments_FollowTheSpecHeadersAndReassemble()
	{
		List<byte[]> fragments = GipDevice.Fragments(SpecExampleMetadata, 0x05);
		Assert.Equal(4, fragments.Count);
		Assert.All(fragments, f => Assert.InRange(f.Length, 7, 64));
		// First: flags 0xF0, payload length 58, total 182 as B6 01.
		Assert.Equal([0x04, 0xF0, 0x05, 58, 0xB6, 0x01], fragments[0][..6]);
		// Middle (offset below 128): flags 0xA0, payload length 58 as BA 00, offset 58 / 116.
		Assert.Equal([0x04, 0xA0, 0x05, 0xBA, 0x00, 58], fragments[1][..6]);
		Assert.Equal([0x04, 0xA0, 0x05, 0xBA, 0x00, 116], fragments[2][..6]);
		// Last: flags 0xB0, payload length 8, offset 174 as AE 01.
		Assert.Equal([0x04, 0xB0, 0x05, 8, 0xAE, 0x01], fragments[3][..6]);

		byte[] assembled = new byte[SpecExampleMetadata.Length];
		int? total = null;
		foreach (byte[] f in fragments)
		{
			GipMessage m = Assert.Single(Gip.ParseAll(f, out bool truncated));
			Assert.False(truncated);
			Assert.True(m.IsFragment);
			Assert.Equal(6, m.HeaderSize);
			int offset = (m.Flags & Gip.FlagInitFragment) != 0 ? 0 : m.TotalOrOffset!.Value;
			if ((m.Flags & Gip.FlagInitFragment) != 0)
			{
				total = m.TotalOrOffset;
			}
			m.Payload.CopyTo(assembled, offset);
		}
		Assert.Equal(SpecExampleMetadata.Length, total);
		Assert.Equal(SpecExampleMetadata, assembled);
	}

	[Fact]
	public void Metadata_LongerBlob_MiddleFragmentPastOffset127_UsesTwoByteOffset()
	{
		byte[] blob = new byte[300];
		List<byte[]> fragments = GipDevice.Fragments(blob, 1);
		byte[] third = fragments[3]; // offset 174
		Assert.Equal([0x04, 0xA0, 0x01, 58, 0xAE, 0x01], third[..6]);
		GipMessage m = Assert.Single(Gip.ParseAll(third, out _));
		Assert.Equal(174, m.TotalOrOffset);
	}

	// ---- Handshake ----

	private static (GipDevice Gip, List<string> Notes) NewDevice()
	{
		GipDevice gip = new(GipIdentity.Generic, 0x0000FFFB0A0B0C0D);
		List<string> notes = [];
		gip.Note += notes.Add;
		return (gip, notes);
	}

	private static List<byte[]> Drain(GipDevice gip, long now)
	{
		List<byte[]> packets = [];
		while (gip.NextPacket(now) is { } p)
		{
			packets.Add(p);
		}
		return packets;
	}

	[Fact]
	public void Hello_HasTheSpecLayout_AndRepeatsEvery500ms()
	{
		(GipDevice gip, _) = NewDevice();
		gip.Begin(0);
		Assert.Equal(GipDeviceState.Arrival, gip.State);
		byte[] hello = Assert.Single(Drain(gip, 0));
		Assert.Equal(32, hello.Length);
		Assert.Equal((0x02, 0x20, 0x1C), (hello[0], hello[1], hello[3]));
		Assert.NotEqual(0, hello[2]);
		Assert.Equal(0x0000FFFB0A0B0C0DUL, BinaryPrimitives.ReadUInt64LittleEndian(hello.AsSpan(4)));
		Assert.Equal((0, 0), (hello[10], hello[11]));
		Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(hello.AsSpan(16)));
		Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(hello.AsSpan(18)));
		Assert.Equal([0x01, 0x00, 0x01, 0x00, 0x01, 0x00], hello[26..32]);

		gip.Tick(499);
		Assert.Empty(Drain(gip, 499));
		gip.Tick(500);
		byte[] second = Assert.Single(Drain(gip, 500));
		Assert.Equal(0x02, second[0]);
		Assert.NotEqual(hello[2], second[2]);
		// A Hello the host has not picked up yet is not queued twice.
		gip.Tick(1000);
		gip.Tick(1500);
		Assert.Single(Drain(gip, 1500));
	}

	[Fact]
	public void Handshake_MetadataAckStart_ThenStatusAndInput()
	{
		(GipDevice gip, List<string> notes) = NewDevice();
		gip.Begin(0);
		Drain(gip, 0);
		gip.Receive(MetadataRequest, 10);
		Assert.Equal(GipDeviceState.Metadata, gip.State);
		List<byte[]> fragments = Drain(gip, 12);
		Assert.Equal(GipDevice.Fragments(gip.MetadataBlob, fragments[0][2]).Select(Convert.ToHexString), fragments.Select(Convert.ToHexString));
		Assert.Equal(GipDeviceState.WaitAck, gip.State);
		// No more Hellos while the metadata is out.
		gip.Tick(600);
		Assert.Empty(Drain(gip, 600));

		gip.Receive(HostAck, 620);
		Assert.Equal(GipDeviceState.Idle, gip.State);
		byte[] complete = Assert.Single(Drain(gip, 620));
		(byte low, byte high) = Gip.TwoByteNumber(gip.MetadataBlob.Length);
		Assert.Equal([0x04, 0xA0, fragments[0][2], 0x00, low, high], complete);

		gip.Receive(SetState(Gip.StateStart), 630);
		Assert.Equal(GipDeviceState.Active, gip.State);
		List<byte[]> after = Drain(gip, 630);
		Assert.Equal(2, after.Count);
		Assert.Equal([0x03, 0x20, after[0][2], 0x04, 0x80, 0x00, 0x00, 0x00], after[0]);
		Assert.Equal((0x20, 0x00, 0x0E), (after[1][0], after[1][1], after[1][3]));
		Assert.Equal(18, after[1].Length);
		Assert.Equal((1, 1, 1, 1), (gip.Counters.MetadataRequests, gip.Counters.MetadataCompletes, gip.Counters.Starts, gip.Counters.InputReports));
		Assert.Contains(notes, n => n.Contains("Set Device State START"));
	}

	[Fact]
	public void Handshake_NoAck_GoesBackToHello()
	{
		(GipDevice gip, List<string> notes) = NewDevice();
		gip.Begin(0);
		Drain(gip, 0);
		gip.Receive(MetadataRequest, 0);
		Drain(gip, 100);
		gip.Tick(1099);
		Assert.Equal(GipDeviceState.WaitAck, gip.State);
		gip.Tick(1100);
		Assert.Equal(GipDeviceState.Arrival, gip.State);
		Assert.Equal(0x02, Assert.Single(Drain(gip, 1100))[0]);
		Assert.Equal(1, gip.Counters.MetadataTimeouts);
		Assert.Contains(notes, n => n.Contains("no Protocol Control ACK"));
	}

	[Fact]
	public void Handshake_NoStartAfterMetadata_AssumesStartAfter500ms()
	{
		(GipDevice gip, _) = NewDevice();
		gip.Begin(0);
		gip.Receive(MetadataRequest, 0);
		Drain(gip, 0);
		gip.Receive(HostAck, 50);
		Drain(gip, 50);
		gip.Tick(549);
		Assert.Equal(GipDeviceState.Idle, gip.State);
		gip.Tick(550);
		Assert.Equal(GipDeviceState.Active, gip.State);
		Assert.Equal(1, gip.Counters.AssumedStarts);
		Assert.Equal(0x03, Drain(gip, 550)[0][0]);
	}

	[Fact]
	public void Handshake_StartWithoutMetadata_CachedByHost_GoesActive()
	{
		(GipDevice gip, List<string> notes) = NewDevice();
		gip.Begin(0);
		gip.Receive(SetState(Gip.StateStart), 5);
		Assert.Equal(GipDeviceState.Active, gip.State);
		List<byte[]> packets = Drain(gip, 5);
		Assert.Equal([0x03, 0x20], packets.Select(p => p[0]));
		Assert.Contains(notes, n => n.Contains("cached"));
	}

	[Fact]
	public void Active_StatusEverySecondForTenSeconds_ThenEvery20()
	{
		(GipDevice gip, _) = NewDevice();
		gip.Begin(0);
		gip.Receive(SetState(Gip.StateStart), 0);
		Drain(gip, 0);
		List<long> statusAt = [];
		for (long t = 0; t <= 60_000; t += 100)
		{
			gip.Tick(t);
			foreach (byte[] p in Drain(gip, t))
			{
				if (p[0] == Gip.Status)
				{
					statusAt.Add(t);
				}
			}
		}
		Assert.Equal([1000, 2000, 3000, 4000, 5000, 6000, 7000, 8000, 9000, 10000, 30000, 50000], statusAt);
	}

	[Fact]
	public void Reset_SendsPoweringOff_ThenHelloAfter500ms()
	{
		(GipDevice gip, _) = NewDevice();
		List<string> stops = [];
		gip.MotorsStop += stops.Add;
		gip.Begin(0);
		gip.Receive(SetState(Gip.StateStart), 0);
		Drain(gip, 0);
		gip.Receive(SetState(Gip.StateReset), 100);
		byte[] off = Assert.Single(Drain(gip, 100));
		Assert.Equal(new byte[] { 0x03, 0x20, off[2], 0x04, 0x00, 0x00, 0x00, 0x00 }, off);
		gip.Tick(599);
		Assert.Equal(GipDeviceState.Resetting, gip.State);
		gip.Tick(600);
		Assert.Equal(GipDeviceState.Arrival, gip.State);
		Assert.Equal(0x02, Assert.Single(Drain(gip, 600))[0]);
		Assert.Equal(["RESET"], stops);
	}

	[Fact]
	public void Off_SendsOnePoweringOffStatus_ThenNothing()
	{
		(GipDevice gip, _) = NewDevice();
		gip.Begin(0);
		gip.Receive(SetState(Gip.StateStart), 0);
		Drain(gip, 0);
		gip.SetInput(new byte[14] { 0x10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false);
		gip.Receive(SetState(Gip.StateOff), 100);
		byte[] status = Assert.Single(Drain(gip, 100));
		Assert.Equal((0x03, 0x04, 0x00), (status[0], status[3], status[4]));
		gip.Tick(30_000);
		Assert.Empty(Drain(gip, 30_000));
		Assert.Equal(GipDeviceState.Off, gip.State);
	}

	[Fact]
	public void Sequence_NeverZero_AndWraps()
	{
		(GipDevice gip, _) = NewDevice();
		gip.Begin(0);
		gip.Receive(SetState(Gip.StateStart), 0);
		Drain(gip, 0);
		List<byte> sequences = [];
		for (int i = 0; i < 600; i++)
		{
			gip.SetInput([(byte)((i & 1) + 1), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], false);
			sequences.Add(Assert.Single(Drain(gip, 0))[2]);
		}
		Assert.DoesNotContain((byte)0, sequences);
		Assert.Contains((byte)255, sequences);
		Assert.Equal(sequences[0], sequences[255]);
	}

	[Fact]
	public void CoalescedTransfer_EveryMessageIsHandled()
	{
		(GipDevice gip, _) = NewDevice();
		List<GipMotorCommand> commands = [];
		gip.MotorCommand += commands.Add;
		gip.Begin(0);
		gip.Receive([.. SetState(Gip.StateStart), .. Motor(0x0F, 10, 20, 30, 40, 50)], 0);
		Assert.Equal(GipDeviceState.Active, gip.State);
		Assert.Equal(new GipMotorCommand(0x0F, 10, 20, 30, 40, 50, 0, 0), Assert.Single(commands));
	}

	[Fact]
	public void SecurityAndUnknownMessages_AreLoggedNotAnswered()
	{
		(GipDevice gip, List<string> notes) = NewDevice();
		gip.Begin(0);
		Drain(gip, 0);
		gip.Receive([Gip.Security, 0xF0, 0x01, 0x3A, 0x80, 0x01, .. new byte[58]], 0);
		gip.Receive([0x1E, 0x30, 0x01, 0x01, 0x04], 0);
		Assert.Equal(1, gip.Counters.SecurityMessages);
		Assert.Equal(1, gip.Counters.OtherHostMessages);
		Assert.Contains(notes, n => n.StartsWith("SECURITY message"));
		Assert.Contains(notes, n => n.Contains("asked for an ACK of Extended Command"));
		Assert.Empty(Drain(gip, 0).Where(p => p[0] != Gip.Hello));
	}

	[Fact]
	public void Off_BeforeAnyStart_AnnouncesAgainAfterAPause_ThreeTimesAtMost()
	{
		(GipDevice gip, List<string> notes) = NewDevice();
		gip.Begin(0);
		long t = 0;
		for (int attempt = 1; attempt <= GipDevice.MaxReannounces + 1; attempt++)
		{
			gip.Receive(MetadataRequest, t);
			Drain(gip, t);
			gip.Receive(HostAck, t);
			Drain(gip, t);
			gip.Receive(SetState(Gip.StateOff), t);
			Assert.Equal(0x00, Assert.Single(Drain(gip, t))[4]);
			Assert.Equal(GipDeviceState.Off, gip.State);
			gip.Tick(t + GipDevice.ReannounceDelayMs - 1);
			Assert.Empty(Drain(gip, t + GipDevice.ReannounceDelayMs - 1));
			t += GipDevice.ReannounceDelayMs;
			gip.Tick(t);
			if (attempt <= GipDevice.MaxReannounces)
			{
				Assert.Equal(GipDeviceState.Arrival, gip.State);
				Assert.Equal(Gip.Hello, Assert.Single(Drain(gip, t))[0]);
			}
			else
			{
				Assert.Equal(GipDeviceState.Off, gip.State);
				Assert.Empty(Drain(gip, t));
			}
		}
		Assert.Equal(GipDevice.MaxReannounces, gip.Reannounces);
		Assert.Contains(notes, n => n.Contains("OFF came before any START") && n.Contains("attempt 1 of 3"));
		Assert.Contains(notes, n => n.Contains("no more automatic re-announces"));

		// The PS (Guide) button still wakes it, and that press is not reported.
		gip.SetInput(new byte[14], true);
		gip.Tick(t + 1);
		Assert.Equal(Gip.Hello, Assert.Single(Drain(gip, t + 1))[0]);
		gip.Receive(SetState(Gip.StateStart), t + 2);
		Assert.DoesNotContain(Drain(gip, t + 2), p => p[0] == Gip.GuideButton);
		gip.SetInput(new byte[14], false);
		Assert.Equal(0x00, Assert.Single(Drain(gip, t + 3), p => p[0] == Gip.GuideButton)[4]);
	}

	[Fact]
	public void Off_AfterStart_StaysOffUntilPs()
	{
		(GipDevice gip, _) = NewDevice();
		gip.Begin(0);
		gip.Receive(SetState(Gip.StateStart), 0);
		Drain(gip, 0);
		gip.Receive(SetState(Gip.StateOff), 10);
		Drain(gip, 10);
		gip.Tick(60_000);
		Assert.Empty(Drain(gip, 60_000));
		Assert.Equal(0, gip.Reannounces);
		gip.SetInput(new byte[14], true);
		gip.Tick(60_001);
		Assert.Equal(GipDeviceState.Arrival, gip.State);
	}

	[Fact]
	public void SetDeviceState_Reserved0x06_WithLongPayload_IsLoggedAndIgnored()
	{
		(GipDevice gip, List<string> notes) = NewDevice();
		gip.Begin(0);
		gip.Receive(SetState(Gip.StateStart), 0);
		Drain(gip, 0);
		gip.Receive([Gip.SetDeviceState, Gip.FlagSystem, 0x07, 0x0F, 0x06, .. new byte[14]], 5);
		Assert.Equal(GipDeviceState.Active, gip.State);
		Assert.Contains(notes, n => n.Contains("reserved in [MS-GIPUSB] table 40") && n.Contains("15-byte payload 06 00"));
	}

	[Fact]
	public void PowerOff_SendsPoweringOffStatusOnce_ThenSilence()
	{
		(GipDevice gip, _) = NewDevice();
		List<string> stops = [];
		gip.MotorsStop += stops.Add;
		gip.Begin(0);
		gip.Receive(SetState(Gip.StateStart), 0);
		Drain(gip, 0);
		Assert.True(gip.PowerOff("test"));
		Assert.True(gip.HasQueuedPackets);
		Assert.Equal(new byte[] { 0x03, 0x00 }, Assert.Single(Drain(gip, 0)).Where((_, i) => i is 0 or 4));
		Assert.False(gip.HasQueuedPackets);
		Assert.False(gip.PowerOff("again"));
		gip.Tick(100_000);
		Assert.Empty(Drain(gip, 100_000));
		Assert.Equal(["power off"], stops);
	}

	[Fact]
	public async Task Pad_BeforeUnplug_HandsThePoweringOffStatusToTheHost()
	{
		ConcurrentQueue<string> log = new();
		using VirtualXboxOne pad = new(autoTick: false) { Log = log.Enqueue, PowerOffSettleMs = 0, PowerOffDeliveryMs = 0 };
		Host host = new(pad);
		host.In();
		host.Out(SetState(Gip.StateStart));
		Assert.Equal(0x03, host.In()!.Data[0]);
		Assert.Equal(0x20, host.In()!.Data[0]);
		SubmitResult? held = null;
		pad.Submit(new SubmitCommand(99, 0, UsbIpCodes.DirIn, 1, 0, 64, 0, 0, 0, new byte[8], [], []), r => held = r);
		Assert.Null(held);
		await pad.BeforeUnplugAsync();
		Assert.Equal((0x03, 0x00), (held!.Data[0], held.Data[4]));
		Assert.Equal(GipDeviceState.Off, pad.State);
		Assert.True(PadRequestLog.Flush(TimeSpan.FromSeconds(5)));
		Assert.Contains(log, l => l.Contains("powering off (unplug)"));
		Assert.Contains(log, l => l.Contains("picked up by the host"));
	}

	[Fact]
	public void BridgeOnlyNoticesUnplugForTheXboxOnePad()
	{
		Assert.IsAssignableFrom<IUnplugNotice>(new VirtualXboxOne(autoTick: false));
		Assert.False(typeof(IUnplugNotice).IsAssignableFrom(typeof(VirtualXbox360)));
		Assert.False(typeof(IUnplugNotice).IsAssignableFrom(typeof(VirtualDualShock4)));
		Assert.False(typeof(IUnplugNotice).IsAssignableFrom(typeof(VirtualDualSense)));
	}

	// ---- Input ----

	[Fact]
	public void Input_FromDualSense_MapsButtonsTriggersSticksAndGuide()
	{
		byte[] report = Ds(r =>
		{
			r[8] = 0x22;          // Cross + D-pad right
			r[9] = 0x21;          // Options + L1
			r[10] = 0x01;         // PS
			r[5] = 255;           // L2
			r[6] = 0;             // R2
			r[1] = 255;           // left stick right
			r[2] = 0;             // left stick up
		});
		byte[] p = GipReports.FromDualSense(report, out bool guide);
		Assert.True(guide);
		Assert.Equal([0x14, 0x18, 0xFF, 0x03, 0x00, 0x00, 0xFF, 0x7F, 0xFF, 0x7F, 0x00, 0x00, 0x00, 0x00], p);

		byte[] rest = GipReports.FromDualSense(Ds(r =>
		{
			r[8] = 0x08 | 0x10 | 0x40 | 0x80; // Square, Circle, Triangle, D-pad released
			r[9] = 0x02 | 0x10 | 0x40 | 0x80; // R1, Create, L3, R3
			r[6] = 128;
			r[3] = 0;
			r[4] = 255;
		}), out bool noGuide);
		Assert.False(noGuide);
		Assert.Equal(GipReports.X | GipReports.B | GipReports.Y | GipReports.View, rest[0]);
		Assert.Equal(GipReports.RB | GipReports.LeftStick | GipReports.RightStick, rest[1]);
		Assert.Equal(GipReports.Trigger(128), BinaryPrimitives.ReadUInt16LittleEndian(rest.AsSpan(4)));
		Assert.Equal(-32768, BinaryPrimitives.ReadInt16LittleEndian(rest.AsSpan(10)));
		Assert.Equal(-32767, BinaryPrimitives.ReadInt16LittleEndian(rest.AsSpan(12)));
		Assert.Equal((0, 1023), (GipReports.Trigger(0), GipReports.Trigger(255)));
		Assert.Equal(new byte[14], GipReports.FromDualSense([], out _));
	}

	[Fact]
	public void Input_OnlyWhenChanged_GuideAsItsOwnMessage()
	{
		(GipDevice gip, _) = NewDevice();
		gip.Begin(0);
		gip.Receive(SetState(Gip.StateStart), 0);
		Drain(gip, 0);
		byte[] a = GipReports.FromDualSense(Ds(r => r[8] = 0x28), out _);
		gip.SetInput(a, false);
		Assert.Single(Drain(gip, 0));
		gip.SetInput(a.ToArray(), false);
		Assert.Empty(Drain(gip, 0));

		gip.SetInput(a, true);
		byte[] pressed = Assert.Single(Drain(gip, 0));
		Assert.Equal(new byte[] { 0x07, 0x20, pressed[2], 0x02, 0x01, 0x5B }, pressed);
		gip.SetInput(a, false);
		byte[] released = Assert.Single(Drain(gip, 0));
		Assert.Equal((0x07, 0x00), (released[0], released[4]));
		Assert.Equal(2, gip.Counters.GuideMessages);
	}

	[Fact]
	public void Input_NotSentBeforeStart()
	{
		(GipDevice gip, _) = NewDevice();
		gip.Begin(0);
		Drain(gip, 0);
		gip.SetInput([0x10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], true);
		Assert.Empty(Drain(gip, 0));
	}

	// ---- Motors ----

	[Fact]
	public void MotorCommand_ParsesTheSpecLayout()
	{
		GipMotorCommand c = GipReports.ParseMotor([0x00, 0x0F, 0x32, 0x64, 0x0A, 0x14, 0xFF, 0x02, 0x03])!.Value;
		Assert.Equal(new GipMotorCommand(0x0F, 50, 100, 10, 20, 255, 2, 3), c);
		Assert.True(c.SetsLeftTrigger && c.SetsRightTrigger && c.SetsLeftMain && c.SetsRightMain && c.SetsTriggers);
		Assert.Equal((2 + 255) * 4 * 10, c.PlayMs);

		GipMotorCommand onlyRightTrigger = GipReports.ParseMotor([0x00, 0x04, 0, 80, 0, 0, 10, 0, 0])!.Value;
		Assert.True(onlyRightTrigger.SetsRightTrigger);
		Assert.False(onlyRightTrigger.SetsLeftTrigger || onlyRightTrigger.SetsLeftMain || onlyRightTrigger.SetsRightMain);
		Assert.Equal(100, onlyRightTrigger.PlayMs);

		Assert.Null(GipReports.ParseMotor([0x01, 0x0F, 0, 0, 0, 0, 1, 0, 0]));
		Assert.Null(GipReports.ParseMotor([0x00, 0x0F, 0, 0]));
		Assert.Contains("LT 50 RT 100", c.ToString());
	}

	[Fact]
	public void Motors_BitmapSetsOnlyItsMotors_AndEachStopsWhenItsTimeIsOver()
	{
		GipMotors m = new();
		Assert.True(m.Apply(new GipMotorCommand(0x0F, 10, 20, 30, 40, 10), 0));
		Assert.Equal(new GipMotorLevels(30, 40, 10, 20), m.Levels);
		Assert.True(m.Apply(new GipMotorCommand(0x04, 0, 90, 0, 0, 5), 0));
		Assert.Equal(new GipMotorLevels(30, 40, 10, 90), m.Levels);
		Assert.False(m.Tick(49));
		// The right trigger's own 50 ms are over; the others keep their 100 ms.
		Assert.True(m.Tick(50));
		Assert.Equal(new GipMotorLevels(30, 40, 10, 0), m.Levels);
		Assert.False(m.Tick(99));
		Assert.True(m.Tick(100));
		Assert.Equal(default, m.Levels);
		Assert.Equal((4, 2), (m.Expired, m.ExpiredTriggers));

		m.Apply(new GipMotorCommand(0x0F, 200, 200, 200, 200, 255), 0);
		Assert.Equal(new GipMotorLevels(100, 100, 100, 100), m.Levels);
		Assert.True(m.Apply(new GipMotorCommand(0x00, 0, 0, 0, 0, 0), 10));
		Assert.Equal(default, m.Levels);
		Assert.False(m.Tick(100_000));
	}

	[Fact]
	public void Motors_LevelZeroStopsThatMotorAtOnce_OthersKeepPlaying()
	{
		GipMotors m = new();
		m.Apply(new GipMotorCommand(0x0F, 50, 60, 70, 80, 255, 0, 235), 0);
		Assert.True(m.Apply(new GipMotorCommand(0x08, 0, 0, 0, 0, 255), 10));
		Assert.Equal(new GipMotorLevels(70, 80, 0, 60), m.Levels);
		Assert.Equal(0, m.Expired);
	}

	[Fact]
	public void Motors_TheProbesHeldCommand_PlaysUntilRepeatTimesDurationIsOver()
	{
		// What Windows sent for a held WGI vibration in the hardware probe: duration 255, delay 0, repeat 235.
		GipMotors m = new();
		GipMotorCommand held = new(0x0F, 50, 0, 0, 0, 255, 0, 235);
		m.Apply(held, 0);
		Assert.Equal(255 * 236 * 10, held.PlayMs);
		Assert.False(m.Tick(2550)); // one duration is not the end: the repeats follow without a gap
		Assert.False(m.Tick(held.PlayMs - 1));
		Assert.Equal(50, m.Levels.LeftTrigger);
		Assert.True(m.Tick(held.PlayMs));
		Assert.Equal(default, m.Levels);
	}

	[Fact]
	public void Motors_DelayAndRepeat_PulseTheMotor()
	{
		// Two cycles of "wait 30 ms, play 50 ms".
		GipMotors m = new();
		Assert.False(m.Apply(new GipMotorCommand(0x04, 0, 70, 0, 0, 5, 3, 1), 0));
		Assert.Equal(0, m.Levels.RightTrigger);
		Assert.True(m.Tick(30));
		Assert.Equal(70, m.Levels.RightTrigger);
		Assert.False(m.Tick(79));
		Assert.True(m.Tick(80));
		Assert.Equal(0, m.Levels.RightTrigger);
		Assert.True(m.Tick(110));
		Assert.Equal(70, m.Levels.RightTrigger);
		Assert.True(m.Tick(160));
		Assert.Equal(0, m.Levels.RightTrigger);
		Assert.Equal(1, m.ExpiredTriggers);
		Assert.False(m.Tick(1000));
	}

	[Fact]
	public void Pad_TriggerRumble_RaisedOnlyWhenATriggerLevelChanges_MainReportOnlyWhenTheMainMotorsChange()
	{
		long now = 0;
		using VirtualXboxOne pad = new(nowMs: () => now, autoTick: false);
		List<TriggerRumble> triggers = [];
		List<byte[]> outputs = [];
		pad.TriggerRumbleChanged += triggers.Add;
		pad.OutputReceived += outputs.Add;
		Host host = new(pad);

		host.Out(Motor(0x0F, 0, 0, 0, 0, 255, 0, 235)); // Windows' "all clear" on start: nothing changes
		Assert.Empty(triggers);
		Assert.Empty(outputs);

		host.Out(Motor(0x0F, 50, 0, 0, 0, 255, 0, 235));
		Assert.Equal([new TriggerRumble(50, 0)], triggers);
		Assert.Empty(outputs);
		host.Out(Motor(0x0F, 50, 0, 0, 0, 255, 0, 235, sequence: 0x11)); // refreshed, same levels
		Assert.Single(triggers);

		host.Out(Motor(0x0F, 50, 0, 40, 0, 255, 0, 235, sequence: 0x12));
		Assert.Single(triggers);
		Assert.Single(outputs);

		host.Out(Motor(0x0C, 0, 30, 0, 0, 10, sequence: 0x13));
		Assert.Equal(new TriggerRumble(0, 30), triggers[^1]);
		now = 100;
		pad.Tick();
		Assert.Equal(new TriggerRumble(0, 0), triggers[^1]);
		Assert.Single(outputs); // the main motors still play the held command

		host.Out(Motor(0x0F, 0, 0, 0, 0, 0, sequence: 0x14)); // duration 0: everything off
		Assert.Equal(2, outputs.Count);
		Assert.Equal(3, triggers.Count);
	}

	[Fact]
	public void Quiesce_StopsTheMotors()
	{
		(GipDevice gip, _) = NewDevice();
		List<string> stops = [];
		gip.MotorsStop += stops.Add;
		gip.Begin(0);
		gip.Receive(SetState(Gip.StateQuiesce), 0);
		gip.Receive(SetState(Gip.StateStop), 0);
		Assert.Equal(["QUIESCE", "STOP"], stops);
		Assert.Equal(GipDeviceState.Idle, gip.State);
	}

	// ---- The pad over its endpoints ----

	private sealed class Host(VirtualXboxOne pad)
	{
		private uint sequence = 1;

		public SubmitResult? In()
		{
			SubmitResult? result = null;
			pad.Submit(new SubmitCommand(sequence++, 0, UsbIpCodes.DirIn, 1, 0, 64, 0, 0, 0, new byte[8], [], []), r => result = r);
			return result;
		}

		public void Out(byte[] data) =>
			pad.Submit(new SubmitCommand(sequence++, 0, UsbIpCodes.DirOut, 1, 0, data.Length, 0, 0, 0, new byte[8], data, []), _ => { });
	}

	[Fact]
	public void Pad_FullProbeSession_HandshakeInputAndTriggerRumble()
	{
		long now = 0;
		ConcurrentQueue<string> log = new();
		using VirtualXboxOne pad = new(nowMs: () => now, autoTick: false) { Log = log.Enqueue };
		List<byte[]> outputs = [];
		List<GipMotorLevels> levels = [];
		pad.OutputReceived += outputs.Add;
		pad.MotorsChanged += levels.Add;
		Host host = new(pad);

		Assert.Equal(0x02, host.In()!.Data[0]);
		Assert.Null(host.In()); // held until something is sent
		host.Out(MetadataRequest);
		// The held IN gets the first fragment; the rest follow one per IN.
		int fragments = 1;
		while (pad.State == GipDeviceState.Metadata)
		{
			Assert.Equal(0x04, host.In()!.Data[0]);
			fragments++;
		}
		Assert.Equal(GipDevice.Fragments(pad.MetadataBlob, 1).Count, fragments);
		host.Out(HostAck);
		Assert.Equal([0x04, 0xA0], host.In()!.Data[..2]);
		host.Out(SetState(Gip.StateStart));
		Assert.Equal(0x03, host.In()!.Data[0]);
		Assert.Equal(0x20, host.In()!.Data[0]);

		pad.PushInput(Ds(r => r[8] = 0x28)); // Cross
		byte[] input = host.In()!.Data;
		Assert.Equal((0x20, GipReports.A), (input[0], input[4]));
		Assert.Empty(outputs);

		host.Out(Motor(0x0F, 50, 25, 100, 0, 20));
		Assert.Equal(new GipMotorLevels(100, 0, 50, 25), levels[^1]);
		byte[] rumble = outputs[^1];
		Assert.Equal(UsbReports.OutputId, rumble[0]);
		Assert.Equal(255, rumble[1 + OutputBlock.MotorLeft]);
		Assert.Equal(0, rumble[1 + OutputBlock.MotorRight]);

		now = 199;
		pad.Tick();
		Assert.True(pad.Motors.AnyOn);
		now = 200;
		pad.Tick();
		Assert.False(pad.Motors.AnyOn);
		Assert.Equal(0, outputs[^1][1 + OutputBlock.MotorLeft]);

		pad.Released();
		Assert.True(PadRequestLog.Flush(TimeSpan.FromSeconds(5)));
		string all = string.Join("\n", log);
		Assert.Contains("GIP arrival", all);
		Assert.Contains("Metadata Request #1", all);
		Assert.Contains("Metadata Complete sent", all);
		Assert.Contains("Set Device State START", all);
		Assert.Contains("first input report", all);
		Assert.Contains("GIP FIRST TRIGGER RUMBLE", all);
		Assert.Contains("GIP summary (host released)", all);
		Assert.Contains("first IN poll", all);
	}

	[Fact]
	public void Pad_ControlRequestsAreLogged()
	{
		ConcurrentQueue<string> log = new();
		using VirtualXboxOne pad = new(autoTick: false) { Log = log.Enqueue };
		SubmitResult? result = null;
		pad.Submit(new SubmitCommand(1, 0, UsbIpCodes.DirIn, 0, 0, 0x28, 0, 0, 0, Setup(0xC0, 0x90, 0, 4, 0x28), [], []), r => result = r);
		Assert.Equal(40, result!.Data.Length);
		pad.Dispose();
		Assert.True(PadRequestLog.Flush(TimeSpan.FromSeconds(5)));
		Assert.Contains(log, l => l.Contains("vendor request 0x90") && l.Contains("answered 40"));
	}

	[Fact]
	public void Pad_IsTheXboxSeriesType_AndHasNoAudio()
	{
		using VirtualXboxOne pad = new(autoTick: false);
		Assert.Equal(VirtualPadType.XboxSeries, pad.Type);
		Assert.False(pad.Type.HasAudio());
		Assert.False(pad.Type.HasLight());
		SubmitResult? result = null;
		pad.Submit(new SubmitCommand(1, 0, UsbIpCodes.DirIn, 2, 0, 64, 0, 0, 0, new byte[8], [], []), r => result = r);
		Assert.NotEqual(0, result!.Status);
	}
}
