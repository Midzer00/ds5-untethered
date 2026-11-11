// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// Link quality: the Overview's live card, its warning toast and switch, and the adapter test's explanation.
public static partial class Text
{
	// Live card.
	public static string LinkLabel(Language? l = null) => Pick(l, $"Bağlantı", $"Link");
	public static string LinkGood(Language? l = null) => Pick(l, $"İyi", $"Good");
	public static string LinkBorderline(Language? l = null) => Pick(l, $"Sınırda", $"Borderline");
	public static string LinkPoor(Language? l = null) => Pick(l, $"Zayıf", $"Weak");
	public static string LinkMeasuring(Language? l = null) => Pick(l, $"Ölçülüyor", $"Measuring");
	public static string LinkAdviceGood(Language? l = null) => Pick(l,
		$"Bağlantı sağlam, bir şey yapmanız gerekmiyor.",
		$"The link is solid, nothing to do.");
	public static string LinkAdviceBorderline(Language? l = null) => Pick(l,
		$"Ara sıra gecikme var. Kumandayı adaptöre yaklaştırın, aradaki engelleri kaldırın.",
		$"Some delay now and then. Move the controller closer to the adapter and clear the way between them.");
	public static string LinkAdvicePoor(Language? l = null) => Pick(l,
		$"Gecikme hissedilebilir. Yaklaşın, adaptörü USB uzatma kablosuyla öne alın, 2,4 GHz cihazlardan uzak tutun.",
		$"You may feel the delay. Move closer, put the adapter on a USB extension cable, away from 2.4 GHz devices.");
	public static string LinkAdviceMeasuring(Language? l = null) => Pick(l,
		$"Bağlantı birkaç saniye ölçülüyor.",
		$"Measuring the link for a few seconds.");
	public static string LinkLimitLabel(Language? l = null) => Pick(l, $"Hissedilir", $"Noticeable");
	public static string LinkGraphTip(Language? l = null) => Pick(l,
		$"Kalın çizgi: her saniyenin en kötü gecikmesi; ince çizgi: tipik gecikme. Kesikli çizginin üstü hissedilir, oraya çıkan anlar kırmızıyla işaretlenir.",
		$"Thick line: the worst delay of each second; thin line: the typical delay. Above the dashed line you can feel it; moments that go there are marked in red.");

	// Warning toast and its switch.
	public static string NotifyLinkTitle(Language? l = null) => Pick(l, $"Bağlantı Zayıf", $"Weak Link");
	public static string NotifyLinkBody(Language? l = null) => Pick(l, $"Adaptöre yaklaşın", $"Move closer to the adapter");
	public static string NotifyLinkSetting(Language? l = null) => Pick(l, $"Zayıf Bağlantı", $"Weak Link");
	public static string NotifyLinkHint(Language? l = null) => Pick(l,
		$"Bağlantı gecikmeyi hissettirecek kadar uzun süre zayıflarsa, her seferinde bir kez.",
		$"If the link stays weak long enough to feel the delay, once each time.");

