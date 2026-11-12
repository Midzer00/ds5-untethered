// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia.Data.Converters;

namespace Kopru.App.Views;

// Keeps the last two words of a wrapping line of text together (a no-break space between them), so a wrapped explanation
// never ends with one word alone on its last line, whatever the width.
public sealed class KeepLastWords : IValueConverter
{
	public static KeepLastWords Instance { get; } = new();

	public static string Apply(string text)
	{
		string trimmed = text.TrimEnd();
		int last = trimmed.LastIndexOf(' ');
		// Two words or fewer: nothing to keep together (it would only stop the line from wrapping at all).
		return last > 0 && trimmed.LastIndexOf(' ', last - 1) > 0 ? trimmed[..last] + ' ' + trimmed[(last + 1)..] : text;
	}

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string s ? Apply(s) : value;

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
		value is string s ? s.Replace(' ', ' ') : value;
}
