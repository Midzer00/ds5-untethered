// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kopru.Core.Input;

namespace Kopru.Core.Tests;

public class RemapGyroTests
{
	private static byte[] Body(params DsButton[] pressed)
	{
		var b = new byte[63];
		b[0] = b[1] = b[2] = b[3] = 128;
		byte dirs = 0;
		foreach (DsButton p in pressed)
		{
			switch (p)
			{
				case DsButton.DpadUp: dirs |= 1; break;
				case DsButton.DpadRight: dirs |= 2; break;
				case DsButton.DpadDown: dirs |= 4; break;
				case DsButton.DpadLeft: dirs |= 8; break;
				default:
					(int off, byte bit) = ButtonRemap.Location(p);
					b[off] |= bit;
					if (p == DsButton.L2) b[4] = 255;
					if (p == DsButton.R2) b[5] = 255;
					break;
			}
		}
		int hat = dirs switch { 1 => 0, 3 => 1, 2 => 2, 6 => 3, 4 => 4, 12 => 5, 8 => 6, 9 => 7, _ => 8 };
		b[7] |= (byte)hat;
		return b;
	}

	private static byte[] WithGyro(byte[] body, short pitch, short yaw, short roll = 0)
	{
		BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(GyroAim.GyroX), pitch);
		BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(GyroAim.GyroY), yaw);
		BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(GyroAim.GyroZ), roll);
		return body;
	}

	private static List<DsButton> Pressed(byte[] body) => ButtonRemap.Physical.Where(b => ButtonRemap.IsPressed(body, b)).ToList();

	public static TheoryData<DsButton, DsButton> AllPairs()
	{
		var data = new TheoryData<DsButton, DsButton>();
		foreach (DsButton from in ButtonRemap.Physical)
		{
			data.Add(from, ButtonRemap.Physical[(ButtonRemap.Physical.ToList().IndexOf(from) + 5) % ButtonRemap.Physical.Count]);
			data.Add(from, DsButton.None);
		}
		return data;
	}

	[Theory]
	[MemberData(nameof(AllPairs))]
	public void EachButton_CanBeRemappedOrTurnedOff(DsButton from, DsButton to)
	{
		var remap = new ButtonRemap(RemapSettings.Default.With(from, to));
		byte[] body = Body(from);
		remap.Apply(body);
		Assert.Equal(to == DsButton.None ? [] : new List<DsButton> { to }, Pressed(body));
	}

	[Fact]
	public void Identity_LeavesTheBodyAlone_AndPsIsKept()
	{
		byte[] body = Body(DsButton.Cross, DsButton.DpadUp, DsButton.L2);
		body[9] |= 0x01; // PS
		byte[] before = (byte[])body.Clone();
		new ButtonRemap(RemapSettings.Default).Apply(body);
		Assert.Equal(before, body);

		var swap = new ButtonRemap(RemapSettings.Default.With(DsButton.Cross, DsButton.Circle));
		swap.Apply(body);
		Assert.Equal(0x01, body[9] & 0x01);
	}

	[Fact]
	public void SwappedButtons_Exchange()
	{
		var remap = new ButtonRemap(RemapSettings.Default.With(DsButton.Cross, DsButton.Circle).With(DsButton.Circle, DsButton.Cross));
		byte[] body = Body(DsButton.Cross);
		remap.Apply(body);
		Assert.Equal([DsButton.Circle], Pressed(body));
	}

	[Fact]
	public void DpadDirections_CombineIntoTheHat_AndOppositesCancel()
	{
		var remap = new ButtonRemap(RemapSettings.Default.With(DsButton.Cross, DsButton.DpadRight).With(DsButton.Circle, DsButton.DpadLeft));
		byte[] body = Body(DsButton.DpadUp, DsButton.Cross);
		remap.Apply(body);
		Assert.Equal(1, body[7] & 0x0F); // NE
		body = Body(DsButton.Cross, DsButton.Circle);
		remap.Apply(body);
		Assert.Equal(8, body[7] & 0x0F);
	}

	[Fact]
	public void Triggers_CarryTheirAnalogValue()
	{
		// L2 <-> R2: the analog value moves with it, even below the digital threshold.
		var remap = new ButtonRemap(RemapSettings.Default.With(DsButton.L2, DsButton.R2).With(DsButton.R2, DsButton.L2));
		byte[] body = Body();
		body[4] = 90;
		remap.Apply(body);
		Assert.Equal(0, body[4]);
		Assert.Equal(90, body[5]);

		// A trigger moved to a face button leaves 0; a face button moved onto a trigger is a full press.
		remap = new ButtonRemap(RemapSettings.Default.With(DsButton.L2, DsButton.Cross).With(DsButton.Square, DsButton.R2));
		body = Body(DsButton.L2, DsButton.Square);
		remap.Apply(body);
		Assert.Equal(0, body[4]);
		Assert.Equal(255, body[5]);
		Assert.Equal([DsButton.Cross, DsButton.R2], Pressed(body));
	}

	[Fact]
	public void Sticks_SwapAndInvert()
	{
		byte[] body = Body();
		body[0] = 10; body[1] = 20; body[2] = 200; body[3] = 220;
		new ButtonRemap(RemapSettings.Default with { SwapSticks = true }).Apply(body);
		Assert.Equal(new byte[] { 200, 220, 10, 20 }, body[..4]);
		new ButtonRemap(RemapSettings.Default with { InvertLeftX = true, InvertRightY = true }).Apply(body);
		Assert.Equal(new byte[] { 55, 220, 10, 235 }, body[..4]);
		new ButtonRemap(RemapSettings.Default with { InvertLeftY = true, InvertRightX = true }).Apply(body);
		Assert.Equal(new byte[] { 55, 35, 245, 235 }, body[..4]);
	}

	[Fact]
	public void RemapSettings_EqualityAndIdentity_FollowTheMapContents()
	{
		RemapSettings a = RemapSettings.Default.With(DsButton.L1, DsButton.R1);
		RemapSettings b = RemapSettings.Default.With(DsButton.L1, DsButton.R1);
		Assert.Equal(a, b);
		Assert.Equal(a.GetHashCode(), b.GetHashCode());
		Assert.NotEqual(RemapSettings.Default, a);
		Assert.True(a.With(DsButton.L1, DsButton.L1).IsIdentity);
		Assert.Equal(RemapSettings.Default, a.With(DsButton.L1, DsButton.L1));
	}

	// Composition: remap -> tuning -> gyro.

	[Fact]
	public void Remap_RunsBeforeTuning()
	{
		// Square -> L2 gives a full L2; the L2 range then still applies (start 50 %: 255 stays 255, digital bit set).
		var t = new InputTransform(InputTuning.Default with { L2 = new TriggerTuning { Start = 50 } },
			RemapSettings.Default.With(DsButton.Square, DsButton.L2));
		byte[] body = Body(DsButton.Square);
		t.Apply(body);
		Assert.Equal(255, body[4]);
		Assert.NotEqual(0, body[8] & InputTransform.L2Bit);

		// Swapped sticks: the left stick's deadzone acts on what the game sees as the left stick (the physical right).
		t = new InputTransform(InputTuning.Default with { LeftStick = new StickTuning { InnerDeadzone = 50 } },
			RemapSettings.Default with { SwapSticks = true });
		body = Body();
		body[0] = 150; body[2] = 160;
		t.Apply(body);
		Assert.Equal(128, body[0]);
		Assert.Equal(150, body[2]);
	}

	[Fact]
	public void Gyro_IsAddedAfterTuning_SoDeadzonesDoNotSwallowIt()
	{
		var gyro = new GyroSettings { Mode = GyroMode.Always, Deadzone = 0 };
		var t = new InputTransform(InputTuning.Default with { RightStick = new StickTuning { InnerDeadzone = 40 } }, null, gyro);
		// Small turn: 12 deg/s yaw to the left -> 0.1 of full scale -> stick moves left by ~13.
		byte[] body = WithGyro(Body(), 0, (short)(12 * GyroAim.CountsPerDegPerSec));
		t.Apply(body);
		Assert.InRange(body[2], 113, 116);
		Assert.Equal(128, body[3]);
	}

	[Fact]
	public void GyroActivation_UsesThePhysicalButton()
	{
		// R1 is remapped to Cross, but "while R1 held" still follows the physical R1.
		var t = new InputTransform(InputTuning.Default, RemapSettings.Default.With(DsButton.R1, DsButton.Cross),
			new GyroSettings { Mode = GyroMode.WhileR1, Deadzone = 0 });
		byte[] body = WithGyro(Body(DsButton.R1), 0, 2000);
		t.Apply(body);
		Assert.True(t.Gyro!.IsActive);
		Assert.True(body[2] < 128);
		Assert.Equal([DsButton.Cross], Pressed(body));
	}

	[Theory]
	[InlineData(GyroMode.Off, false, false)]
	[InlineData(GyroMode.Always, false, true)]
	[InlineData(GyroMode.WhileL2, false, false)]
	[InlineData(GyroMode.WhileL2, true, true)]
	[InlineData(GyroMode.WhileR1, false, false)]
	[InlineData(GyroMode.WhileR1, true, true)]
	public void GyroModes_HoldActivation(GyroMode mode, bool hold, bool active)
	{
		var aim = new GyroAim(new GyroSettings { Mode = mode });
		DsButton button = mode == GyroMode.WhileL2 ? DsButton.L2 : DsButton.R1;
		byte[] body = hold ? Body(button) : Body();
		aim.UpdateActivation(body);
		Assert.Equal(active, aim.IsActive);
	}

	[Fact]
	public void GyroToggle_FlipsOnEachPress()
	{
		var aim = new GyroAim(new GyroSettings { Mode = GyroMode.WhileL2, Toggle = true });
		aim.UpdateActivation(Body(DsButton.L2));
		Assert.True(aim.IsActive);
		aim.UpdateActivation(Body(DsButton.L2));
		Assert.True(aim.IsActive);
		aim.UpdateActivation(Body());
		Assert.True(aim.IsActive);
		aim.UpdateActivation(Body(DsButton.L2));
		Assert.False(aim.IsActive);
	}

	[Fact]
	public void GyroOffset_FollowsSensitivityInversionAndDeadzone()
	{
		short tenDeg = (short)Math.Round(10 * GyroAim.CountsPerDegPerSec);
		var aim = new GyroAim(new GyroSettings { Mode = GyroMode.Always, Deadzone = 0, SensitivityX = 2, SensitivityY = 1 });
		(double x, double y) = aim.RawOffset(WithGyro(Body(), tenDeg, tenDeg));
		Assert.Equal(-20.0 / GyroAim.FullScaleDegPerSec, x, 3);
		Assert.Equal(-10.0 / GyroAim.FullScaleDegPerSec, y, 3);

		aim = new GyroAim(new GyroSettings { Mode = GyroMode.Always, Deadzone = 0, InvertX = true, InvertY = true });
		(x, y) = aim.RawOffset(WithGyro(Body(), tenDeg, tenDeg));
		Assert.True(x > 0 && y > 0);

		// Below the deadzone nothing; above it the deadzone is subtracted.
		aim = new GyroAim(new GyroSettings { Mode = GyroMode.Always, Deadzone = 12 });
		Assert.Equal((0.0, 0.0), aim.RawOffset(WithGyro(Body(), tenDeg, tenDeg)));
		aim = new GyroAim(new GyroSettings { Mode = GyroMode.Always, Deadzone = 5 });
		Assert.Equal(-5.0 / GyroAim.FullScaleDegPerSec, aim.RawOffset(WithGyro(Body(), 0, tenDeg)).X, 3);
	}

	[Fact]
	public void GyroMix_AddsToTheRealStick_AndClamps()
	{
		var aim = new GyroAim(new GyroSettings { Mode = GyroMode.Always, Deadzone = 0, SensitivityX = 5 });
		byte[] body = WithGyro(Body(), 0, short.MinValue);
		body[2] = 250;
		aim.UpdateActivation(body);
		aim.Apply(body);
		Assert.Equal(255, body[2]);
		Assert.Equal(1, aim.OffsetX, 3);
	}

	[Fact]
	public void GyroSmoothing_ApproachesTheTarget()
	{
		var aim = new GyroAim(new GyroSettings { Mode = GyroMode.Always, Deadzone = 0, Smoothing = 50 });
		short rate = (short)(60 * GyroAim.CountsPerDegPerSec);
		double first = 0;
		for (int i = 0; i < 40; i++)
		{
			byte[] body = WithGyro(Body(), 0, rate);
			aim.UpdateActivation(body);
			aim.Apply(body);
			if (i == 0) first = aim.OffsetX;
		}
		Assert.InRange(first, -0.5 * 0.56, -0.5 * 0.5);
		Assert.Equal(-0.5, aim.OffsetX, 2);
	}

	[Fact]
	public void GyroBias_IsSubtracted()
	{
		var aim = new GyroAim(new GyroSettings { Mode = GyroMode.Always, Deadzone = 0, BiasX = 100, BiasY = -50 });
		Assert.Equal((0.0, 0.0), aim.RawOffset(WithGyro(Body(), 100, -50)));
	}

	[Fact]
	public void GyroCalibration_AveragesStillInput_AndRejectsMotion()
	{
		var cal = new GyroCalibrator(TimeSpan.FromSeconds(2));
		for (int i = 0; i < 100; i++)
			cal.Add(WithGyro(Body(), (short)(10 + i % 3), (short)(-20 - i % 2), 5), TimeSpan.FromMilliseconds(i * 21));
		Assert.True(cal.IsComplete);
		GyroCalibrationResult r = cal.Finish();
		Assert.True(r.Success);
		Assert.InRange(r.BiasX, 10, 12);
		Assert.InRange(r.BiasY, -21, -20);
		Assert.Equal(5, r.BiasZ);

		cal = new GyroCalibrator(TimeSpan.FromSeconds(2));
		for (int i = 0; i < 100; i++)
			cal.Add(WithGyro(Body(), (short)(i * 20), 0), TimeSpan.FromMilliseconds(i * 21));
		Assert.Equal(GyroCalibrationStatus.Moved, cal.Finish().Status);

		cal = new GyroCalibrator(TimeSpan.FromSeconds(2));
		cal.Add(new byte[9], TimeSpan.FromSeconds(3));
		Assert.Equal(GyroCalibrationStatus.NotEnoughSamples, cal.Finish().Status);
	}

	[Fact]
	public void Settings_RoundTripThroughJson()
	{
		var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
		RemapSettings remap = RemapSettings.Default.With(DsButton.Cross, DsButton.None).With(DsButton.L1, DsButton.R2) with { SwapSticks = true, InvertRightY = true };
		var gyro = new GyroSettings { Mode = GyroMode.WhileR1, Toggle = true, SensitivityX = 2.5, InvertY = true, Smoothing = 30, Deadzone = 2, BiasY = -3.5 };
		Assert.Equal(remap, JsonSerializer.Deserialize<RemapSettings>(JsonSerializer.Serialize(remap, options), options));
		Assert.Equal(gyro, JsonSerializer.Deserialize<GyroSettings>(JsonSerializer.Serialize(gyro, options), options));
	}

	[Fact]
	public void Apply_DoesNotAllocate()
	{
		var t = new InputTransform(InputTuning.Default with { RightStick = new StickTuning { InnerDeadzone = 5 } },
			RemapSettings.Default.With(DsButton.Cross, DsButton.Circle) with { SwapSticks = true },
			new GyroSettings { Mode = GyroMode.Always, Smoothing = 20 });
		byte[] body = WithGyro(Body(DsButton.Cross, DsButton.DpadLeft), 300, -400);
		t.Apply(body);
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 1000; i++)
			t.Apply(body);
		Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
	}
}
