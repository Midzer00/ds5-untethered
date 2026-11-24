// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Core.Localization;

public enum PluralCategory
{
	Zero,
	One,
	Two,
	Few,
	Many,
	Other
}

// CLDR plural operands (UTS #35) derived from the formatted number shown to the user:
// n = |value|, i = integer digits, v = visible fraction digit count (with trailing zeros),
// w = visible fraction digit count (without trailing zeros), f = visible fraction digits, t = fraction digits without trailing zeros.
public readonly record struct PluralOperands(double N, long I, int V, int W, long F, long T)
{
	public static bool TryParse(string formattedInvariant, out PluralOperands operands)
	{
		operands = default;
		if (string.IsNullOrWhiteSpace(formattedInvariant))
		{
			return false;
		}
		ReadOnlySpan<char> span = formattedInvariant.AsSpan().Trim();
		if (span.Length > 0 && span[0] is '-' or '+')
		{
			span = span[1..];
		}
		if (span.Length == 0 || !double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || double.IsNaN(n) || double.IsInfinity(n))
		{
			return false;
		}
		n = Math.Abs(n);
		string clean = span.IndexOfAny('e', 'E') >= 0
			? n.ToString("0.################", CultureInfo.InvariantCulture)
			: span.ToString();

		int dot = clean.IndexOf('.');
		ReadOnlySpan<char> intSpan = dot < 0 ? clean.AsSpan() : clean.AsSpan(0, dot);
		ReadOnlySpan<char> fracSpan = dot < 0 ? ReadOnlySpan<char>.Empty : clean.AsSpan(dot + 1);

		if (intSpan.Length == 0)
		{
			intSpan = "0";
		}
		if (!long.TryParse(intSpan.Length > 18 ? intSpan[^18..] : intSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long i))
		{
			return false;
		}

		int v = fracSpan.Length;
		ReadOnlySpan<char> trimmedFrac = fracSpan.TrimEnd('0');
		int w = trimmedFrac.Length;
		long f = 0;
		if (v > 0 && !long.TryParse(fracSpan.Length > 18 ? fracSpan[..18] : fracSpan, NumberStyles.None, CultureInfo.InvariantCulture, out f))
		{
			return false;
		}
		long t = 0;
		if (w > 0 && !long.TryParse(trimmedFrac.Length > 18 ? trimmedFrac[..18] : trimmedFrac, NumberStyles.None, CultureInfo.InvariantCulture, out t))
		{
			return false;
		}

		operands = new PluralOperands(n, i, v, w, f, t);
		return true;
	}
}

// CLDR cardinal plural rules for shipped and target languages:
// en, tr, zh (zh-Hans), ru, es, pt (pt-BR), de, ja, fr, pl, ko.
public static class PluralRules
{
	public static PluralCategory Select(string languageCode, PluralOperands op)
	{
		if (string.Equals(languageCode, "pt-PT", StringComparison.OrdinalIgnoreCase))
		{
			return SelectRomanceMillions(op.I == 1 && op.V == 0, op);
		}

		return PrimaryLanguage(languageCode) switch
		{
			"zh" or "ja" or "ko" => PluralCategory.Other,
			"en" or "de" => op.I == 1 && op.V == 0 ? PluralCategory.One : PluralCategory.Other,
			"tr" => op.N == 1 ? PluralCategory.One : PluralCategory.Other,
			"es" => SelectRomanceMillions(op.N == 1, op),
			"pt" or "fr" => SelectRomanceMillions(op.I is 0 or 1, op),
			"ru" => SelectRussian(op),
			"pl" => SelectPolish(op),
			_ => op.I == 1 && op.V == 0 ? PluralCategory.One : PluralCategory.Other
		};
	}

	private static PluralCategory SelectRomanceMillions(bool isOne, PluralOperands op)
	{
		if (isOne)
		{
			return PluralCategory.One;
		}
		if (op.V == 0 && op.I != 0 && op.I % 1_000_000 == 0)
		{
			return PluralCategory.Many;
		}
		return PluralCategory.Other;
	}

	private static PluralCategory SelectRussian(PluralOperands op)
	{
		if (op.V != 0)
		{
			return PluralCategory.Other;
		}
		long mod10 = op.I % 10;
		long mod100 = op.I % 100;
		if (mod10 == 1 && mod100 != 11)
		{
			return PluralCategory.One;
		}
		if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14)
		{
			return PluralCategory.Few;
		}
		return PluralCategory.Many;
	}

	private static PluralCategory SelectPolish(PluralOperands op)
	{
		if (op.V != 0)
		{
			return PluralCategory.Other;
		}
		if (op.I == 1)
		{
			return PluralCategory.One;
		}
		long mod10 = op.I % 10;
		long mod100 = op.I % 100;
		if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14)
		{
			return PluralCategory.Few;
		}
		return PluralCategory.Many;
	}

	internal static string PrimaryLanguage(string code)
	{
		if (string.IsNullOrEmpty(code))
		{
			return "en";
		}
		int dash = code.IndexOfAny(['-', '_']);
		return (dash < 0 ? code : code[..dash]).ToLowerInvariant();
	}
}
