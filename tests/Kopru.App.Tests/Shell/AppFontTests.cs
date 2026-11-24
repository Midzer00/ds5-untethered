// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

public sealed class AppFontTests
{
	[AvaloniaTheory]
	[InlineData("zh-Hans", "设置 · 无线控制器已连接 · ø")]
	[InlineData("zh-Hant", "設定 · 無線控制器已連線 · ø")]
	[InlineData("ja", "設定 · ワイヤレスコントローラー接続済み · ø")]
	[InlineData("ko", "설정 · 무선 컨트롤러 연결됨 · ø")]
	[InlineData("en", "Settings · 简体中文 · 繁體中文 · 日本語 · 한국어 · ø")]
	public void CjkStrings_RenderWithRealGlyphs_NoTofuInHeadlessRender(string code, string sample)
	{
		Language? before = Localizer.Chosen;
		try
		{
			using (StringCatalog.WithOverride(code, $$"""{ "@name": "{{code}}" }"""))
			{
				Localizer.Chosen = new Language(code);
				var block = new TextBlock
				{
					Text = sample,
					FontSize = 20,
					Foreground = Brushes.White,
					HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
					VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
				};
				var window = new Window
				{
					Width = 640,
					Height = 120,
					Background = Brushes.Black,
					Content = block,
				};
				window.Show();
				try
				{
					FitCheck.Settle(window);

					// 1. Every shaped glyph in the laid-out TextBlock must resolve to a real glyph (GlyphIndex != 0; 0 is .notdef/tofu).
					int glyphCount = 0;
					foreach (TextLine line in block.TextLayout.TextLines)
					{
						foreach (TextRun run in line.TextRuns)
						{
							if (run is ShapedTextRun shaped)
							{
								IGlyphTypeface typeface = shaped.GlyphRun.GlyphTypeface;
								Assert.False(string.IsNullOrWhiteSpace(typeface.FamilyName));
								for (int i = 0; i < shaped.ShapedBuffer.Length; i++)
								{
									ushort glyph = shaped.ShapedBuffer[i].GlyphIndex;
									Assert.True(glyph != 0, $"{code}: tofu (.notdef glyph 0) in '{sample}' using font '{typeface.FamilyName}'");
									glyphCount++;
								}
							}
						}
					}
					Assert.True(glyphCount >= sample.Count(c => !char.IsWhiteSpace(c)), $"{code}: expected shaped glyphs, got {glyphCount}");

					// 2. Also verify FontManager character matching directly for every CJK codepoint in the sample.
					foreach (Rune rune in sample.EnumerateRunes())
					{
						if (rune.Value >= 0x3000)
						{
							Assert.True(
								FontManager.Current.TryMatchCharacter(rune.Value, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, block.FontFamily, Localizer.Culture(new Language(code)), out Typeface matched),
								$"{code}: FontManager could not match U+{rune.Value:X4} '{rune}'");
							ushort glyph = matched.GlyphTypeface.GetGlyph((uint)rune.Value);
							Assert.True(glyph != 0, $"{code}: U+{rune.Value:X4} '{rune}' resolved to .notdef glyph 0 in '{matched.GlyphTypeface.FamilyName}'");
						}
					}

					// 3. Headless frame capture actually draws visible glyph pixels onto the black surface.
					using var frame = window.CaptureRenderedFrame();
					Assert.NotNull(frame);
					int litPixels = CountNonBlackPixels(frame!);
					Assert.True(litPixels > 100, $"{code}: headless render drew only {litPixels} non-black pixels for '{sample}'");
				}
				finally
				{
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	[AvaloniaTheory]
	[InlineData("zh-Hans", "Microsoft YaHei UI")]
	[InlineData("zh-Hant", "Microsoft JhengHei UI")]
	[InlineData("ja", "Yu Gothic UI")]
	[InlineData("ko", "Malgun Gothic")]
	[InlineData("en", "Inter")]
	[InlineData("tr", "Inter")]
	public void AppFontFamilyResource_UpdatesWithActiveLanguage(string code, string expectedPrimaryFamily)
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = new Language(code);
			Assert.NotNull(Application.Current);
			Assert.True(Application.Current!.Resources.TryGetResource("AppFontFamily", null, out object? res));
			var family = Assert.IsType<FontFamily>(res);
			Assert.Equal(expectedPrimaryFamily, family.FamilyNames.PrimaryFamilyName);
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static int CountNonBlackPixels(Avalonia.Media.Imaging.Bitmap bitmap)
	{
		PixelSize size = bitmap.PixelSize;
		byte[] pixels = new byte[size.Width * size.Height * 4];
		GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
		try
		{
			bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), handle.AddrOfPinnedObject(), pixels.Length, size.Width * 4);
		}
		finally
		{
			handle.Free();
		}
		int lit = 0;
		for (int i = 0; i < pixels.Length; i += 4)
		{
			if (pixels[i] > 16 || pixels[i + 1] > 16 || pixels[i + 2] > 16)
			{
				lit++;
			}
		}
		return lit;
	}
}
