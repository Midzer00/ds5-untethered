// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Core.Localization;

public enum Language
{
	Turkish,
	English
}

// Current UI language. Defaults to the OS UI culture (Turkish for "tr", English otherwise) until set explicitly.
public static class Localizer
{
	private static readonly object Gate = new();
	private static Language? _chosen;

	public static event Action<Language>? LanguageChanged;

	// Explicit choice from settings; null means "follow the system".
	public static Language? Chosen
	{
		get
		{
			lock (Gate)
			{
				return _chosen;
			}
		}
		set
		{
			Language before;
			Language after;
			lock (Gate)
			{
				before = Current;
				_chosen = value;
				after = Current;
			}
			if (before != after)
			{
				LanguageChanged?.Invoke(after);
			}
		}
	}

	public static Language Current => Chosen ?? FromCulture(CultureInfo.CurrentUICulture);

	public static Language FromCulture(CultureInfo culture) =>
		culture.TwoLetterISOLanguageName.Equals("tr", StringComparison.OrdinalIgnoreCase) ? Language.Turkish : Language.English;

	public static string Code(Language language) => language == Language.Turkish ? "tr" : "en";

	public static Language? FromCode(string? code) => code?.Trim().ToLowerInvariant() switch
	{
		"tr" => Language.Turkish,
		"en" => Language.English,
		_ => null
	};

	private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
	private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");

	public static CultureInfo Culture(Language language) => language == Language.Turkish ? TurkishCulture : EnglishCulture;
}

// Every user-facing string. Each method takes an optional language (default: Localizer.Current) and formats numbers
// with that language's culture. To add a string: add a method here returning both texts via Pick.
public static partial class Text
{
	private static string Pick(Language? language, FormattableString tr, FormattableString en)
	{
		Language lang = language ?? Localizer.Current;
		return (lang == Language.Turkish ? tr : en).ToString(Localizer.Culture(lang));
	}

	public static string VerdictSuitable(Language? l = null) => Pick(l, $"Uygun", $"Suitable");
	public static string VerdictBorderline(Language? l = null) => Pick(l, $"Sınırda", $"Borderline");
	public static string VerdictUnsuitable(Language? l = null) => Pick(l, $"Uygun değil", $"Not suitable");

	public static string AdapterRateTooLow(double rate, Language? l = null) => Pick(l,
		$"Kumanda verisi çok yavaş geliyor ({rate:F0} rapor/sn; en az 250 olmalı).",
		$"Controller data arrives far too slowly ({rate:F0} reports/s; at least 250 needed).");

	public static string AdapterRateLow(double rate, Language? l = null) => Pick(l,
		$"Kumanda verisi yavaş geliyor ({rate:F0} rapor/sn; 250 ve üstü iyi).",
		$"Controller data arrives slowly ({rate:F0} reports/s; 250 or more is good).");

	public static string AdapterReportsLost(double lostPercent, Language? l = null) => Pick(l,
		$"Raporların %{lostPercent:F1}'i kayboluyor.",
		$"{lostPercent:F1}% of reports are lost.");

	public static string AdapterFrequentStalls(double stallMs, Language? l = null) => Pick(l,
		$"Sık takılma var: raporların %1'i {stallMs:F0} ms'den fazla gecikiyor.",
		$"Frequent stalls: 1% of reports are delayed by more than {stallMs:F0} ms.");

	public static string AdapterOccasionalStalls(double stallMs, Language? l = null) => Pick(l,
		$"Ara sıra takılma var: raporların %1'i {stallMs:F0} ms'den fazla gecikiyor.",
		$"Occasional stalls: 1% of reports are delayed by more than {stallMs:F0} ms.");

	public static string AdapterHapticsBehind(double rate, double target, Language? l = null) => Pick(l,
		$"Haptik akışı yetişemiyor ({rate:F0}/sn; {target:F0} gerekli).",
		$"The haptics stream can't keep up ({rate:F0}/s; {target:F0} needed).");

	public static string AdapterHapticsStruggling(double rate, Language? l = null) => Pick(l,
		$"Haptik akışı zor yetişiyor ({rate:F0}/sn).",
		$"The haptics stream barely keeps up ({rate:F0}/s).");

	public static string AdapterSlowsUnderLoad(double idleRate, double loadRate, Language? l = null) => Pick(l,
		$"Haptik çalarken kumanda verisi çok yavaşlıyor ({idleRate:F0} → {loadRate:F0} rapor/sn).",
		$"Controller data slows down a lot while haptics play ({idleRate:F0} → {loadRate:F0} reports/s).");

	public static string AdapterAllGood(double rate, Language? l = null) => Pick(l,
		$"{rate:F0} rapor/sn, kayıp yok denecek kadar az, takılma yok. Haptik ve tetikler için uygun.",
		$"{rate:F0} reports/s, next to no loss, no stalls. Suitable for haptics and triggers.");
}
