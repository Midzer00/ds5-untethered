// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;
using Kopru.Core.Protocol;

namespace Kopru.Core.Tests;

public class InputTransformTests
{
	private static byte[] Body(byte lx = 128, byte ly = 128, byte rx = 128, byte ry = 128, byte l2 = 0, byte r2 = 0)
	{
		var body = new byte[InputState.BodySize];
		for (int i = 0; i < body.Length; i++)
			body[i] = (byte)(i * 37 + 11);
		body[0] = lx; body[1] = ly; body[2] = rx; body[3] = ry; body[4] = l2; body[5] = r2;
		return body;
	}

	private static byte[] Run(InputTuning tuning, byte[] body)
	{
		var copy = (byte[])body.Clone();
		new InputTransform(tuning).Apply(copy);
		return copy;
	}

	private static InputTuning Left(StickTuning s) => InputTuning.Default with { LeftStick = s };

	[Fact]
	public void IdentityLeavesEveryByteUnchanged()
	{
		var rng = new Random(1);
		for (int n = 0; n < 200; n++)
		{
			var body = new byte[InputState.BodySize];
			rng.NextBytes(body);
			Assert.Equal(body, Run(InputTuning.Default, body));
		}
	}

	[Fact]
	public void RadialInnerDeadzoneZeroesSmallDeflection()
	{
		var t = Left(new StickTuning { InnerDeadzone = 10 });
		var outp = Run(t, Body(lx: 128 + 8, ly: 128 + 8));
		Assert.Equal(128, outp[0]);
		Assert.Equal(128, outp[1]);
		// Radial, not per axis: a diagonal past the radius moves both axes even if each is inside on its own.
		outp = Run(t, Body(lx: 128 + 11, ly: 128 + 11));
		Assert.NotEqual(128, outp[0]);
		Assert.NotEqual(128, outp[1]);
	}

	[Fact]
	public void RescalesAfterDeadzone()
	{
		var outp = Run(Left(new StickTuning { InnerDeadzone = 20 }), Body(lx: 255));
		Assert.Equal(255, outp[0]);
		Assert.Equal(128, outp[1]);
	}

	[Fact]
	public void OuterDeadzoneSaturates()
	{
		var outp = Run(Left(new StickTuning { OuterDeadzone = 20 }), Body(lx: 128 + 105));
		Assert.Equal(255, outp[0]);
		outp = Run(Left(new StickTuning { OuterDeadzone = 20 }), Body(lx: 0));
		Assert.Equal(0, outp[0]);
	}

	[Fact]
	public void AntiDeadzoneLiftsSmallInput()
	{
		var outp = Run(Left(new StickTuning { AntiDeadzone = 20 }), Body(lx: 130));
		Assert.True(outp[0] >= 128 + 25, $"got {outp[0]}");
	}

	[Theory]
	[InlineData(ResponseCurve.Linear)]
	[InlineData(ResponseCurve.Smooth)]
	[InlineData(ResponseCurve.Aggressive)]
	public void CurvesAreMonotonic(ResponseCurve curve)
	{
		var transform = new InputTransform(Left(new StickTuning { Curve = curve, InnerDeadzone = 5 }));
		int previous = -1;
		for (int x = 0; x < 256; x++)
		{
			var body = Body(lx: (byte)x);
			transform.Apply(body);
			Assert.True(body[0] >= previous, $"{curve} at {x}");
			previous = body[0];
		}
		Assert.Equal(255, previous);
	}

	[Fact]
	public void SmoothCurveIsSlowerNearCenter()
	{
		var lin = Run(Left(new StickTuning { Curve = ResponseCurve.Linear, InnerDeadzone = 1 }), Body(lx: 170));
		var smooth = Run(Left(new StickTuning { Curve = ResponseCurve.Smooth }), Body(lx: 170));
		var aggr = Run(Left(new StickTuning { Curve = ResponseCurve.Aggressive }), Body(lx: 170));
		Assert.True(smooth[0] < lin[0] && lin[0] < aggr[0]);
	}

