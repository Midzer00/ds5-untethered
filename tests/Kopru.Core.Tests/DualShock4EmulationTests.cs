// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// A minimal HID report descriptor parser (USB-IF HID 1.11 short items): the size in bits of every report per
// (main item kind, report id), and whether the collections balance.
internal static class HidReportParser
{
	public const string Input = "input", Output = "output", Feature = "feature";

	public static (Dictionary<(string Kind, byte Id), int> Bits, int Depth) Parse(ReadOnlySpan<byte> d)
	{
		Dictionary<(string, byte), int> bits = [];
		int size = 0, count = 0, depth = 0;
		byte id = 0;
		int at = 0;
		while (at < d.Length)
		{
			byte prefix = d[at];
			Assert.NotEqual(0xFE, prefix); // no long items
			int length = (prefix & 3) == 3 ? 4 : prefix & 3;
			Assert.True(at + 1 + length <= d.Length, "item runs past the end");
			uint data = 0;
			for (int i = 0; i < length; i++)
			{
				data |= (uint)d[at + 1 + i] << (8 * i);
			}
			int type = (prefix >> 2) & 3;
			int tag = prefix >> 4;
			if (type == 1)
			{
				switch (tag)
				{
					case 7: size = (int)data; break;
					case 8: id = (byte)data; break;
					case 9: count = (int)data; break;
				}
			}
			else if (type == 0)
			{
				string? kind = tag switch { 8 => Input, 9 => Output, 0xB => Feature, _ => null };
				if (kind != null)
				{
					bits[(kind, id)] = bits.GetValueOrDefault((kind, id)) + (size * count);
				}
				depth += tag == 0xA ? 1 : tag == 0xC ? -1 : 0;
				Assert.True(depth >= 0, "end collection without a collection");
			}
			at += 1 + length;
		}
		return (bits, depth);
	}
}

public class DualShock4EmulationTests
{
	private static byte[] Submit(uint seq, uint direction, uint endpoint, int length, byte[]? setup = null, byte[]? data = null)
	{
		byte[] h = new byte[48];
		UsbIpWire.W32(h, 0, UsbIpCodes.CmdSubmit);
		UsbIpWire.W32(h, 4, seq);
		UsbIpWire.W32(h, 8, 0x10001);
		UsbIpWire.W32(h, 12, direction);
		UsbIpWire.W32(h, 16, endpoint);
		UsbIpWire.W32(h, 24, (uint)length);
		UsbIpWire.W32(h, 32, unchecked((uint)-1));
		(setup ?? new byte[8]).CopyTo(h, 40);
		return [.. h, .. data ?? []];
	}

	private static SubmitCommand Command(uint seq, uint direction, uint endpoint, byte[]? data = null, byte[]? setup = null) =>
		new(seq, 0, direction, endpoint, 0, data?.Length ?? 64, 0, -1, 1, setup ?? new byte[8], data ?? [], []);

	// A DualSense USB input report: sticks centred, D-pad released, no fingers, plus the given changes.
	private static byte[] Ds(Action<byte[]>? change = null)
	{
		byte[] r = new byte[UsbReports.InputSize];
		r[0] = UsbReports.InputId;
		r[1] = r[2] = r[3] = r[4] = 0x80;
		r[8] = 0x08;
		r[33] = r[37] = 0x80;
		change?.Invoke(r);
		return r;
	}

	private static byte[] Map(Action<byte[]>? change = null) => DualShock4Reports.FromDualSense(Ds(change), 0, 0);

	private static byte[] GetFeature(VirtualDualShock4 pad, byte id, int length = 255)
	{
		SubmitResult r = pad.Control([0xA1, 0x01, id, 0x03, 0x00, 0x00, (byte)length, (byte)(length >> 8)], []);
		Assert.Equal(0, r.Status);
		return r.Data;
	}

	// One DualSense touch finger: bit 7 of byte 0 = not touching, then packed 12-bit X / Y.
	private static void Finger(byte[] r, int offset, int id, int x, int y, bool down = true)
	{
		r[offset] = (byte)((down ? 0 : 0x80) | id);
		r[offset + 1] = (byte)x;
		r[offset + 2] = (byte)((x >> 8) | ((y & 0x0F) << 4));
		r[offset + 3] = (byte)(y >> 4);
	}

	private static (int Id, bool Down, int X, int Y) ReadFinger(byte[] r, int offset) =>
		(r[offset] & 0x7F, (r[offset] & 0x80) == 0, r[offset + 1] | ((r[offset + 2] & 0x0F) << 8), (r[offset + 2] >> 4) | (r[offset + 3] << 4));

	// ---------------------------------------------------------------- descriptors

