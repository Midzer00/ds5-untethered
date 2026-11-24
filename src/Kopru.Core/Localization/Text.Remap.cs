// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Input;

namespace Kopru.Core.Localization;

// User-facing texts of button remapping and gyro aiming.
public static partial class Text
{
	public static string RemapTab(Language? l = null) => T(l, "Remap.Tab");

	public static string RemapIntro(Language? l = null) => T(l, "Remap.Intro");

	public static string ButtonRemapping(Language? l = null) => T(l, "Button.Remapping");

	public static string ButtonRemappingHint(Language? l = null) => T(l, "Button.RemappingHint");

	public static string PhysicalButton(Language? l = null) => T(l, "Physical.Button");

	public static string TargetButton(Language? l = null) => T(l, "Target.Button");

	public static string ButtonName(DsButton button, Language? l = null) => button switch
	{
		DsButton.Cross => T(l, "Button.Name.Cross"),
		DsButton.Circle => T(l, "Button.Name.Circle"),
		DsButton.Square => T(l, "Button.Name.Square"),
		DsButton.Triangle => T(l, "Button.Name.Triangle"),
		DsButton.DpadUp => T(l, "Button.Name.DpadUp"),
		DsButton.DpadDown => T(l, "Button.Name.DpadDown"),
		DsButton.DpadLeft => T(l, "Button.Name.DpadLeft"),
		DsButton.DpadRight => T(l, "Button.Name.DpadRight"),
		DsButton.L1 => "L1",
		DsButton.R1 => "R1",
		DsButton.L2 => "L2",
		DsButton.R2 => "R2",
		DsButton.L3 => "L3",
		DsButton.R3 => "R3",
		DsButton.Create => T(l, "Button.Name.Create"),
		DsButton.Options => T(l, "Button.Name.Options"),
		DsButton.Touchpad => T(l, "Button.Name.Touchpad"),
		DsButton.Mute => T(l, "Button.Name.Mute"),
		DsButton.LeftPaddle => EdgeLeftPaddle(l),
		DsButton.RightPaddle => EdgeRightPaddle(l),
		_ => T(l, "Button.Name.None")
	};

	public static string StickSwapInvert(Language? l = null) => T(l, "Stick.SwapInvert");

	public static string SwapSticks(Language? l = null) => T(l, "Swap.Sticks");

	public static string InvertLeftX(Language? l = null) => T(l, "Invert.LeftX");

	public static string InvertLeftY(Language? l = null) => T(l, "Invert.LeftY");

	public static string InvertRightX(Language? l = null) => T(l, "Invert.RightX");

	public static string InvertRightY(Language? l = null) => T(l, "Invert.RightY");

	public static string GyroAiming(Language? l = null) => T(l, "Gyro.Aiming");

	public static string GyroAimingHint(Language? l = null) => T(l, "Gyro.AimingHint");

	public static string GyroModeLabel(Language? l = null) => T(l, "Gyro.ModeLabel");

	public static string GyroModeName(GyroMode mode, Language? l = null) => mode switch
	{
		GyroMode.Always => T(l, "Gyro.ModeName.Always"),
		GyroMode.WhileL2 => T(l, "Gyro.ModeName.WhileL2"),
		GyroMode.WhileR1 => T(l, "Gyro.ModeName.WhileR1"),
		_ => T(l, "Gyro.ModeName.Off")
	};

	public static string GyroToggle(Language? l = null) => T(l, "Gyro.Toggle");

	public static string GyroToggleHint(Language? l = null) => T(l, "Gyro.ToggleHint");

	public static string GyroSensitivityX(Language? l = null) => T(l, "Gyro.SensitivityX");

	public static string GyroSensitivityY(Language? l = null) => T(l, "Gyro.SensitivityY");

	public static string GyroInvertX(Language? l = null) => T(l, "Gyro.InvertX");

	public static string GyroInvertY(Language? l = null) => T(l, "Gyro.InvertY");

	public static string GyroSmoothing(Language? l = null) => T(l, "Gyro.Smoothing");

	public static string GyroDeadzone(Language? l = null) => T(l, "Gyro.Deadzone");

	public static string MultiplierValue(double value, Language? l = null) => T(l, "Multiplier.Value", A("value", value, "F1"));

	public static string DegreesPerSecond(double value, Language? l = null) => T(l, "Degrees.PerSecond", A("value", value, "F1"));

	public static string GyroOffset(Language? l = null) => T(l, "Gyro.Offset");

	public static string GyroOffsetHint(Language? l = null) => T(l, "Gyro.OffsetHint");

	public static string GyroCalibration(Language? l = null) => T(l, "Gyro.Calibration");

	public static string GyroCalibrationHint(Language? l = null) => T(l, "Gyro.CalibrationHint");

	public static string GyroCalibrationKeepStill(Language? l = null) => T(l, "Gyro.CalibrationKeepStill");

	public static string GyroCalibrationResultText(GyroCalibrationStatus status, Language? l = null) => status switch
	{
		GyroCalibrationStatus.Success => T(l, "Gyro.CalibrationResultText.Success"),
		GyroCalibrationStatus.Moved => T(l, "Gyro.CalibrationResultText.Moved"),
		_ => T(l, "Gyro.CalibrationResultText.NoData")
	};
}
