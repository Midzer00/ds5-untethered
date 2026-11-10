// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Kopru.Core.Protocol;

namespace Kopru.Core.Virtual;

// The wired DualShock 4's HID reports and the DualSense <-> DualShock 4 conversions.
//
// Input report 0x01 (64 bytes with the id), public USB layout as documented in community write-ups of the DS4 protocol
// (e.g. the PS4 developer wiki "DS4-USB" page):
//   1 LX, 2 LY, 3 RX, 4 RY (0..255, 128 centre, Y down)
//   5 low nibble D-pad hat (0 up, clockwise, 8 released), 0x10 Square, 0x20 Cross, 0x40 Circle, 0x80 Triangle
//   6 0x01 L1, 0x02 R1, 0x04 L2, 0x08 R2, 0x10 Share, 0x20 Options, 0x40 L3, 0x80 R3
//   7 0x01 PS, 0x02 touchpad click, bits 2..7 report counter
//   8 L2, 9 R2 (0..255)
//   10..11 timestamp (uint16 LE, 5.33 us units), 12 sensor temperature
//   13..18 gyro pitch/yaw/roll, 19..24 accelerometer x/y/z (int16 LE)
//   30 low nibble battery level, 0x10 cable connected (0x20 headphones, 0x40 mic: never set here)
//   33 touch packets in this report, 34 touch packet counter, 35..38 finger 1, 39..42 finger 2
//      (each finger: bit 7 of byte 0 set = not touching, bits 0..6 id; then 12-bit X and 12-bit Y, little-endian packed)
// Output report 0x05 (32 bytes): 1 flags (0x01 rumble, 0x02 lightbar, 0x04 flash), 4 weak (right) motor,
//   5 strong (left) motor, 6..8 lightbar red/green/blue, 9 flash on, 10 flash off.
//
// The DualSense USB input report (0x01) shares the face/shoulder bit layout of bytes 8..9 with DS4 bytes 5..6, so those
// carry over bit for bit. Both pads report raw IMU samples in the same axis order (pitch/yaw/roll, x/y/z) with the same
// orientation and the same calibration record layout, so the samples are copied unchanged and the DS4 calibration
// feature (0x02) is filled from the controller's own DualSense calibration (an assumption, to be checked on hardware).
public static class DualShock4Reports
{
	public const byte InputId = 0x01;
	public const int InputSize = 64;
	public const byte OutputId = 0x05;
	public const int OutputSize = 32;

	// Feature reports games and Steam read; sizes include the report id.
	public const byte CalibrationFeature = 0x02;
	public const int CalibrationSize = 37;
	public const byte PairingFeature = 0x12;
	public const int PairingSize = 16;
	public const byte AddressFeature = 0x81;
	public const int AddressSize = 7;
	public const byte FirmwareFeature = 0xA3;
	public const int FirmwareSize = 49;

	// Output flags.
	public const byte RumbleFlag = 0x01, LightbarFlag = 0x02, FlashFlag = 0x04;

	// Button bits (byte 5 face, byte 6 shoulders/menu, byte 7 PS/touchpad).
	public const byte Square = 0x10, Cross = 0x20, Circle = 0x40, Triangle = 0x80;
	public const byte L1 = 0x01, R1 = 0x02, L2 = 0x04, R2 = 0x08, Share = 0x10, Options = 0x20, L3 = 0x40, R3 = 0x80;
	public const byte PS = 0x01, TouchClick = 0x02;

	public const byte CableConnected = 0x10;

	// Battery nibble while the cable is in and charging finished (community-documented: 0..10 level, 11 full).
	public const byte BatteryFull = 11;

	// Touchpad coordinate ranges: DualSense 1920 x 1080, DualShock 4 1920 x 943 (public specs of both pads).
	public const int DualSenseTouchWidth = 1920, DualSenseTouchHeight = 1080;
	public const int TouchWidth = 1920, TouchHeight = 943;

	// DualSense USB input report offsets (with the report id), from the public DualSense layout.
	private const int DsSticks = 1, DsL2 = 5, DsR2 = 6, DsButtons0 = 8, DsButtons1 = 9, DsButtons2 = 10;
	private const int DsImu = 16, DsSensorTicks = 28, DsTouch = 33, DsStatus = 53;

