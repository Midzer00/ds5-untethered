// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;

namespace Kopru.Core.Localization;

public readonly struct Language : IEquatable<Language>
{
	private readonly string? _code;

	public Language(string code)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(code);
		_code = code.Trim();
	}

	public static Language English => new("en");
	public static Language Turkish => new("tr");
	internal static Language Pseudo => new("qps");

	public string Code => string.IsNullOrEmpty(_code) ? "en" : _code;
	public string NativeName => PseudoLocale.IsPseudo(Code)
		? PseudoLocale.NativeName
		: KeyTagLocale.IsKeyTag(Code)
			? KeyTagLocale.NativeName
			: StringCatalog.GetBundle(Code)?.NativeName ?? Code;

	public bool Equals(Language other) => string.Equals(Code, other.Code, StringComparison.OrdinalIgnoreCase);
	public override bool Equals(object? obj) => obj is Language other && Equals(other);
	public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Code);
	public override string ToString() => Code;

	public static bool operator ==(Language left, Language right) => left.Equals(right);
	public static bool operator !=(Language left, Language right) => !left.Equals(right);
}

// Current UI language. Defaults to the OS UI culture when a shipped language matches it (else English) until set explicitly.
public static class Localizer
{
	private static readonly object Gate = new();
	private static Language? _chosen;
	private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
	private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
	private static readonly ConcurrentDictionary<string, CultureInfo> Cultures = new(StringComparer.OrdinalIgnoreCase);

	public static event Action<Language>? LanguageChanged;

	// Shipped languages discovered from embedded strings/<code>.json resources.
	public static IReadOnlyList<Language> Available =>
		StringCatalog.EmbeddedCodes().Select(c => new Language(c)).ToArray();

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

	// Automatic language resolution: exact culture, then its parent chain, then same two-letter language (never across
	// Chinese scripts), else English.
	public static Language FromCulture(CultureInfo? culture)
	{
		if (culture == null || string.IsNullOrEmpty(culture.Name))
		{
			return Language.English;
		}

		IReadOnlyList<Language> available = Available;
		for (CultureInfo c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
		{
			if (FindExact(available, c.Name) is { } exact)
			{
				return exact;
			}
			string? scriptAlias = ChineseScriptAlias(c.Name);
			if (scriptAlias != null && FindExact(available, scriptAlias) is { } aliased)
			{
				return aliased;
			}
		}

		string iso = culture.TwoLetterISOLanguageName;
		if (!string.IsNullOrEmpty(iso) && !iso.Equals("zh", StringComparison.OrdinalIgnoreCase))
		{
			foreach (Language candidate in available)
			{
				if (string.Equals(PluralRules.PrimaryLanguage(candidate.Code), iso, StringComparison.OrdinalIgnoreCase))
				{
					return candidate;
				}
			}
		}

		return Language.English;
	}

	private static Language? FindExact(IReadOnlyList<Language> available, string code)
	{
		foreach (Language lang in available)
		{
			if (string.Equals(lang.Code, code, StringComparison.OrdinalIgnoreCase))
			{
				return lang;
			}
		}
		return null;
	}

	private static string? ChineseScriptAlias(string cultureName) => cultureName.ToLowerInvariant() switch
	{
		"zh-cn" or "zh-sg" or "zh-chs" => "zh-Hans",
		"zh-tw" or "zh-hk" or "zh-mo" or "zh-cht" => "zh-Hant",
		_ => null
	};

	public static string Code(Language language) => language.Code;

	// Resolves a saved language code against shipped languages; unknown codes (and test-only 'qps') return null (Automatic).
	public static Language? FromCode(string? code)
	{
		if (string.IsNullOrWhiteSpace(code))
		{
			return null;
		}
		return FindExact(Available, code.Trim());
	}

	public static CultureInfo Culture(Language language)
	{
		string code = language.Code;
		if (code.Equals("tr", StringComparison.OrdinalIgnoreCase))
		{
			return TurkishCulture;
		}
		if (code.Equals("en", StringComparison.OrdinalIgnoreCase) || code.Equals("qps", StringComparison.OrdinalIgnoreCase)
			|| KeyTagLocale.IsKeyTag(code))
		{
			return EnglishCulture;
		}
		return Cultures.GetOrAdd(code, static c =>
		{
			try
			{
				return CultureInfo.GetCultureInfo(c);
			}
			catch (CultureNotFoundException)
			{
				return EnglishCulture;
			}
		});
	}
}

// Every user-facing string. Each method takes an optional language (default: Localizer.Current) and formats numbers
// with that language's culture. All strings live in Localization/strings/<code>.json.
public static partial class Text
{
	private static TextArg A(string name, string? value) => new(name, value);
	private static TextArg A(string name, int value, string? format = null) => new(name, value, format);
	private static TextArg A(string name, long value, string? format = null) => new(name, value, format);
	private static TextArg A(string name, double value, string? format = null) => new(name, value, format);
	private static TextArg A(string name, float value, string? format = null) => new(name, value, format);
	private static TextArg A(string name, IFormattable value, string? format = null) => new(name, value, format);

