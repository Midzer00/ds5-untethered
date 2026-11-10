// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Kopru.Core.Bridge;
using Kopru.Core.Protocol;

namespace Kopru.Core.Input;

// Every button the DualSense (and the DualSense Edge) reports, one bit each.
[Flags]
public enum PadButtons : uint
{
	None = 0,
	Square = 1u << 0,
	Cross = 1u << 1,
	Circle = 1u << 2,
	Triangle = 1u << 3,
	DpadUp = 1u << 4,
	DpadDown = 1u << 5,
	DpadLeft = 1u << 6,
	DpadRight = 1u << 7,
	L1 = 1u << 8,
	R1 = 1u << 9,
	L2 = 1u << 10,
	R2 = 1u << 11,
	Create = 1u << 12,
	Options = 1u << 13,
	L3 = 1u << 14,
	R3 = 1u << 15,
	PS = 1u << 16,
	Touchpad = 1u << 17,
	Mute = 1u << 18,
	// DualSense Edge only.
	LeftFunction = 1u << 19,
	RightFunction = 1u << 20,
	LeftPaddle = 1u << 21,
	RightPaddle = 1u << 22,
}

// One touchpad finger. X 0..1919, Y 0..1079; Id counts up with each new touch.
public readonly record struct TouchPoint(bool Active, byte Id, int X, int Y)
{
	public const int Width = 1920;
	public const int Height = 1080;

	// Four bytes: bit 7 of the first is set while the finger is NOT down (low 7 bits: touch id), then X in 12 bits
	// (byte 1 + low nibble of byte 2) and Y in 12 bits (high nibble of byte 2 + byte 3).
	public static TouchPoint Parse(ReadOnlySpan<byte> b) =>
		new((b[0] & 0x80) == 0, (byte)(b[0] & 0x7F), b[1] | ((b[2] & 0x0F) << 8), (b[2] >> 4) | (b[3] << 4));
}

