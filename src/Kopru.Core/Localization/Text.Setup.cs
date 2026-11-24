// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// First-run setup wizard texts.
public static partial class Text
{
	public static string SetupLanguageTitle(Language? l = null) => T(l, "Setup.LanguageTitle");
	public static string SetupLanguageInstructions(Language? l = null) => T(l, "Setup.LanguageInstructions");

	public static string SetupDriversTitle(Language? l = null) => T(l, "Setup.DriversTitle");
	public static string SetupDriversInstructions(Language? l = null) => T(l, "Setup.DriversInstructions");
	public static string SetupUsbIpMissing(Language? l = null) => T(l, "Setup.UsbIpMissing");
	public static string SetupHidHideMissing(Language? l = null) => T(l, "Setup.HidHideMissing");
	public static string SetupRestartPending(Language? l = null) => T(l, "Setup.RestartPending");

	public static string SetupPairTitle(Language? l = null) => T(l, "Setup.PairTitle");
	public static string SetupPairInstructions(Language? l = null) => T(l, "Setup.PairInstructions");
	public static string SetupRadioMissing(Language? l = null) => T(l, "Setup.RadioMissing");
	public static string SetupRadioOff(Language? l = null) => T(l, "Setup.RadioOff");
	public static string SetupControllerMissing(Language? l = null) => T(l, "Setup.ControllerMissing");
	public static string SetupControllerOnUsb(Language? l = null) => T(l, "Setup.ControllerOnUsb");
	public static string SetupEdgeConnected(Language? l = null) => T(l, "Setup.EdgeConnected");

	public static string SetupAdapterTitle(Language? l = null) => T(l, "Setup.AdapterTitle");
	public static string SetupAdapterInstructions(Language? l = null) => T(l, "Setup.AdapterInstructions");

	public static string SetupDoneTitle(Language? l = null) => T(l, "Setup.DoneTitle");
	public static string SetupDoneInstructions(Language? l = null) => T(l, "Setup.DoneInstructions");
	public static string SetupNotDoneInstructions(Language? l = null) => T(l, "Setup.NotDoneInstructions");

	public static string SetupActionRecheck(Language? l = null) => T(l, "Setup.ActionRecheck");
	public static string SetupActionDownloadUsbIp(Language? l = null) => T(l, "Setup.ActionDownloadUsbIp");
	public static string SetupActionDownloadHidHide(Language? l = null) => T(l, "Setup.ActionDownloadHidHide");
	public static string SetupActionOpenBluetooth(Language? l = null) => T(l, "Setup.ActionOpenBluetooth");
	public static string SetupActionRunAdapterTest(Language? l = null) => T(l, "Setup.ActionRunAdapterTest");

	public static string SetupOptional(Language? l = null) => T(l, "Setup.Optional");
	public static string SetupGoToOverview(Language? l = null) => T(l, "Setup.GoToOverview");
	public static string AdapterTestIdlePhase(Language? l = null) => T(l, "Adapter.TestIdlePhase");
	public static string AdapterTestLoadPhase(Language? l = null) => T(l, "Adapter.TestLoadPhase");
	public static string AdapterTestFinishing(Language? l = null) => T(l, "Adapter.TestFinishing");
	public static string AdapterTestSecondsLeft(int seconds, Language? l = null) => T(l, "Adapter.TestSecondsLeft", A("seconds", seconds));
	public static string AdapterTestCancel(Language? l = null) => T(l, "Adapter.TestCancel");
	public static string AdapterTestCancelled(Language? l = null) => T(l, "Adapter.TestCancelled");
}