	private static string T(Language? l, string key) =>
		StringCatalog.Format(l, key, ReadOnlySpan<TextArg>.Empty);

	private static string T(Language? l, string key, TextArg a0)
	{
		Span<TextArg> span = [a0];
		return StringCatalog.Format(l, key, span);
	}

	private static string T(Language? l, string key, TextArg a0, TextArg a1)
	{
		Span<TextArg> span = [a0, a1];
		return StringCatalog.Format(l, key, span);
	}

	private static string T(Language? l, string key, TextArg a0, TextArg a1, TextArg a2)
	{
		Span<TextArg> span = [a0, a1, a2];
		return StringCatalog.Format(l, key, span);
	}

	private static string T(Language? l, string key, TextArg a0, TextArg a1, TextArg a2, TextArg a3)
	{
		Span<TextArg> span = [a0, a1, a2, a3];
		return StringCatalog.Format(l, key, span);
	}

	private static string T(Language? l, string key, params TextArg[] args) =>
		StringCatalog.Format(l, key, args);
	public static string VerdictSuitable(Language? l = null) => T(l, "Verdict.Suitable");
	public static string VerdictBorderline(Language? l = null) => T(l, "Verdict.Borderline");
	public static string VerdictUnsuitable(Language? l = null) => T(l, "Verdict.Unsuitable");

	public static string AdapterRateTooLow(double rate, Language? l = null) => T(l, "Adapter.RateTooLow", A("rate", rate, "F0"));

	public static string AdapterRateLow(double rate, Language? l = null) => T(l, "Adapter.RateLow", A("rate", rate, "F0"));

	public static string AdapterReportsLost(double lostPercent, Language? l = null) => T(l, "Adapter.ReportsLost", A("lostPercent", lostPercent, "F1"));

	public static string AdapterFrequentStalls(double stallMs, Language? l = null) => T(l, "Adapter.FrequentStalls", A("stallMs", stallMs, "F0"));

	public static string AdapterOccasionalStalls(double stallMs, Language? l = null) => T(l, "Adapter.OccasionalStalls", A("stallMs", stallMs, "F0"));

	public static string AdapterHapticsBehind(double rate, double target, Language? l = null) => T(l, "Adapter.HapticsBehind", A("rate", rate, "F0"), A("target", target, "F0"));

	public static string AdapterHapticsStruggling(double rate, Language? l = null) => T(l, "Adapter.HapticsStruggling", A("rate", rate, "F0"));

	public static string AdapterSlowsUnderLoad(double idleRate, double loadRate, Language? l = null) => T(l, "Adapter.SlowsUnderLoad", A("idleRate", idleRate, "F0"), A("loadRate", loadRate, "F0"));

	public static string AdapterAllGood(double rate, Language? l = null) => T(l, "Adapter.AllGood", A("rate", rate, "F0"));
}
