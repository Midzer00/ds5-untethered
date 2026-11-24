// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Localization;

namespace Kopru.Core.Tests;

[Collection("Localization")]
public class PluralRulesTests
{
	private static PluralCategory Cat(string lang, string formatted)
	{
		Assert.True(PluralOperands.TryParse(formatted, out PluralOperands op));
		return PluralRules.Select(lang, op);
	}

	[Theory]
	[InlineData("0", PluralCategory.Other)]
	[InlineData("1", PluralCategory.One)]
	[InlineData("-1", PluralCategory.One)]
	[InlineData("1.0", PluralCategory.Other)]
	[InlineData("1.5", PluralCategory.Other)]
	[InlineData("2", PluralCategory.Other)]
	[InlineData("21", PluralCategory.Other)]
	public void EnglishAndGerman_OneOnlyForIntegerOne(string number, PluralCategory expected)
	{
		Assert.Equal(expected, Cat("en", number));
		Assert.Equal(expected, Cat("de", number));
	}

	[Theory]
	[InlineData("0", PluralCategory.Other)]
	[InlineData("1", PluralCategory.One)]
	[InlineData("1.0", PluralCategory.One)]
	[InlineData("1.5", PluralCategory.Other)]
	[InlineData("2", PluralCategory.Other)]
	public void Turkish_OneWhenNumericValueIsOne(string number, PluralCategory expected) =>
		Assert.Equal(expected, Cat("tr", number));

	[Theory]
	[InlineData("zh-Hans")]
	[InlineData("zh")]
	[InlineData("ja")]
	[InlineData("ko")]
	public void ChineseJapaneseKorean_AlwaysOther(string lang)
	{
		foreach (string n in new[] { "0", "1", "2", "5", "11", "21", "100", "1.5" })
		{
			Assert.Equal(PluralCategory.Other, Cat(lang, n));
		}
	}

	[Theory]
	[InlineData("0", PluralCategory.Other)]
	[InlineData("1", PluralCategory.One)]
	[InlineData("1.0", PluralCategory.One)]
	[InlineData("2", PluralCategory.Other)]
	[InlineData("1000000", PluralCategory.Many)]
	[InlineData("2000000", PluralCategory.Many)]
	[InlineData("1000000.0", PluralCategory.Other)]
	public void Spanish_OneManyAndOther(string number, PluralCategory expected) =>
		Assert.Equal(expected, Cat("es", number));

	[Theory]
	[InlineData("pt-BR")]
	[InlineData("fr")]
	public void BrazilianPortugueseAndFrench_ZeroAndOneIntegerPartAreOne(string lang)
	{
		Assert.Equal(PluralCategory.One, Cat(lang, "0"));
		Assert.Equal(PluralCategory.One, Cat(lang, "0.5"));
		Assert.Equal(PluralCategory.One, Cat(lang, "1"));
		Assert.Equal(PluralCategory.One, Cat(lang, "1.5"));
		Assert.Equal(PluralCategory.Other, Cat(lang, "2"));
		Assert.Equal(PluralCategory.Other, Cat(lang, "2.5"));
		Assert.Equal(PluralCategory.Many, Cat(lang, "1000000"));
	}

	[Theory]
	[InlineData("0", PluralCategory.Many)]
	[InlineData("1", PluralCategory.One)]
	[InlineData("2", PluralCategory.Few)]
	[InlineData("3", PluralCategory.Few)]
	[InlineData("4", PluralCategory.Few)]
	[InlineData("5", PluralCategory.Many)]
	[InlineData("11", PluralCategory.Many)]
	[InlineData("12", PluralCategory.Many)]
	[InlineData("14", PluralCategory.Many)]
	[InlineData("20", PluralCategory.Many)]
	[InlineData("21", PluralCategory.One)]
	[InlineData("22", PluralCategory.Few)]
	[InlineData("24", PluralCategory.Few)]
	[InlineData("25", PluralCategory.Many)]
	[InlineData("101", PluralCategory.One)]
	[InlineData("111", PluralCategory.Many)]
	[InlineData("112", PluralCategory.Many)]
	[InlineData("1.0", PluralCategory.Other)]
	[InlineData("1.5", PluralCategory.Other)]
	[InlineData("2.1", PluralCategory.Other)]
	public void Russian_OneFewManyAndFractionalOther(string number, PluralCategory expected) =>
		Assert.Equal(expected, Cat("ru", number));

