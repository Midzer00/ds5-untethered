// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The translation context for the translate page (ds5untethered.com/translate/): an English picture of every page, tab
// and dialog, and a manifest of the boxes where each catalogue key is drawn (tools/i18n/context.ps1). The pictures come
// from the plain English render; the boxes from the same states rendered in the key tag locale, whose layout must match.
// Does nothing unless KOPRU_I18N_CONTEXT_DIR is set.
public sealed class TranslateContextTests
{
	internal const double LayoutTolerance = 1.0;

	[AvaloniaFact]
	public async Task RenderTranslateContext()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_I18N_CONTEXT_DIR") is { Length: > 0 } d ? d : null;
		if (outDir == null)
		{
			return;
		}
		double scale = Environment.GetEnvironmentVariable("KOPRU_I18N_CONTEXT_SCALE") is { Length: > 0 } s
			&& double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed is >= 1 and <= 3 ? parsed : 2;
		Directory.CreateDirectory(outDir);
		foreach (string old in Directory.EnumerateFiles(outDir, "*.webp").Concat(Directory.EnumerateFiles(outDir, "*.png")))
		{
			File.Delete(old);
		}

		ContextRender render = await ContextRender.Run(outDir, scale);
		Assert.True(render.Problems.Count == 0, string.Join(Environment.NewLine, render.Problems));
		ContextManifest.Write(outDir, render, scale);
	}

	// Each catalogue value round-trips through its markers, nested and cut values included.
	[Fact]
	public void KeyTags_EncodeAndDecode_RoundTrip()
	{
		IReadOnlyList<string> keys = KeyTagLocale.Keys;
		Assert.True(keys.Count > 100);
		foreach (string key in new[] { keys[0], keys[^1], keys[keys.Count / 2], "Nav.Overview" })
		{
			string tagged = KeyTagLocale.Wrap(key, "Some text");
			Assert.NotEqual("Some text", tagged);
			KeyTagDecoding decoding = KeyTagLocale.Decode(tagged);
			Assert.Equal("Some text", decoding.Clean);
			KeyTagSpan span = Assert.Single(decoding.Spans);
			Assert.Equal(key, span.Key);
			Assert.Equal((0, 9, false), (span.CleanStart, span.CleanLength, span.Partial));
			Assert.Equal("Some text", tagged.Substring(span.TaggedStart, span.TaggedLength));
		}

		// A value formatted into another: two spans, the inner one its own part.
		string inner = KeyTagLocale.Wrap("Nav.Light", "Light");
		string outer = KeyTagLocale.Wrap("Nav.Settings", "Open " + inner + " now");
		KeyTagDecoding nested = KeyTagLocale.Decode(outer);
		Assert.Equal("Open Light now", nested.Clean);
		Assert.Equal(2, nested.Spans.Count);
		Assert.Contains(nested.Spans, s => s is { Key: "Nav.Settings", CleanStart: 0, CleanLength: 14 });
		Assert.Contains(nested.Spans, s => s is { Key: "Nav.Light", CleanStart: 5, CleanLength: 5 });

		// Two values side by side.
		KeyTagDecoding pair = KeyTagLocale.Decode(KeyTagLocale.Wrap("Nav.Light", "Light") + " · " + KeyTagLocale.Wrap("Nav.Overview", "Overview"));
		Assert.Equal("Light · Overview", pair.Clean);
		Assert.Equal(["Nav.Light", "Nav.Overview"], pair.Spans.Select(s => s.Key));

		// A value cut after its first marker (code that keeps the part after ": ") still names its key.
		string cut = KeyTagLocale.Wrap("Nav.Light", "Label: rest");
		KeyTagDecoding tail = KeyTagLocale.Decode(cut[(cut.IndexOf(": ", StringComparison.Ordinal) + 2)..]);
		Assert.Equal("rest", tail.Clean);
		Assert.Equal(("Nav.Light", true), (Assert.Single(tail.Spans).Key, tail.Spans[0].Partial));

		// Through the catalogue: placeholders are filled and the tag stays.
		string version = Text.AboutVersion("1.2.3", KeyTagLocale.Language);
		KeyTagDecoding formatted = KeyTagLocale.Decode(version);
		Assert.Equal(Text.AboutVersion("1.2.3", Language.English), formatted.Clean);
		Assert.Equal("About.Version", Assert.Single(formatted.Spans).Key);
		Assert.True(KeyTagScanner.IsFormatted("About.Version"));
		Assert.False(KeyTagScanner.IsFormatted("Nav.Overview"));
		Assert.Equal(Text.NavOverview(Language.English), KeyTagLocale.Strip(Text.NavOverview(KeyTagLocale.Language)));
	}

	// The key tag locale is render-only: not a shipped language, not accepted from settings, not in the language picker.
	[AvaloniaFact]
	public void KeyTagLocale_IsNotSelectable()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Assert.DoesNotContain(Localizer.Available, l => KeyTagLocale.IsKeyTag(l.Code));
			Assert.Null(Localizer.FromCode(KeyTagLocale.Code));
			Assert.Null(Localizer.FromCode("QTG"));

			var store = new MemorySettingsStore(new AppSettings { Language = KeyTagLocale.Code });
			var vm = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(store));
			Assert.Null(Localizer.Chosen);
			Assert.DoesNotContain(vm.SettingsPage.Languages, o => o.Language is { } l && KeyTagLocale.IsKeyTag(l.Code));
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// A small page: every key's box is where its text is drawn, a text built from two keys gives both keys their own part,
	// the tagged render lays out like the plain one, and boxes measured on the plain layout match the tagged ones.
	[AvaloniaFact]
	public void SmallPage_GivesEachKeyItsBox_AndMatchesThePlainLayout()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			(Window plain, _, _, _) = SmallPage();
			IReadOnlyList<(string Text, Rect Box, Size Layout)> plainLayout = KeyTagScanner.LayoutSignature(plain);
			IReadOnlyList<PlainText> plainTexts = KeyTagScanner.PlainTexts(plain);
			plain.Close();

			Localizer.Chosen = KeyTagLocale.Language;
			(Window tagged, TextBlock title, TextBlock line, TextBlock wrapped) = SmallPage();
			ContextScan scan = KeyTagScanner.Scan(tagged);
			ContextScan onPlain = KeyTagScanner.Scan(tagged, plainTexts);
			IReadOnlyList<(string Text, Rect Box, Size Layout)> taggedLayout = KeyTagScanner.LayoutSignature(tagged);
			tagged.Close();

			Assert.Empty(ContextRender.LayoutDifferences(plainLayout, taggedLayout));
			Assert.Equal(scan.Items.Select(i => i.Key), onPlain.Items.Select(i => i.Key));
			foreach ((ContextItem a, ContextItem b) in scan.Items.Zip(onPlain.Items))
			{
				Assert.True(Math.Abs(a.Bounds.X - b.Bounds.X) <= LayoutTolerance && Math.Abs(a.Bounds.Right - b.Bounds.Right) <= LayoutTolerance
					&& Math.Abs(a.Bounds.Y - b.Bounds.Y) <= LayoutTolerance && Math.Abs(a.Bounds.Bottom - b.Bounds.Bottom) <= LayoutTolerance,
					$"{a.Key}: {a.Bounds} tagged, {b.Bounds} on the plain layout");
			}

			ContextItem overview = Assert.Single(scan.Items, i => i.Key == "Nav.Overview");
			Rect titleBox = KeyTagScanner.ToWindow(title, new Rect(title.Bounds.Size), tagged);
			Assert.InRange(overview.Bounds.X, titleBox.X - 0.5, titleBox.X + 0.5);
			Assert.InRange(overview.Bounds.Y, titleBox.Y - 0.5, titleBox.Y + 2);
			Assert.InRange(overview.Bounds.Width, title.TextLayout.Width - 1, title.TextLayout.Width + 1);
			Assert.Equal((20.0, 600, false, false, false), (overview.Size, overview.Weight, overview.Wrap, overview.Composite, overview.Formatted));
			Assert.Equal(300 - title.Margin.Left, overview.MaxW, 1);

			// "Light · Settings" drawn from two keys: each key has its own part, side by side on one line.
			ContextItem light = Assert.Single(scan.Items, i => i.Key == "Nav.Light");
			ContextItem settings = Assert.Single(scan.Items, i => i.Key == "Nav.Settings");
			Assert.True(light.Composite && settings.Composite);
			Assert.True(light.Bounds.Right <= settings.Bounds.Left + 0.5, $"{light.Bounds} then {settings.Bounds}");
			Assert.Equal(light.Bounds.Y, settings.Bounds.Y, 1);
			Rect lineBox = KeyTagScanner.ToWindow(line, new Rect(line.Bounds.Size), tagged);
			Assert.True(lineBox.Contains(light.Bounds.Center) && lineBox.Contains(settings.Bounds.Center));

			// The wrapped paragraph: its lines, its width, a formatted value.
			ContextItem about = Assert.Single(scan.Items, i => i.Key == "About.Version");
			Assert.True(about.Wrap && about.Formatted);
			Assert.Equal(160, about.MaxW, 1);
			Assert.True(wrapped.TextLayout.TextLines.Count > 1);
			Assert.True(about.MaxLines >= wrapped.TextLayout.TextLines.Count);
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static (Window Window, TextBlock Title, TextBlock Line, TextBlock Wrapped) SmallPage()
	{
		var title = new TextBlock { Text = Text.NavOverview(), FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(12, 0, 0, 0) };
		var line = new TextBlock { Text = Text.NavLight() + " · " + Text.NavSettings(), FontSize = 13, TextWrapping = TextWrapping.NoWrap };
		var wrapped = new TextBlock
		{
			Text = Text.AboutVersion("0.2.2, a long build note that wraps over a few lines in a narrow box"),
			TextWrapping = TextWrapping.Wrap,
			Width = 160,
			FontSize = 13
		};
		var panel = new StackPanel { Width = 300, Spacing = 8, Children = { title, line, wrapped } };
		var window = new Window { Width = 400, Height = 300, Content = new Border { Padding = new Thickness(20), Child = panel } };
		window.Show();
		FitCheck.Settle(window);
		return (window, title, line, wrapped);
	}

	// SHA-256 of en.json as published (LF line endings): a Windows checkout may have CRLF, the web page hashes the file
	// from GitHub.
	internal static string StringsHash()
	{
		string path = Path.Combine(AppTests.RepoRoot(), "src", "Kopru.Core", "Localization", "strings", "en.json");
		byte[] bytes = File.ReadAllBytes(path);
		using var lf = new MemoryStream(bytes.Length);
		for (int i = 0; i < bytes.Length; i++)
		{
			if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
			{
				continue;
			}
			lf.WriteByte(bytes[i]);
		}
		return Convert.ToHexString(SHA256.HashData(lf.ToArray())).ToLowerInvariant();
	}

	internal static string AppVersion()
	{
		string props = Path.Combine(AppTests.RepoRoot(), "Directory.Build.props");
		return XDocument.Load(props).Descendants("Version").First().Value.Trim();
	}
}
