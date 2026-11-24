// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Strings of the bridge service (state, errors, battery, idle).
public static partial class Text
{
	public static string BridgeStopped(Language? l = null) => T(l, "Bridge.Stopped");

	public static string BridgeWaiting(Language? l = null) => T(l, "Bridge.Waiting");

	public static string BridgeConnecting(Language? l = null) => T(l, "Bridge.Connecting");

	public static string BridgeRunning(Language? l = null) => T(l, "Bridge.Running");

	public static string BridgeError(Language? l = null) => T(l, "Bridge.Error");

	public static string BridgeNoCapture(Language? l = null) =>
		T(l, "Bridge.NoCapture");

	public static string BridgeCaptureInvalid(Language? l = null) =>
		T(l, "Bridge.CaptureInvalid");

	public static string BridgeUsbIpMissing(Language? l = null) =>
		T(l, "Bridge.UsbIpMissing");

	public static string BridgeAttachFailed(Language? l = null) =>
		T(l, "Bridge.AttachFailed");

	public static string BridgeControllerOpenFailed(Language? l = null) =>
		T(l, "Bridge.ControllerOpenFailed");

	public static string BridgeControllerLost(Language? l = null) =>
		T(l, "Bridge.ControllerLost");

	public static string BatteryPercent(double percent, Language? l = null) => T(l, "Battery.Percent", A("percent", percent, "F0"));

	public static string BatteryCharging(Language? l = null) => T(l, "Battery.Charging");

	public static string BatteryFull(Language? l = null) => T(l, "Battery.Full");

	public static string BatteryLow(double percent, Language? l = null) =>
		T(l, "Battery.Low", A("percent", percent, "F0"));

	public static string IdleTimeout(double minutes, Language? l = null) =>
		T(l, "Idle.Timeout", A("minutes", minutes, "F0"));

	public static string HidingUnavailable(Language? l = null) => T(l, "Hiding.Unavailable");

	public static string HidingFailed(string detail, Language? l = null) => T(l, "Hiding.Failed", A("detail", detail));
}
