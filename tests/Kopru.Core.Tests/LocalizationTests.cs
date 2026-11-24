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

	public static TheoryData<string> Languages
	{
		get
		{
			var data = new TheoryData<string>();
			foreach (Language lang in Localizer.Available)
			{
				data.Add(lang.Code);
			}
			return data;
		}
	}

	[Fact]
	public void TextTable_IsNotEmpty() => Assert.True(AllTexts().Count() >= 12);

	[Theory]
	[MemberData(nameof(Languages))]
	public void EveryText_IsNonEmpty(string code)
	{
		Language language = new(code);
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
	[InlineData("tr-TR", "tr")]
	[InlineData("tr", "tr")]
	[InlineData("en-US", "en")]
	[InlineData("de-DE", "en")]
	[InlineData("", "en")]
	public void DefaultLanguage_FollowsUiCulture(string culture, string expectedCode)
	{
		CultureInfo before = CultureInfo.CurrentUICulture;
		try
		{
			Localizer.Chosen = null;
			CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
			Assert.Equal(new Language(expectedCode), Localizer.Current);
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

	// English counts read "1 game" but "2 games"; Turkish has one form.
	[Fact]
	public void Counts_SingularAndPluralInEnglish()
	{
		Assert.Equal("1 game", Text.GameCount(1, Language.English));
		Assert.Equal("2 games", Text.GameCount(2, Language.English));
		Assert.Equal("0 games", Text.GameCount(0, Language.English));
		Assert.Equal("1 oyun", Text.GameCount(1, Language.Turkish));
		Assert.Equal("1 new game added", Text.GameListAdded(1, Language.English));
		Assert.Equal("3 new games added", Text.NotifyGameListBody(3, Language.English));
		Assert.Equal("1 game · updated 9 Nov 2026", Text.GameListInfo(1, "2026-11-09", Language.English));
		Assert.Equal("1 Controller", Text.ControllerCount(1, Language.English));
		Assert.Equal("2 Controllers", Text.ControllerCount(2, Language.English));
		Assert.Equal("1 day ago", Text.AgoDays(1, Language.English));
		Assert.Equal("1 day ago", Text.TimeDaysAgo(1, Language.English));
		Assert.Equal("4 days ago", Text.TimeDaysAgo(4, Language.English));
		Assert.Contains("1 minute without use", Text.PowerOffIdle(1, Language.English));
		Assert.EndsWith("used for 1 minute", Text.IdleTimeout(1, Language.English));
		Assert.EndsWith("used for 10 minutes", Text.IdleTimeout(10, Language.English));
		Assert.StartsWith("1 gap longer", Text.AdapterMeasuredGaps(1, 100, Language.English));
		Assert.EndsWith("1 time.", Text.AdapterLongGaps(1, Language.English));
		Assert.EndsWith("5 times.", Text.AdapterLongGaps(5, Language.English));
	}

	// The PlayStation button is called the "ø button" / "ø tuşu" in every text, in both languages.
	[Fact]
	public void Texts_NeverNameThePsButton()
	{
		var ps = new System.Text.RegularExpressions.Regex(@"\bPS\b");
		foreach ((string name, Func<Language, string> get) in AllTexts())
		{
			foreach (Language language in new[] { Language.Turkish, Language.English })
			{
				string text = get(language);
				Assert.False(ps.IsMatch(text), $"{name} ({language}): {text}");
			}
		}
		Assert.Contains("ø tuşu", Text.ButtonRemappingHint(Language.Turkish));
	}

	[Fact]
	public void EveryKeyInCode_ExistsInEnglishJson_WithMatchingPlaceholders()
	{
		LanguageBundle en = StringCatalog.English;
		Assert.Equal("English", en.NativeName);
		Assert.True(en.Raw.Count >= 1000);

		string? dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		Assert.NotNull(dir);

		var callRegex = new System.Text.RegularExpressions.Regex(@"\bT\(\s*\w+\s*,\s*""([^""]+)""([^;\r\n]*)\)");
		var argRegex = new System.Text.RegularExpressions.Regex(@"\bA\(\s*""([^""]+)""");
		string locDir = Path.Combine(dir!, "src", "Kopru.Core", "Localization");
		int checkedCalls = 0;
		foreach (string file in Directory.EnumerateFiles(locDir, "Text*.cs"))
		{
			string code = File.ReadAllText(file);
			foreach (System.Text.RegularExpressions.Match m in callRegex.Matches(code))
			{
				string key = m.Groups[1].Value;
				Assert.True(en.Messages.TryGetValue(key, out IcuMessage? msg), $"Missing key '{key}' in en.json ({Path.GetFileName(file)})");
				HashSet<string> passedArgs = argRegex.Matches(m.Groups[2].Value).Select(a => a.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
				Assert.Equal(passedArgs, msg!.Placeholders);
				checkedCalls++;
			}
		}
		Assert.True(checkedCalls >= 1000);
	}

	[Fact]
	public void EveryShippedTranslation_MatchesEnglishKeysAndPlaceholders()
	{
		LanguageBundle en = StringCatalog.English;
		IReadOnlyList<string> codes = StringCatalog.EmbeddedCodes();
		Assert.Contains("en", codes);
		Assert.Contains("tr", codes);

		foreach (string code in codes)
		{
			LanguageBundle? bundle = StringCatalog.GetBundle(code);
			Assert.NotNull(bundle);
			Assert.False(string.IsNullOrWhiteSpace(bundle!.NativeName), $"{code}: missing @name");

			foreach ((string key, string raw) in bundle.Raw)
			{
				Assert.True(en.Messages.TryGetValue(key, out IcuMessage? enMsg), $"{code}: unknown key '{key}' (not in en.json)");
				Assert.False(string.IsNullOrWhiteSpace(raw), $"{code}: empty translation for '{key}'");
				Assert.True(IcuMessage.TryParse(raw, out IcuMessage? parsed, out string? error), $"{code} '{key}': invalid ICU ({error})");
				Assert.True(enMsg!.Placeholders.SetEquals(parsed!.Placeholders),
					$"{code} '{key}': placeholders [{string.Join(", ", parsed.Placeholders)}] != en [{string.Join(", ", enMsg.Placeholders)}]");
			}
		}
	}

	[Fact]
	public void MissingLanguageOrKey_EmptyOrMalformedTranslation_FallsBackToEnglish()
	{
		Language missing = new("xx");
		Assert.Equal("Suitable", Text.VerdictSuitable(missing));
		Assert.Equal("4.5% of reports are lost.", Text.AdapterReportsLost(4.5, missing));

		const string partialDe = """
		{
		  "@name": "Deutsch",
		  "Verdict.Suitable": "Geeignet",
		  "Verdict.Borderline": "",
		  "Verdict.Unsuitable": "   ",
		  "Adapter.ReportsLost": "{unclosed",
		  "Adapter.RateLow": "Langsam ({wrongName})"
		}
		""";
		using (StringCatalog.WithOverride("de", partialDe))
		{
			Language de = new("de");
			Assert.Equal("Deutsch", de.NativeName);
			Assert.Equal("Geeignet", Text.VerdictSuitable(de));
			// Empty and whitespace fall back to English.
			Assert.Equal("Borderline", Text.VerdictBorderline(de));
			Assert.Equal("Not suitable", Text.VerdictUnsuitable(de));
			// Malformed ICU and unknown placeholder fall back to English.
			Assert.Equal("4.5% of reports are lost.", Text.AdapterReportsLost(4.5, de));
			Assert.Equal(Text.AdapterRateLow(200, Language.English), Text.AdapterRateLow(200, de));
			// Untranslated key falls back to English.
			Assert.Equal("Settings", Text.NavSettings(de));
		}
	}

	[Fact]
	public void AutomaticCultureMatching_AndUnknownCodeFallback()
	{
		using IDisposable ptBr = StringCatalog.WithOverride("pt-BR", """{ "@name": "Português (Brasil)", "Nav.Settings": "Configurações" }""");
		using IDisposable zhHans = StringCatalog.WithOverride("zh-Hans", """{ "@name": "简体中文", "Nav.Settings": "设置" }""");
		using IDisposable es = StringCatalog.WithOverride("es", """{ "@name": "Español", "Nav.Settings": "Ajustes" }""");

		// Exact match, parent chain, and same two-letter language (pt-PT -> pt-BR).
		Assert.Equal(new Language("pt-BR"), Localizer.FromCulture(CultureInfo.GetCultureInfo("pt-BR")));
		Assert.Equal(new Language("pt-BR"), Localizer.FromCulture(CultureInfo.GetCultureInfo("pt-PT")));
		Assert.Equal(new Language("es"), Localizer.FromCulture(CultureInfo.GetCultureInfo("es-MX")));

		// Simplified Chinese matches zh-CN / zh-Hans, while Traditional Chinese (zh-TW / zh-HK) falls back to English.
		Assert.Equal(new Language("zh-Hans"), Localizer.FromCulture(CultureInfo.GetCultureInfo("zh-Hans")));
		Assert.Equal(new Language("zh-Hans"), Localizer.FromCulture(CultureInfo.GetCultureInfo("zh-CN")));
		Assert.Equal(Language.English, Localizer.FromCulture(CultureInfo.GetCultureInfo("zh-TW")));
		Assert.Equal(Language.English, Localizer.FromCulture(CultureInfo.GetCultureInfo("zh-HK")));

		// Case-insensitive code lookup for shipped languages; unknown codes and test-only 'qps' return null (Automatic).
		Assert.Equal(new Language("pt-BR"), Localizer.FromCode("pt-br"));
		Assert.Equal(new Language("zh-Hans"), Localizer.FromCode("ZH-HANS"));
		Assert.Null(Localizer.FromCode("unknown"));
		Assert.Null(Localizer.FromCode("qps"));
		Assert.Null(Localizer.FromCode(""));
		Assert.Null(Localizer.FromCode(null));
	}

	[Fact]
	public void Cultures_PreserveTurkishCasing_AndFormatNumbersInTargetLanguages()
	{
		CultureInfo tr = Localizer.Culture(Language.Turkish);
		Assert.Equal("İ", "i".ToUpper(tr));
		Assert.Equal("ı", "I".ToLower(tr));

		CultureInfo en = Localizer.Culture(Language.English);
		Assert.Equal("I", "i".ToUpper(en));
		Assert.Equal("i", "I".ToLower(en));

		foreach (string code in new[] { "en", "tr", "zh-Hans", "ru", "es", "pt-BR", "de", "ja", "fr", "pl", "ko" })
		{
			CultureInfo c = Localizer.Culture(new Language(code));
			Assert.False(string.IsNullOrEmpty(12.5.ToString("F1", c)));
			Assert.False(string.IsNullOrEmpty(new DateTime(2026, 11, 9).ToString("d MMM yyyy", c)));
		}
	}

	[Fact]
	public void PseudoLocale_ExpandsWithAccents_AndStaysOutOfShippedList()
	{
		Assert.DoesNotContain(Localizer.Available, l => l.Code.Equals("qps", StringComparison.OrdinalIgnoreCase));
		Assert.Null(Localizer.FromCode("qps"));

		string en = Text.NavSettings(Language.English);
		string qps = Text.NavSettings(Language.Pseudo);
		Assert.StartsWith("[", qps);
		Assert.EndsWith("]", qps);
		Assert.Contains("~", qps);
		Assert.NotEqual(en, qps);
		Assert.True(qps.Length > en.Length);

		// Plurals and placeholders format in English first, then expand.
		string games = Text.GameCount(3, Language.Pseudo);
		Assert.Contains("3", games);
		Assert.StartsWith("[", games);
		Assert.EndsWith("]", games);

		// Average expansion across longer English messages is around +35%.
		var samples = StringCatalog.English.Raw.Values.Where(v => v.Length >= 25 && !v.Contains('{')).Take(100).ToList();
		double avgRatio = samples.Average(s => (double)PseudoLocale.Transform(s).Length / s.Length);
		Assert.InRange(avgRatio, 1.30, 1.42);
	}

	[Fact]
	public void NonEnglishTranslations_KeepTranslatorCasing_AndUseLanguageCulture()
	{
		// Translator casing (sentence case in French/Spanish/Russian, noun casing in German) is preserved verbatim:
		// nothing rewrites non-English labels into English Title Case.
		using IDisposable fr = StringCatalog.WithOverride("fr", """
		{
		  "@name": "Français",
		  "Nav.Settings": "Paramètres rapides",
		  "Back.ToGame": "Retour au jeu",
		  "Adapter.ReportsLost": "{lostPercent} % des rapports sont perdus."
		}
		""");
		using IDisposable de = StringCatalog.WithOverride("de", """
		{
		  "@name": "Deutsch",
		  "Nav.Settings": "Einstellungen und Werkzeuge",
		  "Adapter.ReportsLost": "{lostPercent} % der Berichte gehen verloren."
		}
		""");
		using IDisposable ru = StringCatalog.WithOverride("ru", """
		{
		  "@name": "Русский",
		  "Nav.Settings": "Быстрые настройки",
		  "Adapter.ReportsLost": "Потеряно {lostPercent}% отчётов."
		}
		""");

		Assert.Equal("Paramètres rapides", Text.NavSettings(new Language("fr")));
		Assert.Equal("Retour au jeu", Text.BackToGame(new Language("fr")));
		Assert.Equal("Einstellungen und Werkzeuge", Text.NavSettings(new Language("de")));
		Assert.Equal("Быстрые настройки", Text.NavSettings(new Language("ru")));

		// Decimal separator follows each language's own CultureInfo.
		Assert.Equal("4,5 % des rapports sont perdus.", Text.AdapterReportsLost(4.5, new Language("fr")));
		Assert.Equal("4,5 % der Berichte gehen verloren.", Text.AdapterReportsLost(4.5, new Language("de")));
		Assert.Equal("Потеряно 4,5% отчётов.", Text.AdapterReportsLost(4.5, new Language("ru")));
		Assert.Equal("Raporların %4,5'i kayboluyor.", Text.AdapterReportsLost(4.5, Language.Turkish));
		Assert.Equal("4.5% of reports are lost.", Text.AdapterReportsLost(4.5, Language.English));
	}

	[Fact]
	public async Task LogsDiagnosticsAndUpdateNotes_StayEnglishForOtherLanguages()
	{
		// Update notes keep tr/en only: Turkish gets NotesTr, English and every other language get NotesEn.
		var asset = new Kopru.Core.Updates.UpdateAsset(
			"https://github.com/Midzer00/ds5-untethered/releases/download/v0.2.2/DS5-Untethered-0.2.2-Setup.exe",
			new string('A', 64),
			1024);
		Assert.True(Kopru.Core.Updates.AppVersion.TryParse("0.2.2", out Kopru.Core.Updates.AppVersion version));
		var manifest = new Kopru.Core.Updates.UpdateManifest(
			version,
			"2026-11-23",
			asset,
			null,
			"https://github.com/Midzer00/ds5-untethered/releases/tag/v0.2.2",
			"Türkçe notlar",
			"English release notes",
			null);

		Assert.Equal("Türkçe notlar", manifest.Notes(Language.Turkish));
		foreach (string code in new[] { "en", "de", "fr", "ja", "zh-Hans", "ru", "pt-BR", "qps" })
		{
			Assert.Equal("English release notes", manifest.Notes(new Language(code)));
			Assert.Contains("/LANG=en", Kopru.Core.Updates.UpdateInstaller.Arguments(code, "install.log"));
		}
		Assert.Contains("/LANG=tr", Kopru.Core.Updates.UpdateInstaller.Arguments("tr", "install.log"));

		// Exported diagnostics stay English regardless of the active UI language.
		Language? before = Localizer.Chosen;
		string tempDir = Path.Combine(Path.GetTempPath(), "kopru-i18n-diag-" + Guid.NewGuid().ToString("N")[..8]);
		try
		{
			Localizer.Chosen = Language.Pseudo;
			string zipPath = Path.Combine(tempDir, "diag.zip");
			var sources = new DiagnosticsSources
			{
				Controller = new ControllerInfo(0x054C, 0x0CE6, false, Kopru.Core.Hid.Transport.Bluetooth, "path")
			};
			await DiagnosticsExport.CreateZipAsync(zipPath, sources);
			using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
			using var sysReader = new StreamReader(archive.GetEntry("system.txt")!.Open());
			string systemText = await sysReader.ReadToEndAsync();
			Assert.Contains("App: DS5 Untethered", systemText);
			Assert.DoesNotContain("~", systemText);

			using var ctrlReader = new StreamReader(archive.GetEntry("controller.txt")!.Open());
			string controllerText = await ctrlReader.ReadToEndAsync();
			Assert.Contains("Transport: Bluetooth", controllerText);
			Assert.Contains("Firmware feature: not read", controllerText);
		}
		finally
		{
			Localizer.Chosen = before;
			if (Directory.Exists(tempDir))
			{
				Directory.Delete(tempDir, recursive: true);
			}
		}
	}
}
