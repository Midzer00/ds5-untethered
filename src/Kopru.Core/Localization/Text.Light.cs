// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the light effects, player LEDs and the mute LED.
public static partial class Text
{
	public static string LightModeLabel(Language? l = null) => T(l, "Light.ModeLabel");
	public static string LightModeStatic(Language? l = null) => T(l, "Light.ModeStatic");
	public static string LightModeBreathing(Language? l = null) => T(l, "Light.ModeBreathing");
	public static string LightModeRainbow(Language? l = null) => T(l, "Light.ModeRainbow");
	public static string LightModeBattery(Language? l = null) => T(l, "Light.ModeBattery");

	public static string LightModeStaticHint(Language? l = null) => T(l, "Light.ModeStaticHint");

	public static string LightModeBreathingHint(Language? l = null) => T(l, "Light.ModeBreathingHint");

	public static string LightModeRainbowHint(Language? l = null) => T(l, "Light.ModeRainbowHint");

	public static string LightModeBatteryHint(Language? l = null) => T(l, "Light.ModeBatteryHint");

	public static string BreathingPeriodLabel(Language? l = null) => T(l, "Breathing.PeriodLabel");
	public static string BreathingMinLabel(Language? l = null) => T(l, "Breathing.MinLabel");
	public static string BreathingMaxLabel(Language? l = null) => T(l, "Breathing.MaxLabel");
	public static string RainbowSpeedLabel(Language? l = null) => T(l, "Rainbow.SpeedLabel");
	public static string SecondsValue(double seconds, Language? l = null) => T(l, "Seconds.Value", A("seconds", seconds, "0.#"));
	public static string SpeedValue(double speed, Language? l = null) => T(l, "Speed.Value", A("speed", speed, "F0"));

	public static string PlayerLedsPatternHint(Language? l = null) => T(l, "Player.LedsPatternHint");

	public static string PlayerLedsOff(Language? l = null) => T(l, "Player.LedsOff");
	public static string PlayerLedsAll(Language? l = null) => T(l, "Player.LedsAll");
	public static string LedBrightnessLabel(Language? l = null) => T(l, "Led.BrightnessLabel");
	public static string LedBrightnessLow(Language? l = null) => T(l, "Led.BrightnessLow");
	public static string LedBrightnessMedium(Language? l = null) => T(l, "Led.BrightnessMedium");
	public static string LedBrightnessHigh(Language? l = null) => T(l, "Led.BrightnessHigh");

	// The Light page's group of the user's own light (below the Use The Game's Light switch).
	public static string LightOwnGroup(Language? l = null) => T(l, "Light.OwnGroup");

	// Why Use The Game's Light does nothing now (Low Power, or the Xbox 360 type).
	public static string LightOffLowPower(Language? l = null) => T(l, "Light.OffLowPower");
	public static string GameLightOffXbox(Language? l = null) => T(l, "Game.LightOffXbox");

	// The seventh colour swatch: a colour of the user's own, picked on a colour disc or typed as a hex code.
	public static string ColourCustom(Language? l = null) => T(l, "Colour.Custom");
	public static string ColourWheelLabel(Language? l = null) => T(l, "Colour.WheelLabel");
	public static string CustomColourHex(Language? l = null) => T(l, "Custom.ColourHex");
}