	// DualShock 4 input report offsets.
	public const int Buttons0 = 5, Buttons1 = 6, Buttons2 = 7, L2Axis = 8, R2Axis = 9, Timestamp = 10, Imu = 13, Battery = 30,
		TouchPackets = 33, TouchCounter = 34, Finger1 = 35, Finger2 = 39;

	// A neutral DS4 input report: sticks centred, D-pad released, no fingers on the touchpad.
	public static byte[] Neutral()
	{
		byte[] r = new byte[InputSize];
		r[0] = InputId;
		r[1] = r[2] = r[3] = r[4] = 0x80;
		r[Buttons0] = 0x08;
		r[TouchPackets] = 1;
		r[Finger1] = r[Finger2] = 0x80;
		return r;
	}

	// A DualSense USB input report (0x01, after the app's InputTransform) as a DualShock 4 input report. counter is the
	// 6-bit report counter, touchCounter the touch packet counter. Mute and Edge-only buttons have no DS4 equivalent.
	public static byte[] FromDualSense(ReadOnlySpan<byte> ds, int counter, byte touchCounter)
	{
		byte[] r = Neutral();
		r[Buttons2] = (byte)((counter & 0x3F) << 2);
		if (ds.Length < UsbReports.InputSize || ds[0] != UsbReports.InputId)
		{
			return r;
		}
		ds.Slice(DsSticks, 4).CopyTo(r.AsSpan(1));
		int hat = ds[DsButtons0] & 0x0F;
		r[Buttons0] = (byte)((hat > 8 ? 8 : hat) | (ds[DsButtons0] & 0xF0));
		byte l2 = ds[DsL2], r2 = ds[DsR2];
		// L2/R2 digital bits follow the analog value the game gets (after the app's dead zones).
		r[Buttons1] = (byte)((ds[DsButtons1] & ~(L2 | R2)) | (l2 > 0 ? L2 : 0) | (r2 > 0 ? R2 : 0));
		r[Buttons2] |= (byte)(ds[DsButtons2] & (PS | TouchClick));
		r[L2Axis] = l2;
		r[R2Axis] = r2;
		// DualSense sensor ticks are 1/3 us, DS4 timestamps 16/3 us (5.33 us): one DS4 tick is 16 DualSense ticks.
		uint ticks = BinaryPrimitives.ReadUInt32LittleEndian(ds[DsSensorTicks..]);
		BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(Timestamp), (ushort)(ticks >> 4));
		ds.Slice(DsImu, 12).CopyTo(r.AsSpan(Imu));
		r[Battery] = BatteryByte(ds[DsStatus]);
		r[TouchCounter] = touchCounter;
		ScaleFinger(ds.Slice(DsTouch, 4), r.AsSpan(Finger1, 4));
		ScaleFinger(ds.Slice(DsTouch + 4, 4), r.AsSpan(Finger2, 4));
		return r;
	}

	// DualSense status byte (low nibble level 0..10, high nibble 0 discharging / 1 charging / 2 full) -> DS4 byte 30.
	// The cable bit is set only while the controller is really on its cable (charging or full), so games show the
	// Bluetooth pad's real battery rather than a wired pad's.
	public static byte BatteryByte(byte dsStatus)
	{
		int level = Math.Min(dsStatus & 0x0F, 10);
		return (dsStatus >> 4) switch
		{
			1 => (byte)(CableConnected | level),
			2 => CableConnected | BatteryFull,
			_ => (byte)level
		};
	}

	// One finger: same packing on both pads; Y is scaled from the DualSense's 1080 rows to the DS4's 943.
	public static void ScaleFinger(ReadOnlySpan<byte> ds, Span<byte> ds4)
	{
		int x = ds[1] | ((ds[2] & 0x0F) << 8);
		int y = (ds[2] >> 4) | (ds[3] << 4);
		x = Math.Clamp(x * TouchWidth / DualSenseTouchWidth, 0, TouchWidth - 1);
		y = Math.Clamp(y * TouchHeight / DualSenseTouchHeight, 0, TouchHeight - 1);
		ds4[0] = ds[0];
		ds4[1] = (byte)x;
		ds4[2] = (byte)((x >> 8) | ((y & 0x0F) << 4));
		ds4[3] = (byte)(y >> 4);
	}

	// The game's DS4 output report as a DualSense output report (0x02): rumble in the classic motor bytes (the bridge
	// plays it as haptics and never sends rumble bits over Bluetooth) and the lightbar colour. Flashing is not
	// approximated: the colour stays on. Null when the report sets neither.
	public static byte[]? ToDualSense(ReadOnlySpan<byte> output)
	{
		if (output.Length < 9 || output[0] != OutputId || (output[1] & (RumbleFlag | LightbarFlag)) == 0)
		{
			return null;
		}
		byte[] report = new byte[UsbReports.OutputSize];
		report[0] = UsbReports.OutputId;
		Span<byte> block = report.AsSpan(1, OutputBlock.Size);
		if ((output[1] & RumbleFlag) != 0)
		{
			block[OutputBlock.Flag0] |= OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect;
			block[OutputBlock.MotorLeft] = output[5];
			block[OutputBlock.MotorRight] = output[4];
		}
		if ((output[1] & LightbarFlag) != 0)
		{
			OutputBlock.SetLightbar(block, output[6], output[7], output[8]);
		}
		return report;
	}

	// ---------------------------------------------------------------- feature reports

	// Feature 0x02 (USB): 17 int16 LE like the DualSense's 0x05, but the gyro range values are in the DS4 USB order
	// (pitch+, yaw+, roll+, pitch-, yaw-, roll-) instead of the plus/minus pairs the DualSense (and the DS4 over
	// Bluetooth) use (community-documented quirk of the DS4's USB calibration report). dualSense is the USB form of the
	// live controller's 0x05 (see CalibrationOverlay); without one, neutral values are used.
	public static byte[] Calibration(byte[]? dualSense)
	{
		byte[] source = dualSense is { Length: >= 35 } && dualSense[0] == CalibrationOverlay.Feature ? dualSense : CalibrationOverlay.NeutralReport();
		short[] v = new short[17];
		for (int i = 0; i < v.Length; i++)
		{
			v[i] = BinaryPrimitives.ReadInt16LittleEndian(source.AsSpan(1 + (2 * i)));
		}
		short[] ordered = [v[0], v[1], v[2], v[3], v[5], v[7], v[4], v[6], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15], v[16]];
		byte[] report = new byte[CalibrationSize];
		report[0] = CalibrationFeature;
		for (int i = 0; i < ordered.Length; i++)
		{
			BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(1 + (2 * i)), ordered[i]);
		}
		return report;
	}

	// Feature 0x12: the pad's Bluetooth address (least significant byte first), three constant bytes (08 25 00, as seen
	// in public dumps), then the paired host's address (none: zeros).
	public static byte[] Pairing(ReadOnlySpan<byte> address)
	{
		byte[] report = new byte[PairingSize];
		report[0] = PairingFeature;
		WriteAddress(address, report.AsSpan(1));
		report[7] = 0x08;
		report[8] = 0x25;
		return report;
	}

	// Feature 0x81: the pad's address alone (least significant byte first).
	public static byte[] Address(ReadOnlySpan<byte> address)
	{
		byte[] report = new byte[AddressSize];
		report[0] = AddressFeature;
		WriteAddress(address, report.AsSpan(1));
		return report;
	}

	// Feature 0xA3: firmware build date and time as zero-padded ASCII (16 bytes each), then version words; the hardware
	// (35) and firmware (41) version offsets are the community-documented ones, the values a plausible v2 pad's.
	public const string FirmwareDate = "Sep 21 2018";
	public const string FirmwareTime = "04:50:51";
	public const ushort HardwareVersion = 0xB400;
	public const ushort FirmwareVersion = 0x0A01;

	public static byte[] Firmware()
	{
		byte[] report = new byte[FirmwareSize];
		report[0] = FirmwareFeature;
		Encoding.ASCII.GetBytes(FirmwareDate).CopyTo(report, 1);
		Encoding.ASCII.GetBytes(FirmwareTime).CopyTo(report, 17);
		BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(35), HardwareVersion);
		BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(41), FirmwareVersion);
		return report;
	}

	// address is written in display order (most significant byte first); reports carry it reversed.
	private static void WriteAddress(ReadOnlySpan<byte> address, Span<byte> target)
	{
		for (int i = 0; i < 6; i++)
		{
			target[i] = address[5 - i];
		}
	}
}
