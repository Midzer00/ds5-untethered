// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Reflection;
using Kopru.Core.Diagnostics;
using Kopru.Core.Localization;
using Xunit.Sdk;

[assembly: Kopru.Core.Tests.CultureFromEnvironment]

namespace Kopru.Core.Tests;

// Set KOPRU_TEST_CULTURE (e.g. tr-TR, en-US) to run the whole suite under that culture.
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class CultureFromEnvironmentAttribute : BeforeAfterTestAttribute
{
	private CultureInfo? _culture;
	private CultureInfo? _uiCulture;

	public override void Before(MethodInfo methodUnderTest)
	{
		string? name = Environment.GetEnvironmentVariable("KOPRU_TEST_CULTURE");
		if (string.IsNullOrWhiteSpace(name))
		{
			return;
		}
		_culture = CultureInfo.CurrentCulture;
		_uiCulture = CultureInfo.CurrentUICulture;
		CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
	}

	public override void After(MethodInfo methodUnderTest)
	{
		if (_culture != null && _uiCulture != null)
		{
			CultureInfo.CurrentCulture = _culture;
			CultureInfo.CurrentUICulture = _uiCulture;
		}
	}
}

[CollectionDefinition("Localization", DisableParallelization = true)]
public class LocalizationCollection;

[Collection("Localization")]
public class LocalizationTests
{
	private static IEnumerable<(string Name, Func<Language, string> Get)> AllTexts() =>
		typeof(Text).GetMethods(BindingFlags.Public | BindingFlags.Static)
			.Where(m => m.ReturnType == typeof(string) && !m.IsSpecialName)
			.Select(m => (m.Name, (Func<Language, string>)(lang =>
			{
				object?[] args = m.GetParameters().Select(p => SampleArgument(p.ParameterType, lang)).ToArray();
				return (string)m.Invoke(null, args)!;
			})));

	// Supplies a representative value for every parameter type used by Text.*.cs.
	private static object? SampleArgument(Type type, Language lang)
	{
		Type t = Nullable.GetUnderlyingType(type) ?? type;
		if (t == typeof(Language)) return lang;
		if (t == typeof(string)) return "x";
		if (t == typeof(double)) return 4.5;
		if (t == typeof(float)) return 4.5f;
		if (t == typeof(int)) return 4;
		if (t == typeof(long)) return 4L;
		if (t == typeof(bool)) return true;
		if (t == typeof(TimeSpan)) return TimeSpan.FromSeconds(90);
		if (t.IsEnum) return Enum.GetValues(t).GetValue(0);
		throw new NotSupportedException($"No sample argument for parameter type {type}.");
	}

	public static TheoryData<Language> Languages => new() { Language.Turkish, Language.English };

	[Fact]
	public void TextTable_IsNotEmpty() => Assert.True(AllTexts().Count() >= 12);

	[Theory]
	[MemberData(nameof(Languages))]
	public void EveryText_IsNonEmpty(Language language)
	{
		foreach ((string name, Func<Language, string> get) in AllTexts())
		{
			Assert.False(string.IsNullOrWhiteSpace(get(language)), name);
		}
	}

	[Fact]
	public void EveryText_DiffersBetweenLanguages()
	{
		foreach ((string name, Func<Language, string> get) in AllTexts())
		{
			Assert.NotEqual(get(Language.Turkish), get(Language.English));
		}
	}

	[Fact]
	public void Verdicts_AreTranslated()
	{
		Assert.Equal("Uygun", Text.VerdictSuitable(Language.Turkish));
		Assert.Equal("Sınırda", Text.VerdictBorderline(Language.Turkish));
		Assert.Equal("Uygun değil", Text.VerdictUnsuitable(Language.Turkish));
		Assert.Equal("Suitable", Text.VerdictSuitable(Language.English));
		Assert.Equal("Borderline", Text.VerdictBorderline(Language.English));
		Assert.Equal("Not suitable", Text.VerdictUnsuitable(Language.English));
	}

	[Fact]
	public void AdapterReasons_UseLanguageAndNumberFormat()
	{
		LinkResult good = AdapterGradeTests.Link(700);
		LinkResult lossy = AdapterGradeTests.Link(650, lost: 225, reports: 4775); // 4.5 %
		AdapterReport tr = AdapterCheck.Grade(good, lossy, 94, Language.Turkish);
		AdapterReport en = AdapterCheck.Grade(good, lossy, 94, Language.English);
		Assert.Contains("Raporların %4,5'i kayboluyor.", tr.Reasons);
		Assert.Contains("4.5% of reports are lost.", en.Reasons);
		Assert.Equal("Uygun değil", tr.VerdictTextIn(Language.Turkish));
		Assert.Equal("Not suitable", en.VerdictTextIn(Language.English));
		Assert.Contains(AdapterCheck.Grade(good, good, 94, Language.English).Reasons, r => r.Contains("reports/s"));
	}

	[Fact]
	public void AdapterReasons_FollowChosenLanguage()
	{
		try
		{
			Localizer.Chosen = Language.English;
			Assert.Contains(AdapterCheck.Grade(AdapterGradeTests.Link(700), AdapterGradeTests.Link(650), 70).Reasons, r => r.Contains("haptics"));
			Assert.Equal("Not suitable", AdapterCheck.Grade(AdapterGradeTests.Link(700), AdapterGradeTests.Link(650), 70).VerdictText);
			Localizer.Chosen = Language.Turkish;
			Assert.Contains(AdapterCheck.Grade(AdapterGradeTests.Link(700), AdapterGradeTests.Link(650), 70).Reasons, r => r.Contains("Haptik"));
		}
		finally
		{
			Localizer.Chosen = null;
		}
	}

	[Theory]
	[InlineData("tr-TR", Language.Turkish)]
	[InlineData("tr", Language.Turkish)]
	[InlineData("en-US", Language.English)]
	[InlineData("de-DE", Language.English)]
	[InlineData("", Language.English)]
	public void DefaultLanguage_FollowsUiCulture(string culture, Language expected)
	{
		CultureInfo before = CultureInfo.CurrentUICulture;
		try
		{
			Localizer.Chosen = null;
			CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
			Assert.Equal(expected, Localizer.Current);
		}
		finally
		{
			CultureInfo.CurrentUICulture = before;
		}
	}

	[Fact]
	public void SwitchingLanguage_RaisesEvent()
	{
		List<Language> seen = [];
		void Handler(Language l) => seen.Add(l);
		Localizer.LanguageChanged += Handler;
		try
		{
			Localizer.Chosen = Language.Turkish;
			Localizer.Chosen = Language.English;
			Localizer.Chosen = Language.English; // no change, no event
			Localizer.Chosen = Language.Turkish;
		}
		finally
		{
			// Unsubscribe first: going back to "follow the OS" may itself change the language and raise the event.
			Localizer.LanguageChanged -= Handler;
			Localizer.Chosen = null;
		}
		Assert.Equal(Language.Turkish, seen[^1]);
		Assert.Contains(Language.English, seen);
		Assert.Equal(seen.Count, seen.Zip(seen.Skip(1)).Count(p => p.First != p.Second) + 1);
	}

	[Fact]
	public void LanguageCodes_RoundTrip()
	{
		Assert.Equal(Language.Turkish, Localizer.FromCode(Localizer.Code(Language.Turkish)));
		Assert.Equal(Language.English, Localizer.FromCode("EN"));
		Assert.Null(Localizer.FromCode("fr"));
	}
}
