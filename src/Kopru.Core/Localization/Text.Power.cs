// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of power-off, measured latency and the diagnostics export.
public static partial class Text
{
	public static string PowerOffDone(Language? l = null) => T(l, "Power.OffDone");

	public static string PowerOffFailed(Language? l = null) => T(l, "Power.OffFailed");

	public static string PowerOffIdle(double minutes, Language? l = null) => T(l, "Power.OffIdle", A("minutes", minutes, "F0"));

	public static string PowerOffWhenIdleSetting(Language? l = null) => T(l, "Power.OffWhenIdleSetting");

	// "Pause Haptics In Silence" (Settings, General, Power).
	public static string PauseHapticsInSilence(Language? l = null) => T(l, "Pause.HapticsInSilence");

	public static string PauseHapticsInSilenceShort(Language? l = null) => T(l, "Pause.HapticsInSilenceShort");

	public static string PauseHapticsInSilenceHint(Language? l = null) => T(l, "Pause.HapticsInSilenceHint");

	public static string LatencyValue(double p50, double p99, double relay, Language? l = null) => T(l, "Latency.Value", A("p50", p50, "F1"), A("p99", p99, "F1"), A("relay", relay, "F2"));

	public static string LatencyUnknown(Language? l = null) => T(l, "Latency.Unknown");

	public static string DiagnosticsSaved(string path, Language? l = null) => T(l, "Diagnostics.Saved", A("path", path));

	public static string DiagnosticsFailed(string detail, Language? l = null) => T(l, "Diagnostics.Failed", A("detail", detail));
}
