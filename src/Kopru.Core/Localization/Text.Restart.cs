// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// "Restart The Computer" on the setup page's drivers step (shown while Windows reports a pending restart).
public static partial class Text
{
	public static string SetupRestartButton(Language? l = null) => Pick(l, $"Bilgisayarı Yeniden Başlat", $"Restart The Computer");

	public static string SetupRestartQuestion(Language? l = null) => Pick(l,
		$"Bilgisayar şimdi yeniden başlatılsın mı? Açık programlar kapanır; kaydetmediğiniz işleri önce kaydedin.",
		$"Restart the computer now? Open programs will be closed, so save your work first.");

	public static string SetupRestartConfirm(Language? l = null) => Pick(l, $"Yeniden Başlat", $"Restart");

	public static string SetupRestartCancel(Language? l = null) => Pick(l, $"Vazgeç", $"Cancel");

	public static string SetupRestartFailed(Language? l = null) => Pick(l,
		$"Windows yeniden başlatmayı başlatamadı. Bilgisayarı Başlat menüsünden yeniden başlatabilirsiniz.",
		$"Windows couldn't start the restart. You can restart the computer from the Start menu.");
}
