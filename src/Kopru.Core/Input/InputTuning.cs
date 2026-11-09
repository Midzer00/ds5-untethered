// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Input;

public enum ResponseCurve
{
	Linear,
	// Finer control near the center (exponent 2).
	Smooth,
	// Faster response near the center (exponent 0.5).
	Aggressive,
	// Uses StickTuning.Exponent.
	Custom
}

// Per-stick settings. Percentages are 0-100 of the stick's radius; drift offsets are raw units (-127..127) subtracted
// from the reading. Defaults change nothing.
public sealed record StickTuning
{
	public static StickTuning Default { get; } = new();

	public double InnerDeadzone { get; init; }

	public double OuterDeadzone { get; init; }

	public double AntiDeadzone { get; init; }

	public ResponseCurve Curve { get; init; } = ResponseCurve.Linear;

	public double Exponent { get; init; } = 1.0;

	public double DriftX { get; init; }

	public double DriftY { get; init; }

	public double EffectiveExponent => Curve switch
	{
		ResponseCurve.Smooth => 2.0,
		ResponseCurve.Aggressive => 0.5,
		ResponseCurve.Custom => Math.Clamp(Exponent, 0.1, 10.0),
		_ => 1.0
	};

	public bool IsIdentity => InnerDeadzone <= 0 && OuterDeadzone <= 0 && AntiDeadzone <= 0 && EffectiveExponent == 1.0
		&& DriftX == 0 && DriftY == 0;
}

// Per-trigger settings: analog values below Start % read as 0, values at or above End % read as full.
public sealed record TriggerTuning
{
	public static TriggerTuning Default { get; } = new();

	public double Start { get; init; }

	public double End { get; init; } = 100.0;

	public bool IsIdentity => Start <= 0 && End >= 100;
}

public sealed record InputTuning
{
	public static InputTuning Default { get; } = new();

	public StickTuning LeftStick { get; init; } = StickTuning.Default;

	public StickTuning RightStick { get; init; } = StickTuning.Default;

	public TriggerTuning L2 { get; init; } = TriggerTuning.Default;

	public TriggerTuning R2 { get; init; } = TriggerTuning.Default;

	public bool IsIdentity => LeftStick.IsIdentity && RightStick.IsIdentity && L2.IsIdentity && R2.IsIdentity;

	// Takes the drift offsets and suggested inner deadzones of a successful calibration; anything else is unchanged.
	public InputTuning WithCalibration(DriftCalibrationResult result) => !result.Success ? this : this with
	{
		LeftStick = LeftStick with
		{
			DriftX = result.LeftX, DriftY = result.LeftY,
			InnerDeadzone = Math.Max(LeftStick.InnerDeadzone, result.SuggestedLeftDeadzone)
		},
		RightStick = RightStick with
		{
			DriftX = result.RightX, DriftY = result.RightY,
			InnerDeadzone = Math.Max(RightStick.InnerDeadzone, result.SuggestedRightDeadzone)
		}
	};
}
