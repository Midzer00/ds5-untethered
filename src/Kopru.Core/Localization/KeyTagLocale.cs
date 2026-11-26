// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Kopru.Core.Localization;

// Render-only "key tag" locale ("qtg") for the translation context tool: every catalogue value is the English text wrapped
// in invisible markers that name its key, so a headless render can trace each drawn text back to its key. The markers are
// Unicode tag characters (U+E0020..U+E007F): zero-width, default-ignorable and line-break class CM, so the layout matches
// the plain English render. Never shipped in Localizer.Available or accepted from settings (like PseudoLocale).
//
// A tagged value is: Open d1 d2 <English text> Close d1 d2, where d1 d2 are base-64 digits of the key's index in Keys.
// Both markers carry the index, so a piece of a split or trimmed value still names its key.
internal static class KeyTagLocale
{
	public const string Code = "qtg";
	public const string NativeName = "[Key Tags]";

	private const int OpenCodepoint = 0xE007E;
	private const int CloseCodepoint = 0xE007F;
	private const int DigitBase = 0xE0020;
	private const int Radix = 64;
	public const int MaxKeys = Radix * Radix;

	// One marker: three codepoints, each a surrogate pair.
	public const int MarkerLength = 6;

	private static readonly Lazy<string[]> SortedKeys = new(() =>
	{
		string[] keys = StringCatalog.English.Raw.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
		if (keys.Length > MaxKeys)
		{
			throw new InvalidOperationException($"Too many keys for the key tag encoding: {keys.Length} > {MaxKeys}.");
		}
		return keys;
	});

	private static readonly Lazy<Dictionary<string, int>> IndexByKey = new(() =>
		SortedKeys.Value.Select((k, i) => (k, i)).ToDictionary(p => p.k, p => p.i, StringComparer.Ordinal));

	public static Language Language => new(Code);

	public static bool IsKeyTag(string? code) => string.Equals(code, Code, StringComparison.OrdinalIgnoreCase);

	// Every English key, ordinal order; a key's index here is what its markers encode.
	public static IReadOnlyList<string> Keys => SortedKeys.Value;

	public static int IndexOf(string key) => IndexByKey.Value.TryGetValue(key, out int i) ? i : -1;

	// The English text wrapped in the key's markers (unknown keys come back unchanged).
	public static string Wrap(string key, string text)
	{
		int index = IndexOf(key);
		if (index < 0)
		{
			return text;
		}
		var sb = new StringBuilder(text.Length + 2 * MarkerLength);
		AppendMarker(sb, OpenCodepoint, index);
		sb.Append(text);
		AppendMarker(sb, CloseCodepoint, index);
		return sb.ToString();
	}

	private static void AppendMarker(StringBuilder sb, int kind, int index)
	{
		sb.Append(char.ConvertFromUtf32(kind));
		sb.Append(char.ConvertFromUtf32(DigitBase + index / Radix));
		sb.Append(char.ConvertFromUtf32(DigitBase + index % Radix));
	}

	public static bool HasMarkers(string? text) => text != null && FindMarker(text, 0) >= 0;

	// The text without any markers.
	public static string Strip(string text) => Decode(text).Clean;

	// The keys in a tagged text: each span is the key's visible text, as offsets into the tagged text (markers of nested
	// keys included) and into the clean text. Nested values (a tagged value formatted into another) give one span each.
	// A close marker whose open marker was cut off starts at the enclosing span (or the text start); an open marker
	// without its close ends at the text end.
	public static KeyTagDecoding Decode(string text)
	{
		var spans = new List<KeyTagSpan>();
		var clean = new StringBuilder(text.Length);
		var open = new List<(int Index, int TaggedStart, int CleanStart)>();
		int i = 0;
		while (i < text.Length)
		{
			if (TryReadMarker(text, i, out bool isOpen, out int index))
			{
				if (isOpen)
				{
					open.Add((index, i + MarkerLength, clean.Length));
				}
				else
				{
					int at = open.FindLastIndex(o => o.Index == index);
					if (at >= 0)
					{
						(int _, int taggedStart, int cleanStart) = open[at];
						open.RemoveRange(at, open.Count - at);
						Add(spans, index, taggedStart, i, cleanStart, clean.Length, false);
					}
					else
					{
						(int taggedStart, int cleanStart) = open.Count > 0 ? (open[^1].TaggedStart, open[^1].CleanStart) : (0, 0);
						Add(spans, index, taggedStart, i, cleanStart, clean.Length, true);
					}
				}
				i += MarkerLength;
				continue;
			}
			clean.Append(text[i]);
			i++;
		}
		for (int o = open.Count - 1; o >= 0; o--)
		{
			Add(spans, open[o].Index, open[o].TaggedStart, text.Length, open[o].CleanStart, clean.Length, true);
		}
		return new KeyTagDecoding(clean.ToString(), spans.OrderBy(s => s.CleanStart).ThenByDescending(s => s.CleanLength).ToArray());
	}

	private static void Add(List<KeyTagSpan> spans, int index, int taggedStart, int taggedEnd, int cleanStart, int cleanEnd, bool partial)
	{
		if (index < Keys.Count && cleanEnd > cleanStart)
		{
			spans.Add(new KeyTagSpan(Keys[index], taggedStart, taggedEnd - taggedStart, cleanStart, cleanEnd - cleanStart, partial));
		}
	}

	private static int FindMarker(string text, int from)
	{
		for (int i = from; i < text.Length; i++)
		{
			if (TryReadMarker(text, i, out _, out _))
			{
				return i;
			}
		}
		return -1;
	}

	private static bool TryReadMarker(string text, int i, out bool isOpen, out int index)
	{
		isOpen = false;
		index = -1;
		if (i + MarkerLength > text.Length || !char.IsHighSurrogate(text[i]))
		{
			return false;
		}
		int kind = char.ConvertToUtf32(text[i], text[i + 1]);
		if (kind != OpenCodepoint && kind != CloseCodepoint)
		{
			return false;
		}
		if (!TryDigit(text, i + 2, out int hi) || !TryDigit(text, i + 4, out int lo))
		{
			return false;
		}
		isOpen = kind == OpenCodepoint;
		index = hi * Radix + lo;
		return true;
	}

	private static bool TryDigit(string text, int i, out int digit)
	{
		digit = -1;
		if (!char.IsHighSurrogate(text[i]) || !char.IsLowSurrogate(text[i + 1]))
		{
			return false;
		}
		int cp = char.ConvertToUtf32(text[i], text[i + 1]) - DigitBase;
		if (cp is < 0 or >= Radix)
		{
			return false;
		}
		digit = cp;
		return true;
	}
}

// One key's visible text inside a tagged string. Partial: one of its markers was missing (the value was cut or split).
internal readonly record struct KeyTagSpan(string Key, int TaggedStart, int TaggedLength, int CleanStart, int CleanLength, bool Partial);

internal sealed record KeyTagDecoding(string Clean, IReadOnlyList<KeyTagSpan> Spans);