	[Fact]
	public void DriftOffsetRecenters()
	{
		var outp = Run(Left(new StickTuning { DriftX = 6, DriftY = -4 }), Body(lx: 134, ly: 124));
		Assert.InRange(outp[0], 127, 128);
		Assert.InRange(outp[1], 127, 128);
	}

	[Fact]
	public void TriggerRangeAndDigitalBit()
	{
		var t = InputTuning.Default with { L2 = new TriggerTuning { Start = 10, End = 50 } };
		var transform = new InputTransform(t);
		for (int v = 0; v < 256; v++)
		{
			var body = Body(l2: (byte)v);
			body[InputTransform.Buttons1] = (byte)(v % 2 == 0 ? InputTransform.L2Bit : 0);
			transform.Apply(body);
			if (v <= 25) Assert.Equal(0, body[4]);
			if (v >= 128) Assert.Equal(255, body[4]);
			Assert.Equal(body[4] > 0, (body[InputTransform.Buttons1] & InputTransform.L2Bit) != 0);
		}
	}

	[Fact]
	public void TuningTouchesOnlyStickAndTriggerBytes()
	{
		var t = new InputTuning
		{
			LeftStick = new StickTuning { InnerDeadzone = 15, Curve = ResponseCurve.Smooth, DriftX = 3 },
			RightStick = new StickTuning { OuterDeadzone = 10, AntiDeadzone = 5 },
			L2 = new TriggerTuning { Start = 20 },
			R2 = new TriggerTuning { End = 60 }
		};
		var body = Body(140, 90, 200, 30, 100, 100);
		var outp = Run(t, body);
		for (int i = 6; i < body.Length; i++)
		{
			if (i == InputTransform.Buttons1)
				Assert.Equal(body[i] & ~0x0C, outp[i] & ~0x0C);
			else
				Assert.Equal(body[i], outp[i]);
		}
	}
}

public class DriftCalibratorTests
{
	private static byte[] Body(byte lx, byte ly, byte rx, byte ry)
	{
		var body = new byte[InputState.BodySize];
		body[0] = lx; body[1] = ly; body[2] = rx; body[3] = ry;
		return body;
	}

	[Fact]
	public void StillSticksGiveOffsets()
	{
		var cal = new DriftCalibrator(TimeSpan.FromSeconds(2));
		for (int i = 0; i < 2000 && !cal.IsComplete; i++)
			cal.Add(Body((byte)(134 + i % 3), 124, 128, (byte)(127 + i % 2)), TimeSpan.FromMilliseconds(i * 1.33));
		Assert.True(cal.IsComplete);
		var r = cal.Finish();
		Assert.True(r.Success);
		Assert.InRange(r.LeftX, 6, 8);
		Assert.InRange(r.LeftY, -4, -3);
		Assert.InRange(r.RightX, 0, 1);
		Assert.InRange(r.SuggestedLeftDeadzone, 2, 6);

		var tuned = InputTuning.Default.WithCalibration(r);
		var body = Body(135, 124, 128, 127);
		new InputTransform(tuned).Apply(body);
		Assert.Equal(128, body[0]);
		Assert.Equal(128, body[1]);
	}

	[Fact]
	public void MovedSticksAreRejected()
	{
		var cal = new DriftCalibrator(TimeSpan.FromSeconds(1));
		for (int i = 0; i < 800; i++)
			cal.Add(Body(128, 128, (byte)(i > 400 ? 200 : 128), 128), TimeSpan.FromMilliseconds(i * 1.33));
		var r = cal.Finish();
		Assert.Equal(DriftCalibrationStatus.SticksMoved, r.Status);
		Assert.Same(InputTuning.Default, InputTuning.Default.WithCalibration(r));
	}

	[Fact]
	public void TooFewSamplesAreRejected()
	{
		var cal = new DriftCalibrator(TimeSpan.FromSeconds(1));
		cal.Add(Body(128, 128, 128, 128), TimeSpan.FromSeconds(2));
		Assert.Equal(DriftCalibrationStatus.NotEnoughSamples, cal.Finish().Status);
	}
}
