// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of power-off, measured latency and the diagnostics export.
public static partial class Text
{
	public static string PowerOffDone(Language? l = null) => Pick(l,
		$"Kumanda kapatıldı.",
		$"The controller was turned off.");

	public static string PowerOffFailed(Language? l = null) => Pick(l,
		$"Kumanda kapatılamadı. PS tuşunu 10 saniye basılı tutarak kapatabilirsin.",
		$"Could not turn off the controller. Hold the PS button for 10 seconds to turn it off.");

	public static string PowerOffIdle(double minutes, Language? l = null) => Pick(l,
		$"Kumanda {minutes:F0} dakika kullanılmadığı için kapatıldı.",
		$"The controller was turned off after {minutes:F0} minutes without use.");

	public static string PowerOffWhenIdleSetting(Language? l = null) => Pick(l,
		$"Kullanılmadığında kumandayı kapat",
		$"Turn off the controller when idle");

	public static string LatencyValue(double p50, double p99, double relay, Language? l = null) => Pick(l,
		$"Bağlantı gecikmesi {p50:F1} ms (en kötü {p99:F1} ms), aktarım {relay:F2} ms",
		$"Link delay {p50:F1} ms (worst {p99:F1} ms), relay {relay:F2} ms");

	public static string LatencyUnknown(Language? l = null) => Pick(l,
		$"Gecikme ölçülüyor…",
		$"Measuring latency…");

	public static string DiagnosticsSaved(string path, Language? l = null) => Pick(l,
		$"Tanılama dosyası kaydedildi: {path}",
		$"Diagnostics file saved: {path}");

	public static string DiagnosticsFailed(string detail, Language? l = null) => Pick(l,
		$"Tanılama dosyası oluşturulamadı: {detail}",
		$"Could not create the diagnostics file: {detail}");
}
