// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;

namespace Kopru.Core.Localization;

// User-facing texts of stick and trigger tuning.
public static partial class Text
{
	public static string CalibrationKeepStill(Language? l = null) => T(l, "Calibration.KeepStill");

	public static string CalibrationDone(Language? l = null) => T(l, "Calibration.Done");

	public static string CalibrationSticksMoved(Language? l = null) => T(l, "Calibration.SticksMoved");

	public static string CalibrationNotEnoughData(Language? l = null) => T(l, "Calibration.NotEnoughData");

	public static string CurveName(ResponseCurve curve, Language? l = null) => curve switch
	{
		ResponseCurve.Smooth => T(l, "Curve.Name.Smooth"),
		ResponseCurve.Aggressive => T(l, "Curve.Name.Aggressive"),
		ResponseCurve.Custom => T(l, "Curve.Name.Custom"),
		_ => T(l, "Curve.Name.Linear")
	};

	public static string InnerDeadzoneLabel(Language? l = null) => T(l, "Inner.DeadzoneLabel");

	public static string OuterDeadzoneLabel(Language? l = null) => T(l, "Outer.DeadzoneLabel");

	public static string AntiDeadzoneLabel(Language? l = null) => T(l, "Anti.DeadzoneLabel");

	public static string ResponseCurveLabel(Language? l = null) => T(l, "Response.CurveLabel");

	public static string TriggerStartLabel(Language? l = null) => T(l, "Trigger.StartLabel");

	public static string TriggerEndLabel(Language? l = null) => T(l, "Trigger.EndLabel");
}
