// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Kopru.Core.Protocol;

// The 47-byte output block shared by the USB report (0x02) and the Bluetooth report (0x31).
public static class OutputBlock
{
	public const int Size = 47;

	public const int Flag0 = 0;
	public const int Flag1 = 1;
	public const int MotorRight = 2;
	public const int MotorLeft = 3;
	public const int MuteLed = 8;
	public const int PowerSave = 9;
	public const int RightTrigger = 10;
	public const int LeftTrigger = 21;
	public const int MotorPowerReduction = 36;
	public const int Flag2 = 38;
	public const int LightbarSetup = 41;
	public const int LedBrightness = 42;
	public const int PlayerLeds = 43;
	public const int Red = 44;

	// Flag0
	public const byte CompatibleVibration = 0x01;
	public const byte HapticsSelect = 0x02;
	public const byte RightTriggerEffect = 0x04;
	public const byte LeftTriggerEffect = 0x08;

	// Flag1
	public const byte MuteLedControl = 0x01;
	public const byte PowerSaveControl = 0x02;
	public const byte LightbarControl = 0x04;
	// "Release LEDs" (the Linux hid-playstation driver's valid_flag1 bit 3): hands the LEDs back to the controller's own
	// defaults, which would undo the mute LED the app shows.
	public const byte ReleaseLeds = 0x08;
	public const byte PlayerLedControl = 0x10;
	public const byte HapticsLowPass = 0x20;
	public const byte MotorPowerControl = 0x40;

	// Flag2
	public const byte LedBrightnessControl = 0x01;
	public const byte LightbarSetupControl = 0x02;
	public const byte ImprovedRumble = 0x04;

	public const byte LightOut = 0x02;

	// Player LED brightness (byte 42) and mute LED (byte 8) values; public DualSense output layout (the Linux
	// hid-playstation driver's dualsense_output_report_common struct names the bytes; flag2 bit 0 enabling byte 42 and
	// its 0 high / 1 medium / 2 low scale are the community-documented values).
	public const byte LedBrightnessHigh = 0;
	public const byte LedBrightnessMedium = 1;
	public const byte LedBrightnessLow = 2;
	public const byte MuteLedOff = 0;
	public const byte MuteLedOn = 1;

	// Haptics over Bluetooth (report 0x32) play only while report 0x31 leaves the rumble bits off and turns on power-save
	// control, the haptics low-pass filter and motor power reduction, all at 0 (found on hardware, 2026-11-09).
	public static void SetHapticsMode(Span<byte> block)
	{
		block[Flag0] &= unchecked((byte)~(CompatibleVibration | HapticsSelect));
		block[Flag1] |= PowerSaveControl | HapticsLowPass | MotorPowerControl;
		block[PowerSave] = 0;
		block[MotorPowerReduction] = 0;
	}

	public static void SetTriggers(Span<byte> block, ReadOnlySpan<byte> right, ReadOnlySpan<byte> left)
	{
		block[Flag0] |= RightTriggerEffect | LeftTriggerEffect;
		right.CopyTo(block[RightTrigger..]);
		left.CopyTo(block[LeftTrigger..]);
	}

	public static void SetLightbar(Span<byte> block, byte red, byte green, byte blue)
	{
		block[Flag1] |= LightbarControl;
		block[Red] = red;
		block[Red + 1] = green;
		block[Red + 2] = blue;
	}

	// Player LEDs: mask bits 0..4 left to right, with their brightness.
	public static void SetPlayerLeds(Span<byte> block, byte mask, byte brightness)
	{
		block[Flag1] |= PlayerLedControl;
		block[Flag2] |= LedBrightnessControl;
		block[PlayerLeds] = (byte)(mask & 0x1F);
		block[LedBrightness] = brightness;
	}

	public static void SetMuteLed(Span<byte> block, byte value)
	{
		block[Flag1] |= MuteLedControl;
		block[MuteLed] = value;
	}

	// Classic rumble: large is the left (heavy) motor, small the right (light) one.
	public static void SetRumble(Span<byte> block, byte large, byte small, bool improved = true)
	{
		block[Flag0] |= CompatibleVibration | HapticsSelect;
		block[MotorLeft] = large;
		block[MotorRight] = small;
		if (improved)
		{
			block[Flag2] |= ImprovedRumble;
		}
	}
}

