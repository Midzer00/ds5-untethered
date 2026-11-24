// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// In-app updates (Settings > General, the update toast).
public static partial class Text
{
	public static string UpdatesGroup(Language? l = null) => T(l, "Updates.Group");
	public static string UpdateAutoCheck(Language? l = null) => T(l, "Update.AutoCheck");
	public static string UpdateAutoCheckHint(Language? l = null) => T(l, "Update.AutoCheckHint");
	public static string UpdateCheckNow(Language? l = null) => T(l, "Update.CheckNow");
	public static string UpdateInstall(Language? l = null) => T(l, "Update.Install");
	public static string UpdateInstallAnyway(Language? l = null) => T(l, "Update.InstallAnyway");
	public static string UpdateOpenPage(Language? l = null) => T(l, "Update.OpenPage");

	// State line
	public static string UpdateNotChecked(Language? l = null) => T(l, "Update.NotChecked");
	public static string UpdateUpToDate(Language? l = null) => T(l, "Update.UpToDate");
	public static string UpdateChecking(Language? l = null) => T(l, "Update.Checking");
	public static string UpdateReady(string version, Language? l = null) => T(l, "Update.Ready", A("version", version));
	public static string UpdateDownloading(double percent, Language? l = null) => T(l, "Update.Downloading", A("percent", percent, "F0"));
	public static string UpdateStarting(Language? l = null) => T(l, "Update.Starting");
	public static string UpdateConfirm(Language? l = null) => T(l, "Update.Confirm");
	public static string UpdateZipCopy(Language? l = null) => T(l, "Update.ZipCopy");

	// Errors, in plain words
	public static string UpdateCheckOffline(Language? l = null) => T(l, "Update.CheckOffline");
	public static string UpdateCheckTimeout(Language? l = null) => T(l, "Update.CheckTimeout");
	public static string UpdateCheckInvalid(Language? l = null) => T(l, "Update.CheckInvalid");
	public static string UpdateNotYetAvailable(Language? l = null) => T(l, "Update.NotYetAvailable");
	public static string UpdateDownloadOffline(Language? l = null) => T(l, "Update.DownloadOffline");
	public static string UpdateDownloadRejected(Language? l = null) => T(l, "Update.DownloadRejected");
	public static string UpdateSaveFailed(Language? l = null) => T(l, "Update.SaveFailed");
	public static string UpdateLaunchFailed(Language? l = null) => T(l, "Update.LaunchFailed");

	// Toast
	public static string NotifyUpdateTitle(Language? l = null) => T(l, "Notify.UpdateTitle");
	public static string NotifyUpdateBody(string version, Language? l = null) => T(l, "Notify.UpdateBody", A("version", version));
}
