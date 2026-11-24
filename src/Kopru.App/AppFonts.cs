// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Media;
using Kopru.Core.Localization;

namespace Kopru.App;

public static class AppFonts
{
	public const string Inter = "fonts:Inter#Inter";

	// Windows CJK UI fonts shipped with Windows 10 and 11: Simplified Chinese, Traditional Chinese, Japanese, Korean.
	public static readonly IReadOnlyList<FontFallback> Fallbacks =
	[
		new FontFallback { FontFamily = new FontFamily("Microsoft YaHei UI") },
		new FontFallback { FontFamily = new FontFamily("Microsoft YaHei") },
		new FontFallback { FontFamily = new FontFamily("Microsoft JhengHei UI") },
		new FontFallback { FontFamily = new FontFamily("Yu Gothic UI") },
		new FontFallback { FontFamily = new FontFamily("Yu Gothic") },
		new FontFallback { FontFamily = new FontFamily("Malgun Gothic") },
	];

	public static AppBuilder WithUiFonts(this AppBuilder builder) =>
		builder.WithInterFont().With(new FontManagerOptions
		{
			DefaultFamilyName = Inter,
			FontFallbacks = Fallbacks
		});

	public static FontFamily ForLanguage(Language language)
	{
		string code = language.Code;
		if (code.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
		{
			return new FontFamily("Yu Gothic UI, Yu Gothic, Microsoft YaHei UI, Malgun Gothic");
		}
		if (code.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
		{
			return new FontFamily("Malgun Gothic, Microsoft YaHei UI, Yu Gothic UI");
		}
		if (code.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
			|| code.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
			|| code.Equals("zh-HK", StringComparison.OrdinalIgnoreCase))
		{
			return new FontFamily("Microsoft JhengHei UI, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic");
		}
		if (code.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
		{
			return new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Yu Gothic UI, Malgun Gothic");
		}
		return new FontFamily(Inter);
	}
}
