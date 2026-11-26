// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kopru.Core.Localization;
using SkiaSharp;

namespace Kopru.App.Tests;

internal sealed record ContextShotResult(ContextShot Shot, string Image, IReadOnlyList<ContextItem> Items, IReadOnlyList<string> Untagged);

// Both renders of the translation context: the plain English pass saves the pictures, the key tag pass reads the boxes,
// and every shot's text layout must be the same in both (within LayoutTolerance).
internal sealed class ContextRender
{
	public List<ContextShotResult> Shots { get; } = [];
	public List<string> Problems { get; } = [];
	// The largest laid-out text width difference between the renders over all shots (kerning across markers).
	public double MaxWidthDifferenceSeen { get; private set; }

	public static async Task<ContextRender> Run(string outDir, double scale)
	{
		var result = new ContextRender();
		Language? before = Localizer.Chosen;
		CultureInfo uiBefore = CultureInfo.CurrentUICulture;
		try
		{
			// The view models are built with the language on Automatic: on an English Windows, so whatever they word before
			// the switch is English too.
			CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
			var plain = new List<(ContextShot Shot, string Image, IReadOnlyList<(string, Rect, Size)> Layout, IReadOnlyList<PlainText> Texts)>();
			await ContextScene.Run(Language.English, async (shot, window) =>
			{
				await Settle(window);
				string image = shot.Id + ".webp";
				ContextImage.SaveWebp(window, Path.Combine(outDir, image), scale);
				plain.Add((shot, image, KeyTagScanner.LayoutSignature(window), KeyTagScanner.PlainTexts(window)));
				result.Problems.AddRange(ForbiddenText(window).Select(t => $"{shot.Id}: public picture shows \"{t}\""));
			});

			int index = 0;
			await ContextScene.Run(KeyTagLocale.Language, async (shot, window) =>
			{
				await Settle(window);
				if (index >= plain.Count || plain[index].Shot.Id != shot.Id)
				{
					result.Problems.Add($"{shot.Id}: the key tag render reached a different shot than the plain one");
					return;
				}
				(ContextShot plainShot, string image, IReadOnlyList<(string, Rect, Size)> plainLayout, IReadOnlyList<PlainText> texts) = plain[index++];
				ContextScan scan = KeyTagScanner.Scan(window, texts);
				IReadOnlyList<(string, Rect, Size)> taggedLayout = KeyTagScanner.LayoutSignature(window);
				result.Problems.AddRange(LayoutDifferences(plainLayout, taggedLayout).Select(p => $"{shot.Id}: {p}"));
				result.MaxWidthDifferenceSeen = Math.Max(result.MaxWidthDifferenceSeen, MaxWidthDifference(plainLayout, taggedLayout));
				result.Shots.Add(new ContextShotResult(plainShot, image, scan.Items, scan.Untagged));
			});
			if (index != plain.Count)
			{
				result.Problems.Add($"the key tag render saw {index} shots, the plain one {plain.Count}");
			}
		}
		finally
		{
			Localizer.Chosen = before;
			CultureInfo.CurrentUICulture = uiBefore;
		}
		return result;
	}

	private static Task Settle(Window window)
	{
		FitCheck.Settle(window);
		Thread.Sleep(200);
		FitCheck.Settle(window);
		return Task.CompletedTask;
	}

	// Where the two renders' text boxes differ (by more than the tolerance), in tree order: each TextBlock's box, and its
	// laid-out text's height (the same lines). The laid-out width may differ a little more (WidthTolerance): a marker between
	// two letters stops the kerning of that pair; the boxes are measured on the plain layout anyway.
	public static IEnumerable<string> LayoutDifferences(IReadOnlyList<(string Text, Rect Box, Size Layout)> plain,
		IReadOnlyList<(string Text, Rect Box, Size Layout)> tagged)
	{
		if (plain.Count != tagged.Count)
		{
			yield return $"{plain.Count} texts in the plain render, {tagged.Count} in the key tag render";
			yield break;
		}
		double tol = TranslateContextTests.LayoutTolerance;
		for (int i = 0; i < plain.Count; i++)
		{
			(string text, Rect a, Size la) = plain[i];
			(string taggedText, Rect b, Size lb) = tagged[i];
			if (Math.Abs(a.X - b.X) > tol || Math.Abs(a.Y - b.Y) > tol || Math.Abs(a.Width - b.Width) > tol || Math.Abs(a.Height - b.Height) > tol
				|| Math.Abs(la.Width - lb.Width) > WidthTolerance || Math.Abs(la.Height - lb.Height) > tol)
			{
				yield return $"\"{text}\" {a} {la} vs \"{taggedText}\" {b} {lb}";
			}
		}
	}

	public const double WidthTolerance = 3;

	// The largest difference in laid-out text width between the two renders (for the report).
	public static double MaxWidthDifference(IReadOnlyList<(string Text, Rect Box, Size Layout)> plain, IReadOnlyList<(string Text, Rect Box, Size Layout)> tagged) =>
		plain.Count == tagged.Count && plain.Count > 0 ? plain.Zip(tagged, (a, b) => Math.Abs(a.Layout.Width - b.Layout.Width)).Max() : 0;

	// Any text on screen that names a real game, device, user folder (the other tests' sample data).
	private static IEnumerable<string> ForbiddenText(Window window)
	{
		IEnumerable<string> texts = window.GetVisualDescendants()
			.Select(v => v switch { TextBlock t => t.Text, TextPresenter p => p.Text, _ => null })
			.OfType<string>();
		return texts.Where(t => ContextScene.ForbiddenText.Any(f => t.Contains(f, StringComparison.OrdinalIgnoreCase))).Distinct();
	}
}

// A window's picture at `scale` (2: crisp text), as WebP (SkiaSharp, already in the test project through Avalonia.Skia).
internal static class ContextImage
{
	public static void SaveWebp(Window window, string path, double scale)
	{
		var size = new PixelSize((int)Math.Round(window.Bounds.Width * scale), (int)Math.Round(window.Bounds.Height * scale));
		using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
		bitmap.Render(window);
		using var png = new MemoryStream();
		bitmap.Save(png);
		png.Position = 0;
		using SKBitmap decoded = SKBitmap.Decode(png);
		using SKImage image = SKImage.FromBitmap(decoded);
		using SKData data = image.Encode(SKEncodedImageFormat.Webp, Quality);
		using FileStream file = File.Create(path);
		data.SaveTo(file);
	}

	// Lossy WebP at 90: the 2x text stays clean at about 40% of the lossless size (100 would be lossless in Skia).
	public static int Quality { get; set; } = 90;
}
