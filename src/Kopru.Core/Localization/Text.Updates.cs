// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// In-app updates (Settings > General, the update toast).
public static partial class Text
{
	public static string UpdatesGroup(Language? l = null) => Pick(l, $"Güncellemeler", $"Updates");
	public static string UpdateAutoCheck(Language? l = null) => Pick(l, $"Güncellemeleri Otomatik Denetle", $"Check For Updates Automatically");
	public static string UpdateAutoCheckHint(Language? l = null) => Pick(l,
		$"Açılışta ve haftada bir GitHub'da yeni sürüm var mı diye bakar. Yalnızca bir dosya istenir, sana ait hiçbir bilgi gitmez.",
		$"Looks on GitHub for a new version at start and once a week. Only a file is requested; nothing about you is sent.");
	public static string UpdateCheckNow(Language? l = null) => Pick(l, $"Şimdi Denetle", $"Check Now");
	public static string UpdateInstall(Language? l = null) => Pick(l, $"Güncelle", $"Update");
	public static string UpdateInstallAnyway(Language? l = null) => Pick(l, $"Yine De Güncelle", $"Update Anyway");
	public static string UpdateOpenPage(Language? l = null) => Pick(l, $"İndirme Sayfasını Aç", $"Open Download Page");

	// State line
	public static string UpdateNotChecked(Language? l = null) => Pick(l, $"Henüz Denetlenmedi", $"Not Checked Yet");
	public static string UpdateUpToDate(Language? l = null) => Pick(l, $"Güncel", $"Up To Date");
	public static string UpdateChecking(Language? l = null) => Pick(l, $"Denetleniyor…", $"Checking…");
	public static string UpdateReady(string version, Language? l = null) => Pick(l, $"{version} Hazır", $"{version} Ready");
	public static string UpdateDownloading(double percent, Language? l = null) => Pick(l, $"İndiriliyor… %{percent:F0}", $"Downloading… {percent:F0}%");
	public static string UpdateStarting(Language? l = null) => Pick(l, $"Kurulum Başlıyor…", $"Starting Setup…");
	public static string UpdateConfirm(Language? l = null) => Pick(l,
		$"Güncelleme sanal kumandayı kısa süre durdurur.",
		$"Updating stops the virtual controller for a moment.");
	public static string UpdateZipCopy(Language? l = null) => Pick(l,
		$"Bu kopya zip'ten açıldı; yeni sürümü sayfadan indir.",
		$"This copy came from the zip; get the new version from the page.");

	// Errors, in plain words
	public static string UpdateCheckOffline(Language? l = null) => Pick(l, $"Denetlenemedi: internete ulaşılamadı.", $"Couldn't check: no internet connection.");
	public static string UpdateCheckTimeout(Language? l = null) => Pick(l, $"Denetlenemedi: sunucu yanıt vermedi.", $"Couldn't check: the server didn't answer.");
	public static string UpdateCheckInvalid(Language? l = null) => Pick(l, $"Denetlenemedi: sürüm bilgisi okunamadı.", $"Couldn't check: the version info couldn't be read.");
	public static string UpdateNotYetAvailable(Language? l = null) => Pick(l, $"Yeni sürüm henüz hazır değil, biraz sonra tekrar dene.", $"The new version isn't ready yet, try again a bit later.");
	public static string UpdateDownloadOffline(Language? l = null) => Pick(l, $"İndirilemedi: bağlantı koptu, tekrar dene.", $"Couldn't download: the connection dropped, try again.");
	public static string UpdateDownloadRejected(Language? l = null) => Pick(l, $"İndirilen dosya hatalıydı ve silindi. Tekrar dene.", $"The downloaded file was faulty and was deleted. Try again.");
	public static string UpdateSaveFailed(Language? l = null) => Pick(l, $"Dosya kaydedilemedi, diskte yer var mı bak.", $"Couldn't save the file; check the free disk space.");
	public static string UpdateLaunchFailed(Language? l = null) => Pick(l, $"Kurulum başlatılamadı; uygulamayı kapatıp yeniden aç.", $"Setup couldn't be started; close and reopen the app.");

	// Toast
	public static string NotifyUpdateTitle(Language? l = null) => Pick(l, $"Yeni Sürüm Hazır", $"New Version Ready");
	public static string NotifyUpdateBody(string version, Language? l = null) => Pick(l, $"{version} · Ayarlar > Genel", $"{version} · Settings > General");
}
