// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;

namespace Kopru.Core.Localization;

// User-facing texts of stick and trigger tuning.
public static partial class Text
{
	public static string CalibrationKeepStill(Language? l = null) => Pick(l,
		$"Çubuklara dokunmayın, kalibrasyon sürüyor…",
		$"Don't touch the sticks, calibrating…");

	public static string CalibrationDone(Language? l = null) => Pick(l,
		$"Kalibrasyon tamamlandı.",
		$"Calibration complete.");

	public static string CalibrationSticksMoved(Language? l = null) => Pick(l,
		$"Çubuklar hareket etti. Dokunmadan tekrar deneyin.",
		$"The sticks moved. Try again without touching them.");

	public static string CalibrationNotEnoughData(Language? l = null) => Pick(l,
		$"Yeterli veri gelmedi. Kolun bağlı olduğundan emin olup tekrar deneyin.",
		$"Not enough data arrived. Make sure the controller is connected and try again.");

	public static string CurveName(ResponseCurve curve, Language? l = null) => curve switch
	{
		ResponseCurve.Smooth => Pick(l, $"Yumuşak", $"Smooth"),
		ResponseCurve.Aggressive => Pick(l, $"Agresif", $"Aggressive"),
		ResponseCurve.Custom => Pick(l, $"Özel", $"Custom"),
		_ => Pick(l, $"Doğrusal", $"Linear")
	};

	public static string InnerDeadzoneLabel(Language? l = null) => Pick(l, $"İç Ölü Bölge", $"Inner Deadzone");

	public static string OuterDeadzoneLabel(Language? l = null) => Pick(l, $"Dış Ölü Bölge", $"Outer Deadzone");

	public static string AntiDeadzoneLabel(Language? l = null) => Pick(l, $"Ölü Bölge Telafisi", $"Anti-Deadzone");

	public static string ResponseCurveLabel(Language? l = null) => Pick(l, $"Tepki Eğrisi", $"Response Curve");

	public static string TriggerStartLabel(Language? l = null) => Pick(l, $"Tetik Başlangıcı", $"Trigger Start");

	public static string TriggerEndLabel(Language? l = null) => Pick(l, $"Tetik Sonu", $"Trigger End");
}
