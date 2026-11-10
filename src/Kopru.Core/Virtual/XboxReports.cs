// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Kopru.Core.Protocol;

namespace Kopru.Core.Virtual;

// The wired Xbox 360 controller's interrupt reports and the DualSense <-> Xbox 360 conversions.
//
// Input (interrupt IN 0x81, 20 bytes), public layout as documented by the Linux xpad driver and the "XBox 360 controller
// USB protocol" notes (free60): 0 type 0x00, 1 length 0x14, 2 buttons (0x01 up, 0x02 down, 0x04 left, 0x08 right,
// 0x10 start, 0x20 back, 0x40 left stick, 0x80 right stick), 3 buttons (0x01 LB, 0x02 RB, 0x04 guide, 0x10 A, 0x20 B,
// 0x40 X, 0x80 Y), 4 LT, 5 RT (0..255), 6 LX, 8 LY, 10 RX, 12 RY (signed 16-bit little-endian, Y up), 14..19 zero.
// Output (interrupt OUT 0x01): rumble 00 08 00 <large motor> <small motor> 00 00 00; LED 01 03 <pattern> (ignored).
public static class XboxReports
{
	public const int InputSize = 20;

	public const byte DpadUp = 0x01, DpadDown = 0x02, DpadLeft = 0x04, DpadRight = 0x08, Start = 0x10, Back = 0x20, LeftStick = 0x40, RightStick = 0x80;

	public const byte LB = 0x01, RB = 0x02, Guide = 0x04, A = 0x10, B = 0x20, X = 0x40, Y = 0x80;

	// D-pad hat value (0 up, clockwise, 8 released) -> Xbox D-pad bits.
	private static readonly byte[] Hat =
	[
		DpadUp, DpadUp | DpadRight, DpadRight, DpadDown | DpadRight, DpadDown, DpadDown | DpadLeft, DpadLeft, DpadUp | DpadLeft
	];

	// A DualSense USB input report (0x01, after the app's InputTransform) as an Xbox 360 input report.
	// Cross A, Circle B, Square X, Triangle Y, L1/R1 LB/RB, L2/R2 analog LT/RT, Create and touchpad click Back, Options
	// Start, L3/R3 stick clicks, PS Guide; mute is not mapped. Sticks are centred and Y is inverted (XInput: up positive).
	public static byte[] FromDualSense(ReadOnlySpan<byte> report)
	{
		byte[] x = new byte[InputSize];
		x[1] = InputSize;
		if (report.Length < 11 || report[0] != UsbReports.InputId)
		{
			return x;
		}
		byte b0 = report[8], b1 = report[9], b2 = report[10];
		int hat = b0 & 0x0F;
		byte low = hat < 8 ? Hat[hat] : (byte)0;
		if ((b1 & 0x20) != 0) low |= Start;
		if ((b1 & 0x10) != 0 || (b2 & 0x02) != 0) low |= Back;
		if ((b1 & 0x40) != 0) low |= LeftStick;
		if ((b1 & 0x80) != 0) low |= RightStick;
		byte high = 0;
		if ((b1 & 0x01) != 0) high |= LB;
		if ((b1 & 0x02) != 0) high |= RB;
		if ((b2 & 0x01) != 0) high |= Guide;
		if ((b0 & 0x20) != 0) high |= A;
		if ((b0 & 0x40) != 0) high |= B;
		if ((b0 & 0x10) != 0) high |= X;
		if ((b0 & 0x80) != 0) high |= Y;
		x[2] = low;
		x[3] = high;
		x[4] = report[5];
		x[5] = report[6];
		BinaryPrimitives.WriteInt16LittleEndian(x.AsSpan(6), Axis(report[1]));
		BinaryPrimitives.WriteInt16LittleEndian(x.AsSpan(8), InvertedAxis(report[2]));
		BinaryPrimitives.WriteInt16LittleEndian(x.AsSpan(10), Axis(report[3]));
		BinaryPrimitives.WriteInt16LittleEndian(x.AsSpan(12), InvertedAxis(report[4]));
		return x;
	}

	// 0..255 (128 centre) -> -32768..32767 with 128 exactly 0.
	public static short Axis(byte v) => v >= 128 ? (short)((v - 128) * 32767 / 127) : (short)((v - 128) * 256);

	public static short InvertedAxis(byte v) => (short)Math.Clamp(-Axis(v), short.MinValue, short.MaxValue);

	// Rumble from the game as a DualSense output report (0x02) carrying only the classic motors, so the bridge's usual
	// rumble-as-haptics path plays it; null for anything that is not a rumble report (the LED report is ignored).
	public static byte[]? RumbleToDualSense(ReadOnlySpan<byte> output)
	{
		if (output.Length < 5 || output[0] != 0x00 || output[1] < 5)
		{
			return null;
		}
		byte[] report = new byte[UsbReports.OutputSize];
		report[0] = UsbReports.OutputId;
		report[1 + OutputBlock.Flag0] = OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect;
		report[1 + OutputBlock.MotorLeft] = output[3];
		report[1 + OutputBlock.MotorRight] = output[4];
		return report;
	}
}
