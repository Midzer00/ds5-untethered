// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Kopru.Core.Protocol;

namespace Kopru.Core.Tests;

public class CrcTests
{
	[Fact]
	public void StandardCheckValue() => Assert.Equal(0xCBF43926u, Crc.Standard(Encoding.ASCII.GetBytes("123456789")));

	[Fact]
	public void OutputSeedAlone() => Assert.Equal(0xEADA2D49u, Crc.Compute(Crc.OutputSeed, []));

	[Fact]
	public void InputCrc_RoundTripAndCorruption()
	{
		byte[] report = new byte[78];
		report[0] = 0x31;
		report[8] = 7;
		BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), Crc.Compute(Crc.InputSeed, report.AsSpan(0, 74)));
		Assert.True(BluetoothReports.InputCrcValid(report));
		report[10] ^= 1;
		Assert.False(BluetoothReports.InputCrcValid(report));
		Assert.False(BluetoothReports.InputCrcValid(new byte[10]));
	}
}

public class OutputReportTests
{
	[Fact]
	public void Bluetooth_LayoutAndCrc()
	{
		byte[] block = Enumerable.Range(1, 47).Select(i => (byte)i).ToArray();
		byte[] report = new byte[78];

		BluetoothReports.BuildOutput(block, 5, report);

		Assert.Equal((0x31, 0x50, 0x10), (report[0], report[1], report[2]));
		Assert.Equal(block, report[3..50]);
		Assert.All(report[50..74], b => Assert.Equal(0, b));
		Assert.Equal(Crc.Compute(Crc.OutputSeed, report.AsSpan(0, 74)), BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(74)));
	}

	[Fact]
	public void Bluetooth_PaddedToWindowsLength_KeepsTheSameReport()
	{
		byte[] block = new byte[47];
		OutputBlock.SetLightbar(block, 1, 2, 3);
		byte[] shortReport = new byte[78];
		byte[] padded = new byte[547];

		BluetoothReports.BuildOutput(block, 3, shortReport);
		BluetoothReports.BuildOutput(block, 3, padded);

		Assert.Equal(shortReport, padded[..78]);
		Assert.All(padded[78..], b => Assert.Equal(0, b));
	}

	[Fact]
	public void Usb_Layout()
	{
		byte[] block = new byte[47];
		OutputBlock.SetTriggers(block, TriggerEffects.Feedback(2, 8), TriggerEffects.Off());
		byte[] report = new byte[48];

		UsbReports.BuildOutput(block, report);

		Assert.Equal(0x02, report[0]);
		Assert.Equal(TriggerEffects.Feedback(2, 8), report[11..22]);
	}

	[Fact]
	public void HapticsMode_ClearsRumbleBits_AndTurnsOnTheThreeControls()
	{
		byte[] block = new byte[47];
		OutputBlock.SetRumble(block, 100, 50);

		OutputBlock.SetHapticsMode(block);

		Assert.Equal(0, block[0] & 0x03);
		Assert.Equal(0x62, block[1] & 0x62);
		Assert.Equal((0, 0), (block[OutputBlock.PowerSave], block[OutputBlock.MotorPowerReduction]));
	}

	[Fact]
	public void Rumble_LargeLeftSmallRight()
	{
		byte[] block = new byte[47];
		OutputBlock.SetRumble(block, 200, 30);
		Assert.Equal((0x03, 30, 200, 0x04), (block[0], block[2], block[3], block[38] & 0x04));
	}
}

public class HapticsReportTests
{
	private static sbyte[] Frames(Func<int, int> f) => Enumerable.Range(0, 32).Select(i => (sbyte)f(i)).ToArray();

	[Fact]
	public void Layout()
	{
		byte[] report = new byte[142];

		BluetoothReports.BuildHaptics(Frames(i => i), Frames(i => -i), 9, 42, 0x20, report);

		Assert.Equal(new byte[] { 0x32, 0x90, 0x91, 0x07, 0xFE, 0, 0, 0, 0, 0x20, 42, 0x92, 0x40 }, report[..13]);
		Assert.Equal((5, unchecked((byte)-5)), (report[13 + 10], report[13 + 11]));
		Assert.All(report[77..138], b => Assert.Equal(0, b));
		Assert.Equal(Crc.Compute(Crc.OutputSeed, report.AsSpan(0, 138)), BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(138)));
	}

	[Fact]
	public void WrongFrameCount_Throws() =>
		Assert.Throws<ArgumentException>(() => BluetoothReports.BuildHaptics(new sbyte[31], new sbyte[32], 0, 0, 0x20, new byte[142]));

	[Fact]
	public void IntervalIs10_67ms() => Assert.Equal(10.667, BluetoothReports.HapticsInterval.TotalMilliseconds, 3);
}

public class TriggerEffectTests
{
	private static int[] ZoneValues(byte[] e)
	{
		uint packed = BinaryPrimitives.ReadUInt32LittleEndian(e.AsSpan(3));
		return Enumerable.Range(0, 10).Select(z => (int)((packed >> (3 * z)) & 7)).ToArray();
	}

	[Fact]
	public void Feedback()
	{
		byte[] e = TriggerEffects.Feedback(2, 8);
		Assert.Equal(11, e.Length);
		Assert.Equal(0x21, e[0]);
		Assert.Equal(0b1111111100, e[1] | (e[2] << 8));
		Assert.Equal(new[] { 0, 0, 7, 7, 7, 7, 7, 7, 7, 7 }, ZoneValues(e));
		Assert.Equal(TriggerEffects.Off(), TriggerEffects.Feedback(3, 0));
	}

	[Fact]
	public void Weapon()
	{
		byte[] e = TriggerEffects.Weapon(2, 6, 8);
		Assert.Equal((0x25, (1 << 2) | (1 << 6), 7), (e[0], e[1] | (e[2] << 8), e[3]));
		Assert.Throws<ArgumentOutOfRangeException>(() => TriggerEffects.Weapon(5, 5, 4));
	}

	[Fact]
	public void Vibration()
	{
		byte[] e = TriggerEffects.Vibration(0, 5, 40);
		Assert.Equal((0x26, 0x3FF, 40), (e[0], e[1] | (e[2] << 8), e[9]));
		Assert.All(ZoneValues(e), v => Assert.Equal(4, v));
	}

	[Fact]
	public void RangeChecks()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => TriggerEffects.Feedback(10, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => TriggerEffects.Feedback(0, 9));
	}
}

public class InputTests
{
	[Fact]
	public void UsbAndBluetoothBodiesParseAlike()
	{
		byte[] body = new byte[63];
		(body[4], body[5], body[6]) = (11, 22, 33);
		BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(27), 987654);
		body[52] = 0x18;
		byte[] usb = [0x01, .. body];
		byte[] bt = new byte[78];
		bt[0] = 0x31;
		body.CopyTo(bt, 2);

		InputState fromUsb = InputState.Parse(InputState.Body(usb));
		InputState fromBt = InputState.Parse(InputState.Body(bt));

		Assert.Equal(fromUsb, fromBt);
		Assert.Equal((11, 22, 33, 987654u, 80, 1), (fromUsb.L2, fromUsb.R2, fromUsb.Sequence, fromUsb.SensorTicks, fromUsb.BatteryLevel, fromUsb.ChargingState));
		Assert.True(InputState.Body(new byte[10]).IsEmpty);
	}
}