	[Fact]
	public void DeviceAndConfigurationDescriptors_AreAWiredDualShock4v2_HidOnly()
	{
		byte[] d = VirtualDualShock4.DeviceDescriptor;
		Assert.Equal((18, 1), (d[0], d[1]));
		Assert.Equal(d.Length, d[0]);
		Assert.Equal((0x054C, 0x09CC), (d[8] | (d[9] << 8), d[10] | (d[11] << 8)));
		Assert.Equal(0x0200, d[2] | (d[3] << 8));
		Assert.Equal((0, 0, 0, 64), (d[4], d[5], d[6], d[7]));
		Assert.Equal(1, d[17]);

		byte[] c = VirtualDualShock4.ConfigurationDescriptor;
		Assert.Equal(c.Length, c[2] | (c[3] << 8));
		int interfaces = 0, at = 0;
		List<byte[]> endpoints = [];
		byte[]? hid = null;
		while (at < c.Length)
		{
			Assert.True(c[at] > 0);
			byte[] item = c[at..(at + c[at])];
			interfaces += item[1] == 4 ? 1 : 0;
			if (item[1] == 4)
			{
				Assert.Equal((3, 0, 0, 2), (item[5], item[6], item[7], item[4]));
			}
			if (item[1] == 5)
			{
				endpoints.Add(item);
			}
			if (item[1] == 0x21)
			{
				hid = item;
			}
			at += c[at];
		}
		Assert.Equal(c.Length, at);
		Assert.Equal(1, interfaces);
		Assert.Equal(c[4], interfaces);
		Assert.Equal(new byte[] { 0x84, 0x03 }, endpoints.Select(e => e[2]));
		Assert.All(endpoints, e => Assert.Equal((3, 64), (e[3], e[4] | (e[5] << 8))));
		Assert.NotNull(hid);
		Assert.Equal(VirtualDualShock4.HidDescriptor, hid);
		Assert.Equal(VirtualDualShock4.ReportDescriptor.Length, hid![7] | (hid[8] << 8));
		Assert.Equal(0x22, hid[6]);

		VirtualDualShock4 pad = new();
		Assert.Equal((VirtualDualShock4.VendorId, VirtualDualShock4.ProductId), (pad.Info.VendorId, pad.Info.ProductId));
		Assert.Equal(((byte)0x03, (byte)0x00, (byte)0x00), Assert.Single(pad.Info.Interfaces));
		Assert.Equal(UsbIpCodes.SpeedFull, pad.Info.Speed);
		Assert.Equal(VirtualPadType.DualShock4, pad.Type);
	}

	[Fact]
	public void ReportDescriptor_DeclaresTheDs4ReportSizes()
	{
		(Dictionary<(string Kind, byte Id), int> bits, int depth) = HidReportParser.Parse(VirtualDualShock4.ReportDescriptor);
		Assert.Equal(0, depth);
		Assert.Equal((DualShock4Reports.InputSize - 1) * 8, bits[(HidReportParser.Input, DualShock4Reports.InputId)]);
		Assert.Equal((DualShock4Reports.OutputSize - 1) * 8, bits[(HidReportParser.Output, DualShock4Reports.OutputId)]);
		Assert.Equal((DualShock4Reports.CalibrationSize - 1) * 8, bits[(HidReportParser.Feature, 0x02)]);
		Assert.Equal((DualShock4Reports.PairingSize - 1) * 8, bits[(HidReportParser.Feature, 0x12)]);
		Assert.Equal((DualShock4Reports.AddressSize - 1) * 8, bits[(HidReportParser.Feature, 0x81)]);
		Assert.Equal((DualShock4Reports.FirmwareSize - 1) * 8, bits[(HidReportParser.Feature, 0xA3)]);
		Assert.Equal(6, bits.Count);
		Assert.All(bits.Values, b => Assert.Equal(0, b % 8));
		// Starts as a Generic Desktop game pad application collection.
		Assert.Equal(new byte[] { 0x05, 0x01, 0x09, 0x05, 0xA1, 0x01 }, VirtualDualShock4.ReportDescriptor[..6]);
	}

	[Fact]
	public void EveryDeclaredFeatureReport_IsAnsweredWithItsDeclaredLength()
	{
		VirtualDualShock4 pad = new();
		(Dictionary<(string Kind, byte Id), int> bits, _) = HidReportParser.Parse(VirtualDualShock4.ReportDescriptor);
		foreach (((_, byte id), int size) in bits.Where(b => b.Key.Kind == HidReportParser.Feature))
		{
			byte[] report = GetFeature(pad, id);
			Assert.Equal(size / 8 + 1, report.Length);
			Assert.Equal(id, report[0]);
			Assert.Equal(report, pad.FeatureReports[id]);
		}
		Assert.Equal(bits.Count(b => b.Key.Kind == HidReportParser.Feature), pad.FeatureReports.Count);
		// Short reads get the start of the report; unknown ids stall.
		Assert.Equal(new byte[] { 0x12, pad.Address[5] }, GetFeature(pad, 0x12, 2));
		Assert.NotEqual(0, pad.Control([0xA1, 0x01, 0x99, 0x03, 0, 0, 64, 0], []).Status);
	}