	[Theory]
	[InlineData("0", PluralCategory.Many)]
	[InlineData("1", PluralCategory.One)]
	[InlineData("2", PluralCategory.Few)]
	[InlineData("4", PluralCategory.Few)]
	[InlineData("5", PluralCategory.Many)]
	[InlineData("11", PluralCategory.Many)]
	[InlineData("12", PluralCategory.Many)]
	[InlineData("14", PluralCategory.Many)]
	[InlineData("21", PluralCategory.Many)]
	[InlineData("22", PluralCategory.Few)]
	[InlineData("24", PluralCategory.Few)]
	[InlineData("25", PluralCategory.Many)]
	[InlineData("101", PluralCategory.Many)]
	[InlineData("112", PluralCategory.Many)]
	[InlineData("1.5", PluralCategory.Other)]
	public void Polish_OneOnlyForOne_FewManyAndFractionalOther(string number, PluralCategory expected) =>
		Assert.Equal(expected, Cat("pl", number));

	[Fact]
	public void EndToEndFormatting_UsesCldrCategoriesAndFallsBackToOther()
	{
		using IDisposable ru = StringCatalog.WithOverride("ru", """
		{
		  "@name": "Русский",
		  "Game.Count": "{count, plural, one {# игра} few {# игры} many {# игр} other {# игры}}"
		}
		""");
		using IDisposable pl = StringCatalog.WithOverride("pl", """
		{
		  "@name": "Polski",
		  "Game.Count": "{count, plural, one {# gra} few {# gry} many {# gier} other {# gry}}"
		}
		""");
		using IDisposable fr = StringCatalog.WithOverride("fr", """
		{
		  "@name": "Français",
		  "Game.Count": "{count, plural, one {# jeu} other {# jeux}}"
		}
		""");
		using IDisposable ja = StringCatalog.WithOverride("ja", """
		{
		  "@name": "日本語",
		  "Game.Count": "{count} 本のゲーム"
		}
		""");

		Language ruLang = new("ru");
		Assert.Equal("0 игр", Text.GameCount(0, ruLang));
		Assert.Equal("1 игра", Text.GameCount(1, ruLang));
		Assert.Equal("2 игры", Text.GameCount(2, ruLang));
		Assert.Equal("5 игр", Text.GameCount(5, ruLang));
		Assert.Equal("11 игр", Text.GameCount(11, ruLang));
		Assert.Equal("21 игра", Text.GameCount(21, ruLang));
		Assert.Equal("24 игры", Text.GameCount(24, ruLang));

		Language plLang = new("pl");
		Assert.Equal("1 gra", Text.GameCount(1, plLang));
		Assert.Equal("3 gry", Text.GameCount(3, plLang));
		Assert.Equal("5 gier", Text.GameCount(5, plLang));
		Assert.Equal("21 gier", Text.GameCount(21, plLang));
		Assert.Equal("22 gry", Text.GameCount(22, plLang));

		// French: 0 and 1 use 'one', 2 and 1,000,000 (many falls back to 'other' when 'many' is omitted) use 'other'.
		Language frLang = new("fr");
		Assert.Equal("0 jeu", Text.GameCount(0, frLang));
		Assert.Equal("1 jeu", Text.GameCount(1, frLang));
		Assert.Equal("2 jeux", Text.GameCount(2, frLang));

		Language jaLang = new("ja");
		Assert.Equal("1 本のゲーム", Text.GameCount(1, jaLang));
		Assert.Equal("5 本のゲーム", Text.GameCount(5, jaLang));
	}
}
