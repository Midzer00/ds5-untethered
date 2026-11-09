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

	public static string InnerDeadzoneLabel(Language? l = null) => Pick(l, $"İç ölü bölge", $"Inner deadzone");

	public static string OuterDeadzoneLabel(Language? l = null) => Pick(l, $"Dış ölü bölge", $"Outer deadzone");

	public static string AntiDeadzoneLabel(Language? l = null) => Pick(l, $"Ölü bölge telafisi", $"Anti-deadzone");

	public static string ResponseCurveLabel(Language? l = null) => Pick(l, $"Tepki eğrisi", $"Response curve");

	public static string TriggerStartLabel(Language? l = null) => Pick(l, $"Tetik başlangıcı", $"Trigger start");

	public static string TriggerEndLabel(Language? l = null) => Pick(l, $"Tetik sonu", $"Trigger end");
}