	[Fact]
	public void Calibration_IsTheControllersOwn_InDs4UsbOrder()
	{
		short[] ds = [1, -2, 3, 8000, -8100, 8200, -8300, 8400, -8500, 540, 541, 8190, -8191, 8192, -8193, 8194, -8195];
		byte[] dsReport = new byte[CalibrationOverlay.UsbLength];
		dsReport[0] = CalibrationOverlay.Feature;
		for (int i = 0; i < ds.Length; i++)
		{
			BinaryPrimitives.WriteInt16LittleEndian(dsReport.AsSpan(1 + (2 * i)), ds[i]);
		}
		byte[] c = GetFeature(new VirtualDualShock4(dualSenseCalibration: dsReport), 0x02);
		Assert.Equal(DualShock4Reports.CalibrationSize, c.Length);
		short[] v = Enumerable.Range(0, 17).Select(i => BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(1 + (2 * i)))).ToArray();
		// Bias, then pitch+/yaw+/roll+, pitch-/yaw-/roll-, speed+/-, accel pairs.
		Assert.Equal(new short[] { 1, -2, 3, 8000, 8200, 8400, -8100, -8300, -8500, 540, 541, 8190, -8191, 8192, -8193, 8194, -8195 }, v);
	}

	[Fact]
	public void Calibration_WithoutTheControllers_IsNeutral_AndGivesStandardScaling()
	{
		byte[] c = GetFeature(new VirtualDualShock4(), 0x02);
		short[] v = Enumerable.Range(0, 17).Select(i => BinaryPrimitives.ReadInt16LittleEndian(c.AsSpan(1 + (2 * i)))).ToArray();
		Assert.Equal(new short[] { 0, 0, 0 }, v[..3]);
		// Gyro: (plus - minus) counts over (speed+ + speed-) deg/s -> about 16.4 counts per deg/s on every axis.
		for (int axis = 0; axis < 3; axis++)
		{
			double perDegree = (v[3 + axis] - v[6 + axis]) / (double)(v[9] + v[10]);
			Assert.InRange(perDegree, 16.0, 16.8);
		}
		// Accelerometer: plus - minus spans 2 g -> 8192 counts per g, centred.
		for (int axis = 0; axis < 3; axis++)
		{
			Assert.Equal(16384, v[11 + (2 * axis)] - v[12 + (2 * axis)]);
			Assert.Equal(0, v[11 + (2 * axis)] + v[12 + (2 * axis)]);
		}
		// A malformed calibration falls back to the same neutral values.
		Assert.Equal(c, DualShock4Reports.Calibration([0x05, 1, 2]));
	}

	[Fact]
	public void PairingAndAddress_CarryAFakeLocallyAdministeredAddress_PerSlot()
	{
		VirtualDualShock4 pad = new(VirtualDualSense.BusIdFor(2));
		byte[] address = pad.Address;
		Assert.Equal(6, address.Length);
		Assert.Equal(0x02, address[0] & 0x03); // locally administered, unicast
		Assert.Equal(2, address[5]);
		Assert.NotEqual(DeviceIdentity.VirtualAddressFor(2), address);
		Assert.Equal(1, new VirtualDualShock4().Address[5]);

		byte[] pairing = GetFeature(pad, 0x12);
		Assert.Equal(DualShock4Reports.PairingSize, pairing.Length);
		Assert.Equal(address.Reverse().ToArray(), pairing[1..7]);
		Assert.Equal(new byte[] { 0x08, 0x25, 0x00 }, pairing[7..10]);
		Assert.All(pairing[10..], b => Assert.Equal(0, b));

		byte[] a = GetFeature(pad, 0x81);
		Assert.Equal(DualShock4Reports.AddressSize, a.Length);
		Assert.Equal(address.Reverse().ToArray(), a[1..]);
	}

	[Fact]
	public void Firmware_HasBuildDateTimeAndVersions()
	{
		byte[] f = GetFeature(new VirtualDualShock4(), 0xA3);
		Assert.Equal(DualShock4Reports.FirmwareSize, f.Length);
		Assert.Equal(DualShock4Reports.FirmwareDate, Encoding.ASCII.GetString(f, 1, 16).TrimEnd('\0'));
		Assert.Equal(DualShock4Reports.FirmwareTime, Encoding.ASCII.GetString(f, 17, 16).TrimEnd('\0'));
		Assert.Equal(DualShock4Reports.HardwareVersion, BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(35)));
		Assert.Equal(DualShock4Reports.FirmwareVersion, BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(41)));
	}

	[Fact]
	public void StringsAndHidDescriptors_AreServed()
	{
		VirtualDualShock4 pad = new();
		string Str(byte index)
		{
			byte[] s = pad.Control([0x80, 6, index, 3, 0x09, 0x04, 255, 0], []).Data;
			Assert.Equal(s.Length, s[0]);
			return Encoding.Unicode.GetString(s, 2, s.Length - 2);
		}
		Assert.Equal("Sony Interactive Entertainment", Str(1));
		Assert.Equal("Wireless Controller", Str(2));
		Assert.Equal(VirtualDualShock4.ReportDescriptor, pad.Control([0x81, 6, 0, 0x22, 0, 0, 0xFF, 0x01], []).Data);
		Assert.Equal(VirtualDualShock4.HidDescriptor, pad.Control([0x81, 6, 0, 0x21, 0, 0, 0xFF, 0], []).Data);
	}

	// ---------------------------------------------------------------- input

	public static TheoryData<int, int, int, byte> Buttons => new()
	{
		// DualSense report byte, bit, DS4 report byte, DS4 bit
		{ 8, 0x10, 5, DualShock4Reports.Square },
		{ 8, 0x20, 5, DualShock4Reports.Cross },
		{ 8, 0x40, 5, DualShock4Reports.Circle },
		{ 8, 0x80, 5, DualShock4Reports.Triangle },
		{ 9, 0x01, 6, DualShock4Reports.L1 },
		{ 9, 0x02, 6, DualShock4Reports.R1 },
		{ 9, 0x10, 6, DualShock4Reports.Share },
		{ 9, 0x20, 6, DualShock4Reports.Options },
		{ 9, 0x40, 6, DualShock4Reports.L3 },
		{ 9, 0x80, 6, DualShock4Reports.R3 },
		{ 10, 0x01, 7, DualShock4Reports.PS },
		{ 10, 0x02, 7, DualShock4Reports.TouchClick },
	};

	[Theory]
	[MemberData(nameof(Buttons))]
	public void Buttons_Map(int dsByte, int dsBit, int ds4Byte, byte ds4Bit)
	{
		byte[] neutral = Map();
		byte[] r = Map(d => d[dsByte] |= (byte)dsBit);
		Assert.Equal(ds4Bit, r[ds4Byte] ^ neutral[ds4Byte]);
		for (int i = 5; i <= 7; i++)
		{
			if (i != ds4Byte)
			{
				Assert.Equal(neutral[i], r[i]);
			}
		}
	}

	[Fact]
	public void MuteAndEdgeButtons_AreIgnored()
	{
		Assert.Equal(Map(), Map(d => d[10] |= 0xFC));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(5)]
	[InlineData(6)]
	[InlineData(7)]
	[InlineData(8)]
	public void Dpad_Hat_CarriesOver(int hat)
	{
		byte[] r = Map(d => d[8] = (byte)(hat | 0x20));
		Assert.Equal(hat, r[5] & 0x0F);
		Assert.Equal(DualShock4Reports.Cross, r[5] & 0xF0);
	}

	[Theory]
	[InlineData(9)]
	[InlineData(15)]
	public void Dpad_InvalidHat_IsReleased(int hat)
	{
		Assert.Equal(8, Map(d => d[8] = (byte)hat)[5] & 0x0F);
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(1, 254)]
	[InlineData(128, 77)]
	[InlineData(255, 255)]
	public void Triggers_AnalogAndDigital(int l2, int r2)
	{
		byte[] r = Map(d => { d[5] = (byte)l2; d[6] = (byte)r2; });
		Assert.Equal((l2, r2), (r[8], r[9]));
		Assert.Equal(l2 > 0, (r[6] & DualShock4Reports.L2) != 0);
		Assert.Equal(r2 > 0, (r[6] & DualShock4Reports.R2) != 0);
	}

	[Fact]
	public void Triggers_DigitalBitsFollowTheAnalogValue_NotTheDualSenseBits()
	{
		// The app's dead zone zeroed the analog value: no digital press either.
		byte[] r = Map(d => d[9] |= 0x0C);
		Assert.Equal(0, r[6] & (DualShock4Reports.L2 | DualShock4Reports.R2));
	}

	[Theory]
	[InlineData(128, 128, 128, 128)]
	[InlineData(0, 0, 255, 255)]
	[InlineData(255, 255, 0, 0)]
	[InlineData(1, 254, 127, 129)]
	public void Sticks_CopyUnchanged(int lx, int ly, int rx, int ry)
	{
		byte[] r = Map(d => { d[1] = (byte)lx; d[2] = (byte)ly; d[3] = (byte)rx; d[4] = (byte)ry; });
		Assert.Equal((lx, ly, rx, ry), (r[1], r[2], r[3], r[4]));
	}

	[Fact]
	public void Neutral_IsCentred_Released_WithNoFingers()
	{
		foreach (byte[] r in new[] { Map(), DualShock4Reports.Neutral(), DualShock4Reports.FromDualSense([], 0, 0), DualShock4Reports.FromDualSense(new byte[64], 0, 0) })
		{
			Assert.Equal(DualShock4Reports.InputSize, r.Length);
			Assert.Equal(DualShock4Reports.InputId, r[0]);
			Assert.Equal((128, 128, 128, 128), (r[1], r[2], r[3], r[4]));
			Assert.Equal(0x08, r[5]);
			Assert.Equal(0, r[6]);
			Assert.Equal(1, r[33]);
			Assert.False(ReadFinger(r, 35).Down);
			Assert.False(ReadFinger(r, 39).Down);
		}
	}

	[Fact]
	public void GyroAndAccelerometer_AreCopiedInOrder()
	{
		short[] imu = [-1234, 2345, -3456, 4567, -5678, 8192];
		byte[] r = Map(d =>
		{
			for (int i = 0; i < imu.Length; i++)
			{
				BinaryPrimitives.WriteInt16LittleEndian(d.AsSpan(16 + (2 * i)), imu[i]);
			}
		});
		Assert.Equal(imu, Enumerable.Range(0, 6).Select(i => BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(13 + (2 * i)))).ToArray());
	}

	[Theory]
	[InlineData(0u, 0)]
	[InlineData(16u, 1)]
	[InlineData(3000u, 187)] // 1 ms
	[InlineData(0x10_0000u, 0)] // wraps at 16 bits
	[InlineData(0x10_0010u, 1)]
	public void Timestamp_IsInDs4Units(uint dsTicks, int ds4)
	{
		byte[] r = Map(d => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(28), dsTicks));
		Assert.Equal(ds4, BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(10)));
	}

	[Theory]
	[InlineData(0x00, 0)]
	[InlineData(0x05, 5)]
	[InlineData(0x0A, 10)]
	[InlineData(0x0F, 10)] // out of range level clamps
	[InlineData(0x13, 0x13)] // charging: cable bit + level
	[InlineData(0x1A, 0x1A)]
	[InlineData(0x2A, 0x1B)] // full: cable bit + 11
	[InlineData(0x20, 0x1B)]
	[InlineData(0xF4, 0x04)] // error states: battery only
	public void Battery_LevelAndCable(int dsStatus, int ds4)
	{
		Assert.Equal(ds4, Map(d => d[53] = (byte)dsStatus)[30]);
	}

	[Theory]
	[InlineData(0, 0, 0, 0)]
	[InlineData(1919, 1079, 1919, 942)]
	[InlineData(960, 540, 960, 471)]
	[InlineData(100, 1080, 100, 942)] // past the edge clamps
	[InlineData(4095, 4095, 1919, 942)]
	public void Touch_IsScaledToTheDs4Pad(int x, int y, int x4, int y4)
	{
		byte[] r = Map(d => { Finger(d, 33, 5, x, y); Finger(d, 37, 6, 1919 - Math.Min(x, 1919), 0, down: false); });
		Assert.Equal((5, true, x4, y4), ReadFinger(r, 35));
		(int id, bool down, int x2, _) = ReadFinger(r, 39);
		Assert.Equal((6, false, 1919 - Math.Min(x, 1919)), (id, down, x2));
		Assert.Equal(1, r[33]);
	}

	[Fact]
	public void Counter_IncrementsPerReport_AndTouchCounterPerTouchChange()
	{
		using VirtualDualShock4 pad = new();
		List<byte[]> seen = [];
		for (int i = 0; i < 70; i++)
		{
			pad.PushInput(Ds());
			seen.Add(pad.LatestInput);
		}
		Assert.Equal(Enumerable.Range(0, 70).Select(i => i % 64), seen.Select(r => r[7] >> 2));
		byte touch = seen[^1][34];
		Assert.All(seen.Skip(1), r => Assert.Equal(touch, r[34]));

		pad.PushInput(Ds(d => Finger(d, 33, 1, 10, 10)));
		Assert.Equal((byte)(touch + 1), pad.LatestInput[34]);
		pad.PushInput(Ds(d => Finger(d, 33, 1, 10, 10)));
		Assert.Equal((byte)(touch + 1), pad.LatestInput[34]);
		pad.PushInput(Ds(d => Finger(d, 33, 1, 20, 10)));
		Assert.Equal((byte)(touch + 2), pad.LatestInput[34]);
	}

	// ---------------------------------------------------------------- output

	private static byte[] Ds4Output(byte flags, byte weak = 0, byte strong = 0, byte red = 0, byte green = 0, byte blue = 0, byte on = 0, byte off = 0)
	{
		byte[] o = new byte[DualShock4Reports.OutputSize];
		o[0] = DualShock4Reports.OutputId;
		o[1] = flags;
		o[4] = weak;
		o[5] = strong;
		(o[6], o[7], o[8], o[9], o[10]) = (red, green, blue, on, off);
		return o;
	}

	[Fact]
	public void Output_RumbleAndLightbar_BecomeADualSenseReport()
	{
		byte[] r = DualShock4Reports.ToDualSense(Ds4Output(0x07, weak: 40, strong: 200, red: 1, green: 2, blue: 3, on: 50, off: 50))!;
		Assert.Equal(UsbReports.OutputSize, r.Length);
		Assert.Equal(UsbReports.OutputId, r[0]);
		byte[] b = r[1..];
		Assert.Equal(OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect, b[OutputBlock.Flag0]);
		Assert.Equal((200, 40), (b[OutputBlock.MotorLeft], b[OutputBlock.MotorRight]));
		Assert.Equal(OutputBlock.LightbarControl, b[OutputBlock.Flag1]);
		Assert.Equal((1, 2, 3), (b[OutputBlock.Red], b[OutputBlock.Red + 1], b[OutputBlock.Red + 2]));
		// No adaptive trigger effects, no improved rumble, nothing else.
		Assert.Equal(0, b[OutputBlock.Flag2]);
		Assert.All(b.AsSpan(OutputBlock.RightTrigger, 22).ToArray(), x => Assert.Equal(0, x));
	}

	[Fact]
	public void Output_OnlyTheFlaggedParts_AndNothingWithoutFlags()
	{
		byte[] rumble = DualShock4Reports.ToDualSense(Ds4Output(0x01, weak: 9, strong: 8, red: 255))![1..];
		Assert.Equal(0, rumble[OutputBlock.Flag1]);
		Assert.Equal((8, 9), (rumble[OutputBlock.MotorLeft], rumble[OutputBlock.MotorRight]));
		Assert.Equal(0, rumble[OutputBlock.Red]);

		byte[] light = DualShock4Reports.ToDualSense(Ds4Output(0x02, weak: 9, strong: 8, red: 255))![1..];
		Assert.Equal(0, light[OutputBlock.Flag0]);
		Assert.Equal((0, 0), (light[OutputBlock.MotorLeft], light[OutputBlock.MotorRight]));
		Assert.Equal(255, light[OutputBlock.Red]);

		Assert.Null(DualShock4Reports.ToDualSense(Ds4Output(0x04, weak: 9, strong: 8, red: 255)));
		Assert.Null(DualShock4Reports.ToDualSense(Ds4Output(0x00, weak: 9)));
		Assert.Null(DualShock4Reports.ToDualSense([0x02, 0xFF, 0, 0, 9, 9, 9, 9, 9]));
		Assert.Null(DualShock4Reports.ToDualSense([0x05, 0xFF]));
	}

	[Fact]
	public void Output_ByInterruptOrSetReport_ReachesOutputReceived()
	{
		using VirtualDualShock4 pad = new();
		List<byte[]> outputs = [];
		pad.OutputReceived += outputs.Add;
		pad.Submit(Command(1, UsbIpCodes.DirOut, VirtualDualShock4.OutEndpoint, Ds4Output(0x01, strong: 100)), _ => { });
		SubmitResult r = pad.Control([0x21, 0x09, 0x05, 0x02, 0, 0, 32, 0], Ds4Output(0x02, red: 7));
		Assert.Equal(0, r.Status);
		// A feature write is accepted and does nothing.
		Assert.Equal(0, pad.Control([0x21, 0x09, 0x13, 0x03, 0, 0, 23, 0], new byte[23]).Status);
		Assert.Equal(2, outputs.Count);
		Assert.Equal(100, outputs[0][1 + OutputBlock.MotorLeft]);
		Assert.Equal(7, outputs[1][1 + OutputBlock.Red]);
	}

	[Fact]
	public void Bridge_RumbleGoesToHaptics_LightbarToTheController_NoRumbleBitsEver()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualShock4 pad = new();
		using ControllerBridge bridge = new(session, pad);

		pad.Submit(Command(1, UsbIpCodes.DirOut, VirtualDualShock4.OutEndpoint, Ds4Output(0x03, weak: 60, strong: 220, red: 10, green: 20, blue: 30)), _ => { });
		Assert.Equal(((byte)220, (byte)60), bridge.Motors);
		byte[] sent = link.Writes.Last(w => w[0] == 0x31).AsSpan(3, OutputBlock.Size).ToArray();
		Assert.Equal((10, 20, 30), (sent[OutputBlock.Red], sent[OutputBlock.Red + 1], sent[OutputBlock.Red + 2]));
		Assert.True(SpinWait.SpinUntil(() => link.Writes.Any(w => w[0] == 0x32 && w.AsSpan(13, 64).ToArray().Any(s => s != 0)), 3000));

		pad.Submit(Command(2, UsbIpCodes.DirOut, VirtualDualShock4.OutEndpoint, Ds4Output(0x01)), _ => { });
		Assert.Equal(((byte)0, (byte)0), bridge.Motors);
		Assert.All(link.Writes.Where(w => w[0] == 0x31), w =>
		{
			Assert.Equal(0, w[3 + OutputBlock.Flag0] & (OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect));
			Assert.Equal(0, w[3 + OutputBlock.Flag2] & OutputBlock.ImprovedRumble);
			Assert.Equal((0, 0), (w[3 + OutputBlock.MotorLeft], w[3 + OutputBlock.MotorRight]));
		});
	}

	[Fact]
	public void Bridge_UserLight_KeepsTheGamesLightbarOut_FallbackTriggerAsInXboxMode()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualShock4 pad = new();
		using ControllerBridge bridge = new(session, pad);
		byte[] fallback = TriggerEffects.Feedback(2, 5);
		bridge.ApplyProfile(new BridgeProfile(LightFromGame: false, FallbackTrigger: fallback));

		pad.Submit(Command(1, UsbIpCodes.DirOut, VirtualDualShock4.OutEndpoint, Ds4Output(0x02, red: 10, green: 20, blue: 30)), _ => { });
		byte[] sent = link.Writes.Last(w => w[0] == 0x31).AsSpan(3, OutputBlock.Size).ToArray();
		Assert.Equal(0, sent[OutputBlock.Flag1] & OutputBlock.LightbarControl);
		Assert.False(bridge.GameControlsLight);
		Assert.Equal(fallback, sent.AsSpan(OutputBlock.RightTrigger, fallback.Length).ToArray());
		Assert.Equal(fallback, sent.AsSpan(OutputBlock.LeftTrigger, fallback.Length).ToArray());
	}

	[Fact]
	public void Bridge_InputReachesTheGameAsDs4()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualShock4 pad = new();
		using ControllerBridge bridge = new(session, pad);
		session.Start();
		SubmitResult? result = null;
		pad.Submit(Command(9, UsbIpCodes.DirIn, VirtualDualShock4.InEndpoint), r => result = r);
		link.Feed(FakeLink.Input(77, 3));
		Assert.True(SpinWait.SpinUntil(() => result != null, 3000));
		Assert.Equal(DualShock4Reports.InputSize, result!.Data.Length);
		Assert.Equal(DualShock4Reports.InputId, result.Data[0]);
	}

	// ---------------------------------------------------------------- USB/IP and the bridge host

	[Fact]
	public async Task OverUsbIp_Enumerates_AndDeliversInputFeaturesAndOutput()
	{
		using VirtualDualShock4 pad = new();
		List<byte[]> outputs = [];
		pad.OutputReceived += o => { lock (outputs) outputs.Add(o); };
		await using UsbIpServer server = new(pad);
		server.Start();

		using (TcpClient list = new())
		{
			await list.ConnectAsync("127.0.0.1", server.Port);
			NetworkStream ls = list.GetStream();
			await ls.WriteAsync(UsbIpWire.OpHeader(UsbIpCodes.OpReqDevlist, 0));
			byte[] reply = await UsbIpWire.ReadExactlyAsync(ls, 8 + 4 + 312 + 4, default);
			Assert.Equal((0x054C, 0x09CC), (BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(12 + 300)), BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(12 + 302))));
			Assert.Equal(1, reply[12 + 311]);
			Assert.Equal((0x03, 0x00, 0x00), (reply[12 + 312], reply[12 + 313], reply[12 + 314]));
		}

		using TcpClient client = new();
		await client.ConnectAsync("127.0.0.1", server.Port);
		NetworkStream s = client.GetStream();
		byte[] busid = new byte[32];
		Encoding.ASCII.GetBytes(VirtualDualShock4.BusId, busid);
		await s.WriteAsync((byte[])[.. UsbIpWire.OpHeader(UsbIpCodes.OpReqImport, 0), .. busid]);
		Assert.Equal(0u, UsbIpWire.U32(await UsbIpWire.ReadExactlyAsync(s, 8 + 312, default), 4));

		await s.WriteAsync(Submit(1, UsbIpCodes.DirIn, 0, 18, setup: [0x80, 6, 0, 1, 0, 0, 18, 0]));
		Assert.Equal(VirtualDualShock4.DeviceDescriptor, (await UsbIpWire.ReadExactlyAsync(s, 48 + 18, default))[48..]);
		int cfgLength = VirtualDualShock4.ConfigurationDescriptor.Length;
		await s.WriteAsync(Submit(2, UsbIpCodes.DirIn, 0, 255, setup: [0x80, 6, 0, 2, 0, 0, 255, 0]));
		Assert.Equal(VirtualDualShock4.ConfigurationDescriptor, (await UsbIpWire.ReadExactlyAsync(s, 48 + cfgLength, default))[48..]);
		await s.WriteAsync(Submit(3, UsbIpCodes.DirOut, 0, 0, setup: [0x00, 9, 1, 0, 0, 0, 0, 0]));
		Assert.Equal(3u, UsbIpWire.U32(await UsbIpWire.ReadExactlyAsync(s, 48, default), 4));
		int hidLength = VirtualDualShock4.ReportDescriptor.Length;
		await s.WriteAsync(Submit(4, UsbIpCodes.DirIn, 0, hidLength + 64, setup: [0x81, 6, 0, 0x22, 0, 0, (byte)(hidLength + 64), 0]));
		Assert.Equal(VirtualDualShock4.ReportDescriptor, (await UsbIpWire.ReadExactlyAsync(s, 48 + hidLength, default))[48..]);

		// GET_FEATURE 0x12 as games and Steam ask it.
		await s.WriteAsync(Submit(5, UsbIpCodes.DirIn, 0, 16, setup: [0xA1, 0x01, 0x12, 0x03, 0, 0, 16, 0]));
		byte[] pairing = (await UsbIpWire.ReadExactlyAsync(s, 48 + 16, default))[48..];
		Assert.Equal(pad.FeatureReports[0x12], pairing);

		// Interrupt IN waits for input, then carries the 64-byte report.
		await s.WriteAsync(Submit(6, UsbIpCodes.DirIn, VirtualDualShock4.InEndpoint, 64));
		await Task.Delay(50);
		pad.PushInput(Ds(r => r[8] = 0x28));
		byte[] input = await UsbIpWire.ReadExactlyAsync(s, 48 + 64, default);
		Assert.Equal(6u, UsbIpWire.U32(input, 4));
		Assert.Equal((DualShock4Reports.InputId, 0x28), (input[48], input[48 + 5]));

		// Interrupt OUT rumble becomes a DualSense output report with the classic motors.
		await s.WriteAsync(Submit(7, UsbIpCodes.DirOut, VirtualDualShock4.OutEndpoint, 32, data: Ds4Output(0x01, weak: 50, strong: 200)));
		Assert.Equal(7u, UsbIpWire.U32(await UsbIpWire.ReadExactlyAsync(s, 48, default), 4));
		Assert.True(SpinWait.SpinUntil(() => { lock (outputs) return outputs.Count == 1; }, 2000));
		Assert.Equal((UsbReports.OutputId, (byte)200, (byte)50), (outputs[0][0], outputs[0][1 + OutputBlock.MotorLeft], outputs[0][1 + OutputBlock.MotorRight]));
	}

	[Fact]
	public async Task SwitchingToDualShock4_SwapsThePadOnce_DetachBeforeAttach()
	{
		ConcurrentQueue<string> log = new();
		QueueControllers controllers = new();
		DeviceIdentity identity = DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
			[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
			new Dictionary<int, string> { [1] = "Sony Interactive Entertainment" }, new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] });
		await using BridgeHost host = new(controllers, new FakeIdentity(identity), new FakeAttacher(log), new ManualTime())
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan
		};
		ConcurrentQueue<VirtualPadType> changed = new();
		host.PadTypeChanged += changed.Enqueue;
		controllers.Links.Enqueue(new RemovableLink(log, "a"));
		await host.StartAsync();
		Assert.True(SpinWait.SpinUntil(() => host.State == BridgeState.Running, 5000));

		host.UsePad(VirtualPadType.DualShock4);
		Assert.True(SpinWait.SpinUntil(() => changed.Count == 1, 5000));
		// Asking again for the pad already in use changes nothing.
		host.UsePad(VirtualPadType.DualShock4);
		await Task.Delay(100);
		host.UsePad(VirtualPadType.Xbox360);
		Assert.True(SpinWait.SpinUntil(() => changed.Count == 2, 5000));

		Assert.Equal([VirtualPadType.DualShock4, VirtualPadType.Xbox360], changed);
		Assert.Equal(["attach 1", "detach 1", "attach 2", "detach 2", "attach 3"], log.Where(e => e.StartsWith("attach") || e.StartsWith("detach")));
		Assert.DoesNotContain("dispose a", log);
		Assert.Equal(BridgeState.Running, host.State);
	}

	[Fact]
	public void PerGameChoice_DualShock4_FollowsTheGameInFront_AndPersists()
	{
		GameCatalog catalog = new([new CatalogGame("Old Game", ["old.exe"], null, false, true)]);
		var processes = new GameModeTests.FakeProcesses();
		var bridge = new PadBridge();
		var games = new GameModeController(catalog, new GameWatcher(catalog, processes), bridge);

		processes.Show("old.exe");
		games.Tick();
		string key = games.ActiveGame!.Key;
		games.SetPadType(key, VirtualPadType.DualShock4);
		Assert.Equal(VirtualPadType.DualShock4, bridge.Pads[^1]);
		int count = bridge.Pads.Count;
		games.SetPadType(key, VirtualPadType.DualShock4);
		Assert.Equal(count, bridge.Pads.Count);

		processes.Exit("old.exe");
		games.Tick();
		Assert.Equal(VirtualPadType.DualSense, bridge.Pads[^1]);

		GameMemory memory = games.SnapshotMemory();
		Assert.Equal(VirtualPadType.DualShock4, memory.PadTypes![key]);
		var again = new GameModeController(catalog, new GameWatcher(catalog, new GameModeTests.FakeProcesses()), null);
		again.LoadMemory(memory);
		Assert.Equal(VirtualPadType.DualShock4, again.PadTypeFor(key));
	}

	private sealed class PadBridge : IGameBridge
	{
		public List<VirtualPadType> Pads { get; } = [];

		public void ApplyProfile(BridgeProfile profile)
		{
		}

		public GameSignals TakeGameSignals() => default;

		public void UsePad(VirtualPadType type) => Pads.Add(type);
	}
}
