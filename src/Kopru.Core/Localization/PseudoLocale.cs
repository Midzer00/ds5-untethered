// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Kopru.Core.Localization;

// Test-only pseudo-locale ("qps"): maps ASCII letters to accented equivalents (Latin-1 / Latin Extended-A,
// all present in Inter), expands each string by ~35% using space-separated tilde groups so wrapped paragraphs
// break naturally, and frames the text in [...]. Never shipped in Localizer.Available or accepted from settings.
internal static class PseudoLocale
{
	public const string Code = "qps";
	public const string NativeName = "[Þšéûďö ~~~~]";

	public static bool IsPseudo(string? code) =>
		string.Equals(code, Code, StringComparison.OrdinalIgnoreCase);

	public static string Transform(string text)
	{
		if (string.IsNullOrEmpty(text) || !HasAsciiLetter(text))
		{
			return text;
		}

		if (!text.Contains('\n'))
		{
			return "[" + TransformLine(text) + "]";
		}

		string[] lines = text.Split('\n');
		var sb = new StringBuilder(text.Length + (text.Length / 2) + 8);
		sb.Append('[');
		for (int i = 0; i < lines.Length; i++)
		{
			if (i > 0)
			{
				sb.Append('\n');
			}
			string line = lines[i];
			bool cr = line.EndsWith('\r');
			string clean = cr ? line[..^1] : line;
			sb.Append(HasAsciiLetter(clean) ? TransformLine(clean) : clean);
			if (cr)
			{
				sb.Append('\r');
			}
		}
		sb.Append(']');
		return sb.ToString();
	}

	private static string TransformLine(string line)
	{
		// Total length target is ~1.35 * line.Length (including the outer '[' ']' and the space before tildes).
		int padLen = Math.Max(2, (int)Math.Round(line.Length * 0.35) - 3);
		var sb = new StringBuilder(line.Length + padLen + 1);
		foreach (char c in line)
		{
			sb.Append(Accent(c));
		}
		sb.Append(' ');
		for (int i = 0; i < padLen; i++)
		{
			// Break every 5th char into a space (never first or last) so wrapping paragraphs behave realistically.
			bool space = i > 0 && i < padLen - 1 && (i % 5 == 4);
			sb.Append(space ? ' ' : '~');
		}
		return sb.ToString();
	}

	private static bool HasAsciiLetter(string text)
	{
		foreach (char c in text)
		{
			if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
			{
				return true;
			}
		}
		return false;
	}

	private static char Accent(char c) => c switch
	{
		'A' => 'Å',
		'C' => 'Ç',
		'D' => 'Ď',
		'E' => 'É',
		'G' => 'Ĝ',
		'H' => 'Ĥ',
		'I' => 'Î',
		'J' => 'Ĵ',
		'K' => 'Ķ',
		'L' => 'Ĺ',
		'N' => 'Ñ',
		'O' => 'Ö',
		'P' => 'Þ',
		'R' => 'Ŕ',
		'S' => 'Ŝ',
		'T' => 'Ţ',
		'U' => 'Û',
		'W' => 'Ŵ',
		'Y' => 'Ý',
		'Z' => 'Ž',
		'a' => 'å',
		'c' => 'ç',
		'd' => 'ď',
		'e' => 'é',
		'g' => 'ĝ',
		'h' => 'ĥ',
		'i' => 'î',
		'j' => 'ĵ',
		'k' => 'ķ',
		'l' => 'ĺ',
		'n' => 'ñ',
		'o' => 'ö',
		'p' => 'þ',
		'r' => 'ŕ',
		's' => 'š',
		'u' => 'û',
		'w' => 'ŵ',
		'y' => 'ý',
		'z' => 'ž',
		_ => c
	};
}
