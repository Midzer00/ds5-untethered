// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the Overview's quick settings card and Low Power mode.
public static partial class Text
{
	public static string QuickSettingsTitle(Language? l = null) => T(l, "Quick.SettingsTitle");

	public static string LowPower(Language? l = null) => T(l, "Low.Power");

	public static string LowPowerNote(Language? l = null) => T(l, "Low.PowerNote");

	// Why the game's light, the speaker and the microphone do nothing while Low Power is on.
	public static string LowPowerOffNote(Language? l = null) => T(l, "Low.PowerOffNote");

	public static string HapticsStrength(Language? l = null) => T(l, "Haptics.Strength");

	public static string QuickLight(Language? l = null) => T(l, "Quick.Light");

	public static string QuickMicrophone(Language? l = null) => T(l, "Quick.Microphone");

	public static string StepLight(Language? l = null) => T(l, "Step.Light");

	public static string StepNormal(Language? l = null) => T(l, "Step.Normal");

	public static string StepStrong(Language? l = null) => T(l, "Step.Strong");

	public static string StepOff(Language? l = null) => T(l, "Step.Off");

	public static string StepLow(Language? l = null) => T(l, "Step.Low");

	public static string StepHigh(Language? l = null) => T(l, "Step.High");

	// A value between the steps.
	public static string QuickCustom(double percent, Language? l = null) => T(l, "Quick.Custom", A("percent", percent, "F0"));

	public static string AdvancedSettings(Language? l = null) => T(l, "Advanced.Settings");

	public static string AppliedWhenConnected(Language? l = null) => T(l, "Applied.WhenConnected");

	public static string AutoLowPower(Language? l = null) => T(l, "Auto.LowPower");

	public static string AutoLowPowerHint(Language? l = null) => T(l, "Auto.LowPowerHint");

	// Toggle tiles: desktop mode under the name gamers look for, and the state word under each tile's label.
	public static string QuickMouseMode(Language? l = null) => T(l, "Quick.MouseMode");

	public static string QuickStateOn(Language? l = null) => T(l, "Quick.StateOn");

	public static string QuickStateOff(Language? l = null) => T(l, "Quick.StateOff");

	public static string QuickLowPowerShort(Language? l = null) => T(l, "Quick.LowPowerShort");

	// One plain sentence per quick setting (the tooltips of the card).
	public static string QuickLowPowerTip(Language? l = null) => T(l, "Quick.LowPowerTip");

	// The Speaker tile's state word when it cannot play: the type games see has no speaker, or Low Power holds it off.
	public static string QuickSpeakerDualSenseOnly(Language? l = null) => T(l, "Quick.SpeakerDualSenseOnly");

	public static string QuickSpeakerOffLowPower(Language? l = null) => T(l, "Quick.SpeakerOffLowPower");

	public static string QuickMouseModeTip(Language? l = null) => T(l, "Quick.MouseModeTip");

	public static string QuickSpeakerTip(Language? l = null) => T(l, "Quick.SpeakerTip");

	public static string QuickMicTip(Language? l = null) => T(l, "Quick.MicTip");

	public static string QuickHapticsTip(Language? l = null) => T(l, "Quick.HapticsTip");

	public static string QuickTriggerTip(Language? l = null) => T(l, "Quick.TriggerTip");

	public static string QuickLightTip(Language? l = null) => T(l, "Quick.LightTip");

	public static string QuickProfileTip(Language? l = null) => T(l, "Quick.ProfileTip");

	// Added to the tooltip of a setting Low Power overrides while it is on.
	public static string QuickOverriddenByLowPower(Language? l = null) => T(l, "Quick.OverriddenByLowPower");

	// The Overview's live meters: the two haptic motors and the triggers.
	public static string LiveLeftHapticsTip(Language? l = null) => T(l, "Live.LeftHapticsTip");

	public static string LiveRightHapticsTip(Language? l = null) => T(l, "Live.RightHapticsTip");

	public static string LiveTriggerTip(Language? l = null) => T(l, "Live.TriggerTip");

	// Tooltip of the battery reading in the Overview's hero.
	public static string HeroBatteryTip(Language? l = null) => T(l, "Hero.BatteryTip");

	public static string HeroBatteryChargingTip(Language? l = null) => T(l, "Hero.BatteryChargingTip");

	// The text link at the end of the health line that runs the fix.
	public static string HealthFixLink(Language? l = null) => T(l, "Health.FixLink");
}
