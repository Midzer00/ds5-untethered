// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the controller test page. Button names that are the same everywhere (L1, R2, PS...) are not
// translated and live in the view model.
public static partial class Text
{
	public static string TesterSourceBridge(Language? l = null) => T(l, "Tester.SourceBridge");

	public static string TesterSourceDirect(Language? l = null) => T(l, "Tester.SourceDirect");

	public static string TesterNoInput(Language? l = null) => T(l, "Tester.NoInput");

	public static string TesterNoInputHint(Language? l = null) => T(l, "Tester.NoInputHint");

	public static string TesterDirectHint(Language? l = null) => T(l, "Tester.DirectHint");

	public static string TesterLeftStick(Language? l = null) => T(l, "Tester.LeftStick");

	public static string TesterRightStick(Language? l = null) => T(l, "Tester.RightStick");

	public static string TesterStickHint(Language? l = null) => T(l, "Tester.StickHint");

	public static string TesterNoTouch(Language? l = null) => T(l, "Tester.NoTouch");

	public static string TesterMotion(Language? l = null) => T(l, "Tester.Motion");

	public static string TesterGyro(Language? l = null) => T(l, "Tester.Gyro");

	public static string TesterAccel(Language? l = null) => T(l, "Tester.Accel");

	public static string TesterMotionNeedsFull(Language? l = null) => T(l, "Tester.MotionNeedsFull");

	public static string TesterBattery(Language? l = null) => T(l, "Tester.Battery");

	public static string TesterConnection(Language? l = null) => T(l, "Tester.Connection");

	public static string TesterReportRate(Language? l = null) => T(l, "Tester.ReportRate");

	public static string TesterSource(Language? l = null) => T(l, "Tester.Source");

	public static string TesterOutputs(Language? l = null) => T(l, "Tester.Outputs");

	public static string TesterOutputsHint(Language? l = null) => T(l, "Tester.OutputsHint");

	public static string TesterHaptics(Language? l = null) => T(l, "Tester.Haptics");

	public static string TesterLeft(Language? l = null) => T(l, "Tester.Left");

	public static string TesterRight(Language? l = null) => T(l, "Tester.Right");

	public static string TesterTriggerEffects(Language? l = null) => T(l, "Tester.TriggerEffects");

	public static string TesterLightColour(Language? l = null) => T(l, "Tester.LightColour");

	// Two lines on purpose (the card is narrow; a free wrap leaves one word alone on the second line).
	public static string TesterCalibrationHint(Language? l = null) => T(l, "Tester.CalibrationHint");

	public static string TesterSquare(Language? l = null) => T(l, "Tester.Square");

	public static string TesterCross(Language? l = null) => T(l, "Tester.Cross");

	public static string TesterCircle(Language? l = null) => T(l, "Tester.Circle");

	public static string TesterTriangle(Language? l = null) => T(l, "Tester.Triangle");

	public static string TesterUp(Language? l = null) => T(l, "Tester.Up");

	public static string TesterDown(Language? l = null) => T(l, "Tester.Down");

	public static string TesterCreate(Language? l = null) => T(l, "Tester.Create");

	public static string TesterOptions(Language? l = null) => T(l, "Tester.Options");

	public static string TesterTouchClick(Language? l = null) => T(l, "Tester.TouchClick");

	public static string TesterMute(Language? l = null) => T(l, "Tester.Mute");

	public static string TesterLeftFn(Language? l = null) => T(l, "Tester.LeftFn");

	public static string TesterRightFn(Language? l = null) => T(l, "Tester.RightFn");

	public static string TesterLeftPaddle(Language? l = null) => T(l, "Tester.LeftPaddle");

	public static string TesterRightPaddle(Language? l = null) => T(l, "Tester.RightPaddle");

}