	// Adapter test explanation.
	public static string AdapterHeadlineSuitable(Language? l = null) => Pick(l,
		$"Uygun: titreşim ve tetikler için yeterince hızlı.",
		$"Suitable: fast enough for haptics and triggers.");
	public static string AdapterHeadlineBorderline(Language? l = null) => Pick(l,
		$"Sınırda: çalışır, ama ara sıra gecikme ya da kesik titreşim olabilir.",
		$"Borderline: it works, but expect some delay or choppy vibration now and then.");
	public static string AdapterHeadlineUnsuitable(Language? l = null) => Pick(l,
		$"Uygun değil: gecikmeyi ve kesik titreşimi hissedebilirsiniz.",
		$"Not suitable: you may feel delay and choppy vibration.");
	public static string AdapterHeadlineNoData(Language? l = null) => Pick(l,
		$"Ölçülemedi: kumandadan veri gelmedi.",
		$"Could not measure: no data came from the controller.");
	public static string AdapterHeadlineIncomplete(Language? l = null) => Pick(l,
		$"Ölçüm yarım kaldı: kumanda test sırasında veri göndermeyi bıraktı (uykuya geçti, kapandı ya da uzaklaştı).",
		$"The measurement is incomplete: the controller stopped sending during the test (it slept, turned off or moved away).");
	public static string AdapterAdviceRetry(Language? l = null) => Pick(l,
		$"Kumandanın açık ve adaptöre birkaç metre yakın olduğundan emin olup testi tekrarlayın.",
		$"Make sure the controller is on and within a few metres of the adapter, then run the test again.");
	public static string AdapterAdvicePlacement(Language? l = null) => Pick(l,
		$"Adaptörü öndeki bir USB 2.0 girişine ya da kısa bir USB uzatma kablosuyla masanın üstüne alın; USB 3.0 (mavi) girişlerden ve kablosuz mouse, klavye, kulaklık alıcıları gibi 2,4 GHz cihazlardan uzak tutun.",
		$"Move the adapter to a front USB 2.0 port or onto a short USB extension cable on the desk, away from USB 3.0 (blue) ports and 2.4 GHz devices such as wireless mouse, keyboard and headset receivers.");
	public static string AdapterAdviceDistance(Language? l = null) => Pick(l,
		$"Kumandayı birkaç metre içinde tutup testi tekrarlayın; sonuç değişmezse Bluetooth 5 ya da daha yeni bir adaptör deneyin.",
		$"Keep the controller within a few metres and test again; if nothing changes, try a Bluetooth 5 or newer adapter.");

	public static string AdapterStatusOk(Language? l = null) => Pick(l, $"Tamam", $"OK");
	public static string AdapterStatusBorderline(Language? l = null) => Pick(l, $"Sınırda", $"Borderline");
	public static string AdapterStatusBad(Language? l = null) => Pick(l, $"Kötü", $"Bad");

	public static string AdapterMeasuredRate(double rate, Language? l = null) => Pick(l, $"Saniyede {rate:F0} rapor", $"{rate:F0} reports a second");
	public static string AdapterNeedRate(double min, Language? l = null) => Pick(l, $"en az {min:F0} gerekli", $"at least {min:F0} needed");
	public static string AdapterMeasuredDelay(double ms, Language? l = null) => Pick(l, $"En kötü gecikme {ms:F1} ms", $"Worst delay {ms:F1} ms");
	public static string AdapterNeedDelay(double ms, Language? l = null) => Pick(l, $"{ms:F0} ms'nin altında olmalı", $"should stay under {ms:F0} ms");
	public static string AdapterMeasuredNoLoss(Language? l = null) => Pick(l, $"Kayıp rapor yok", $"No lost reports");
	public static string AdapterMeasuredLoss(double percent, Language? l = null) => Pick(l, $"Kayıp rapor %{percent:F1}", $"Lost reports {percent:F1}%");
	public static string AdapterNeedLoss(double percent, Language? l = null) => Pick(l, $"en çok %{percent:F1}", $"at most {percent:F1}%");
	public static string AdapterMeasuredGaps(int gaps, double ms, Language? l = null) => Pick(l, $"{gaps} kez {ms:F0} ms'den uzun kesinti", $"{gaps} gaps longer than {ms:F0} ms");
	public static string AdapterNeedGaps(int count, double seconds, Language? l = null) => Pick(l, $"{seconds:F0} saniyede {count}'ten az olmalı", $"fewer than {count} in {seconds:F0} seconds");
	public static string AdapterMeasuredHaptics(double rate, Language? l = null) => Pick(l, $"Titreşim akışı saniyede {rate:F0}", $"Vibration stream {rate:F0} a second");
	public static string AdapterNeedHaptics(double rate, Language? l = null) => Pick(l, $"en az {rate:F0} gerekli", $"at least {rate:F0} needed");
	public static string AdapterMeasuredSlowdown(double idle, double load, Language? l = null) => Pick(l,
		$"Titreşimle {idle:F0} → {load:F0} rapor/sn", $"With vibration {idle:F0} → {load:F0} reports/s");
	public static string AdapterNeedSlowdown(double percent, Language? l = null) => Pick(l, $"en az %{percent:F0}'ı kalmalı", $"should keep at least {percent:F0}%");
	public static string AdapterLongGaps(int gaps, Language? l = null) => Pick(l,
		$"Kumanda verisi {gaps} kez uzun süre kesildi.",
		$"Controller data stopped for a long moment {gaps} times.");
}
