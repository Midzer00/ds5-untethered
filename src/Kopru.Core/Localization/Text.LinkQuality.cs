// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Link quality: the Overview's live card, its warning toast and switch, and the adapter test's explanation.
public static partial class Text
{
	// Live card.
	public static string LinkGood(Language? l = null) => T(l, "Link.Good");
	public static string LinkBorderline(Language? l = null) => T(l, "Link.Borderline");
	public static string LinkPoor(Language? l = null) => T(l, "Link.Poor");
	public static string LinkMeasuring(Language? l = null) => T(l, "Link.Measuring");

	// On the USB cable there is no radio link to grade: the word under the gauge says so instead of "Measuring" for ever.
	public static string LinkWired(Language? l = null) => T(l, "Link.Wired");

	// The live card's caption under the delay while the cable is the link.
	public static string LiveTypicalDelayWired(Language? l = null) => T(l, "Live.TypicalDelayWired");
	// What to do, only when the player must do something (Borderline, Weak).
	public static string LinkAdviceBorderline(Language? l = null) => T(l, "Link.AdviceBorderline");
	public static string LinkAdvicePoor(Language? l = null) => T(l, "Link.AdvicePoor");
	public static string LinkGraphTip(Language? l = null) => T(l, "Link.GraphTip");

	// Warning toast and its switch.
	public static string NotifyLinkTitle(Language? l = null) => T(l, "Notify.LinkTitle");
	public static string NotifyLinkBody(Language? l = null) => T(l, "Notify.LinkBody");
	public static string NotifyLinkSetting(Language? l = null) => T(l, "Notify.LinkSetting");
	public static string NotifyLinkHint(Language? l = null) => T(l, "Notify.LinkHint");

	// Adapter test explanation.
	public static string AdapterHeadlineSuitable(Language? l = null) => T(l, "Adapter.HeadlineSuitable");
	public static string AdapterHeadlineBorderline(Language? l = null) => T(l, "Adapter.HeadlineBorderline");
	public static string AdapterHeadlineUnsuitable(Language? l = null) => T(l, "Adapter.HeadlineUnsuitable");
	public static string AdapterHeadlineNoData(Language? l = null) => T(l, "Adapter.HeadlineNoData");
	public static string AdapterHeadlineIncomplete(Language? l = null) => T(l, "Adapter.HeadlineIncomplete");
	public static string AdapterAdviceRetry(Language? l = null) => T(l, "Adapter.AdviceRetry");
	public static string AdapterAdvicePlacement(Language? l = null) => T(l, "Adapter.AdvicePlacement");
	public static string AdapterAdviceDistance(Language? l = null) => T(l, "Adapter.AdviceDistance");

	public static string AdapterStatusOk(Language? l = null) => T(l, "Adapter.StatusOk");
	public static string AdapterStatusBorderline(Language? l = null) => T(l, "Adapter.StatusBorderline");
	public static string AdapterStatusBad(Language? l = null) => T(l, "Adapter.StatusBad");

	public static string AdapterMeasuredRate(double rate, Language? l = null) => T(l, "Adapter.MeasuredRate", A("rate", rate, "F0"));
	public static string AdapterNeedRate(double min, Language? l = null) => T(l, "Adapter.NeedRate", A("min", min, "F0"));
	public static string AdapterMeasuredDelay(double ms, Language? l = null) => T(l, "Adapter.MeasuredDelay", A("ms", ms, "F1"));
	public static string AdapterNeedDelay(double ms, Language? l = null) => T(l, "Adapter.NeedDelay", A("ms", ms, "F0"));
	public static string AdapterMeasuredNoLoss(Language? l = null) => T(l, "Adapter.MeasuredNoLoss");
	public static string AdapterMeasuredLoss(double percent, Language? l = null) => T(l, "Adapter.MeasuredLoss", A("percent", percent, "F1"));
	public static string AdapterNeedLoss(double percent, Language? l = null) => T(l, "Adapter.NeedLoss", A("percent", percent, "F1"));
	public static string AdapterMeasuredGaps(int gaps, double ms, Language? l = null) => T(l, "Adapter.MeasuredGaps", A("gaps", gaps), A("ms", ms, "F0"));
	public static string AdapterNeedGaps(int count, double seconds, Language? l = null) => T(l, "Adapter.NeedGaps", A("count", count), A("seconds", seconds, "F0"));
	public static string AdapterMeasuredHaptics(double rate, Language? l = null) => T(l, "Adapter.MeasuredHaptics", A("rate", rate, "F0"));
	public static string AdapterNeedHaptics(double rate, Language? l = null) => T(l, "Adapter.NeedHaptics", A("rate", rate, "F0"));
	public static string AdapterMeasuredSlowdown(double idle, double load, Language? l = null) => T(l, "Adapter.MeasuredSlowdown", A("idle", idle, "F0"), A("load", load, "F0"));
	public static string AdapterNeedSlowdown(double percent, Language? l = null) => T(l, "Adapter.NeedSlowdown", A("percent", percent, "F0"));
	public static string AdapterLongGaps(int gaps, Language? l = null) => T(l, "Adapter.LongGaps", A("gaps", gaps));
}