// Everything the controller reports in one input report, for the controller test page.
//
// Input body layout (63 bytes; USB report 0x01 from byte 1, Bluetooth report 0x31 from byte 2). Source: the public
// DualSense input report layout as documented by the Linux hid-playstation driver (struct dualsense_input_report) and
// SDL's hidapi_ps5 driver (Edge buttons); offsets below are body offsets:
//   0 LX, 1 LY, 2 RX, 3 RY (0..255, 128 centre, Y down), 4 L2, 5 R2 (0..255), 6 sequence
//   7 buttons0: low nibble D-pad hat (0 up, 1 up-right, ... 7 up-left, 8 released); 0x10 square, 0x20 cross,
//     0x40 circle, 0x80 triangle
//   8 buttons1: 0x01 L1, 0x02 R1, 0x04 L2, 0x08 R2, 0x10 create, 0x20 options, 0x40 L3, 0x80 R3
//   9 buttons2: 0x01 PS, 0x02 touchpad click, 0x04 mute; Edge (SDL): 0x10 left Fn, 0x20 right Fn, 0x40 left paddle,
//     0x80 right paddle (not verified on hardware)
//   15/17/19 gyro X/Y/Z (s16 LE, pitch/yaw/roll), 21/23/25 accelerometer X/Y/Z (s16 LE), 27 sensor timestamp (u32 LE)
//   32 touch point 1, 36 touch point 2 (4 bytes each, see TouchPoint), 52 battery/charging status (see BatteryStatus)
// The short Bluetooth report 0x01 (before the pad is switched to full reports; SDL's PS5SimpleStatePacket_t):
//   1 LX, 2 LY, 3 RX, 4 RY, 5-7 buttons0..2 (same bits; byte 7's high bits are a counter), 8 L2, 9 R2.
public sealed record InputSnapshot(
	byte LeftX, byte LeftY, byte RightX, byte RightY, byte L2, byte R2, PadButtons Buttons,
	TouchPoint Touch1, TouchPoint Touch2,
	short GyroX, short GyroY, short GyroZ, short AccelX, short AccelY, short AccelZ,
	BatteryStatus? Battery, bool IsFull)
{
	public const int Buttons0 = 7, Buttons1 = 8, Buttons2 = 9, Gyro = 15, Accel = 21, TouchOffset = 32;
	public const int SimpleLength = 10;

	public static InputSnapshot Idle { get; } = new(128, 128, 128, 128, 0, 0, PadButtons.None, default, default, 0, 0, 0, 0, 0, 0, null, false);

	public bool IsPressed(PadButtons button) => button != PadButtons.None && (Buttons & button) == button;

	// A full 63-byte input body.
	public static InputSnapshot Parse(ReadOnlySpan<byte> body)
	{
		if (body.Length < InputState.BodySize)
		{
			throw new ArgumentException("input body too short", nameof(body));
		}
		return new InputSnapshot(body[0], body[1], body[2], body[3], body[4], body[5],
			DecodeButtons(body[Buttons0], body[Buttons1], body[Buttons2]),
			TouchPoint.Parse(body[TouchOffset..]), TouchPoint.Parse(body[(TouchOffset + 4)..]),
			S16(body, Gyro), S16(body, Gyro + 2), S16(body, Gyro + 4), S16(body, Accel), S16(body, Accel + 2), S16(body, Accel + 4),
			BatteryStatus.Parse(body), true);
	}

	private static short S16(ReadOnlySpan<byte> body, int at) => BinaryPrimitives.ReadInt16LittleEndian(body[at..]);

	// The short Bluetooth report 0x01 (report ID included): sticks, buttons and triggers only.
	public static InputSnapshot ParseSimple(ReadOnlySpan<byte> report)
	{
		if (report.Length < SimpleLength || report[0] != UsbReports.InputId)
		{
			throw new ArgumentException("not a simple input report", nameof(report));
		}
		return new InputSnapshot(report[1], report[2], report[3], report[4], report[8], report[9],
			DecodeButtons(report[5], report[6], (byte)(report[7] & 0x07)), default, default, 0, 0, 0, 0, 0, 0, null, false);
	}

	// Any input report read from the pad: full USB 0x01 / Bluetooth 0x31 (CRC checked), or the short Bluetooth 0x01.
	// Null for anything else.
	public static InputSnapshot? FromReport(ReadOnlySpan<byte> report, bool bluetooth)
	{
		if (report.IsEmpty)
		{
			return null;
		}
		if (report[0] == BluetoothReports.InputId)
		{
			return BluetoothReports.InputCrcValid(report) ? Parse(InputState.Body(report)) : null;
		}
		if (report[0] == UsbReports.InputId)
		{
			if (bluetooth)
			{
				return report.Length >= SimpleLength ? ParseSimple(report) : null;
			}
			return report.Length >= UsbReports.InputSize ? Parse(InputState.Body(report)) : null;
		}
		return null;
	}

	public static PadButtons DecodeButtons(byte b0, byte b1, byte b2)
	{
		PadButtons buttons = (b0 & 0x0F) switch
		{
			0 => PadButtons.DpadUp,
			1 => PadButtons.DpadUp | PadButtons.DpadRight,
			2 => PadButtons.DpadRight,
			3 => PadButtons.DpadDown | PadButtons.DpadRight,
			4 => PadButtons.DpadDown,
			5 => PadButtons.DpadDown | PadButtons.DpadLeft,
			6 => PadButtons.DpadLeft,
			7 => PadButtons.DpadUp | PadButtons.DpadLeft,
			_ => PadButtons.None
		};
		// buttons0's high nibble is Square..Triangle (bits 0..3), buttons1 is L1..R3 (bits 8..15).
		buttons |= (PadButtons)((uint)b0 >> 4);
		buttons |= (PadButtons)((uint)b1 << 8);
		PadButtons[] third = [PadButtons.PS, PadButtons.Touchpad, PadButtons.Mute, PadButtons.None,
			PadButtons.LeftFunction, PadButtons.RightFunction, PadButtons.LeftPaddle, PadButtons.RightPaddle];
		for (int i = 0; i < 8; i++)
		{
			if ((b2 & (1 << i)) != 0)
			{
				buttons |= third[i];
			}
		}
		return buttons;
	}

	// The first 9 body bytes (sticks, triggers, buttons1 for the L2/R2 bits) for InputTransform previews.
	public byte[] ToTransformBody() => [LeftX, LeftY, RightX, RightY, L2, R2, 0, 0x08, (byte)((uint)Buttons >> 8)];
}