public static class UsbReports
{
	public const byte InputId = 0x01;
	public const int InputSize = 64;
	public const byte OutputId = 0x02;
	public const int OutputSize = 48;

	public static void BuildOutput(ReadOnlySpan<byte> block, Span<byte> report)
	{
		report.Clear();
		report[0] = OutputId;
		block[..OutputBlock.Size].CopyTo(report[1..]);
	}
}

public static class BluetoothReports
{
	public const byte InputId = 0x31;
	public const int InputSize = 78;
	public const byte OutputId = 0x31;
	public const int OutputSize = 78;
	public const byte Tag = 0x10;

	public const byte HapticsId = 0x32;
	public const int HapticsSize = 142;
	public const int HapticsFrames = 32;
	public const int HapticsSampleRate = 3000;
	public const int HapticsSamplesOffset = 13;
	public const byte DefaultBufferDepth = 0x20;
	public static readonly TimeSpan HapticsInterval = TimeSpan.FromSeconds((double)HapticsFrames / HapticsSampleRate);

	// report.Length may be larger than 78 (Windows writes at the device's full output length); the rest stays zero.
	public static void BuildOutput(ReadOnlySpan<byte> block, byte sequence, Span<byte> report)
	{
		report.Clear();
		report[0] = OutputId;
		report[1] = (byte)((sequence & 0x0F) << 4);
		report[2] = Tag;
		block[..OutputBlock.Size].CopyTo(report[3..]);
		BinaryPrimitives.WriteUInt32LittleEndian(report[74..], Crc.Compute(Crc.OutputSeed, report[..74]));
	}

	// One haptics report: a stream-control block (0x11) and 32 frames of signed 8-bit stereo at 3 kHz (block 0x12).
	public static void BuildHaptics(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right, byte sequence, byte counter,
		byte bufferDepth, Span<byte> report)
	{
		if (left.Length != HapticsFrames || right.Length != HapticsFrames)
		{
			throw new ArgumentException($"need {HapticsFrames} frames per side");
		}
		report.Clear();
		report[0] = HapticsId;
		report[1] = (byte)((sequence & 0x0F) << 4);
		report[2] = 0x91;
		report[3] = 0x07;
		report[4] = 0xFE;
		report[9] = bufferDepth;
		report[10] = counter;
		report[11] = 0x92;
		report[12] = HapticsFrames * 2;
		for (int i = 0; i < HapticsFrames; i++)
		{
			report[HapticsSamplesOffset + 2 * i] = (byte)left[i];
			report[HapticsSamplesOffset + 2 * i + 1] = (byte)right[i];
		}
		BinaryPrimitives.WriteUInt32LittleEndian(report[138..], Crc.Compute(Crc.OutputSeed, report[..138]));
	}

	public static bool InputCrcValid(ReadOnlySpan<byte> report) =>
		report.Length >= InputSize && report[0] == InputId
		&& BinaryPrimitives.ReadUInt32LittleEndian(report[74..]) == Crc.Compute(Crc.InputSeed, report[..74]);
}

// The 63-byte input body; it starts at byte 1 of USB report 0x01 and byte 2 of Bluetooth report 0x31.
public readonly record struct InputState(byte LeftX, byte LeftY, byte RightX, byte RightY, byte L2, byte R2, byte Sequence,
	uint SensorTicks, int BatteryLevel, int ChargingState)
{
	public const int BodySize = 63;

	public static InputState Parse(ReadOnlySpan<byte> body)
	{
		byte status = body[52];
		return new InputState(body[0], body[1], body[2], body[3], body[4], body[5], body[6],
			BinaryPrimitives.ReadUInt32LittleEndian(body[27..]), Math.Min(status & 0x0F, 10) * 10, status >> 4);
	}

	// The body of a USB (0x01) or Bluetooth (0x31) input report; empty for anything else.
	public static ReadOnlySpan<byte> Body(ReadOnlySpan<byte> report) => report switch
	{
		[UsbReports.InputId, ..] when report.Length >= UsbReports.InputSize => report.Slice(1, BodySize),
		[BluetoothReports.InputId, ..] when report.Length >= BluetoothReports.InputSize => report.Slice(2, BodySize),
		_ => []
	};
}
