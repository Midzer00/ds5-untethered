// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Kopru.App.Tests;

// Tooltips app-wide: in the dark and the light theme the text stands out from the surface (WCAG AA, at least 4.5:1), the
// surface is opaque, corners are rounded, long text wraps inside a readable width, and the size is the app's body size.
// With KOPRU_TOOLTIP_RENDER_DIR set a few are saved as tooltip-{dark|light}.png.
public sealed class TooltipContrastTests
{
	private static readonly string[] Samples =
	[
		"Oyunu başlatır.",
		"Oyunun kumanda hoparlörüne gönderdiği sesleri çalar; sadece DualSense türünde çalışır.",
		"Kalın çizgi: her saniyenin en kötü gecikmesi; ince çizgi: tipik gecikme. Kesikli çizginin üstü hissedilir.",
	];

	[AvaloniaTheory]
	[InlineData("dark")]
	[InlineData("light")]
	public void Tooltips_AreReadable_InBothThemes(string theme)
	{
		var stack = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(20), HorizontalAlignment = HorizontalAlignment.Left };
		foreach (string text in Samples)
		{
			// Shown as an open tooltip is (the theme fades a closed one out).
			stack.Children.Add(new ToolTip { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 1 });
		}
		var window = new Window
		{
			Width = 420,
			Height = 300,
			RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light,
			Background = theme == "dark" ? new SolidColorBrush(Color.Parse("#16181C")) : new SolidColorBrush(Color.Parse("#F3F4F6")),
			Content = stack,
		};
		window.Show();
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Dispatcher.UIThread.RunJobs();

		foreach (ToolTip tip in stack.Children.OfType<ToolTip>())
		{
			Color back = Assert.IsAssignableFrom<ISolidColorBrush>(tip.Background).Color;
			// The colour the text is really drawn in (the app's TextBlock styles could override the tooltip's).
			TextBlock shown = tip.GetVisualDescendants().OfType<TextBlock>().Single(b => b.Text == (string)tip.Content!);
			Color fore = Assert.IsAssignableFrom<ISolidColorBrush>(shown.Foreground).Color;
			Assert.Equal(255, back.A);
			double ratio = Contrast(fore, back);
			Assert.True(ratio >= 4.5, $"{theme}: contrast {ratio:0.00} for {fore} on {back}");
			// Dark theme: a dark surface with light text; light theme: the other way round.
			Assert.Equal(theme == "dark", Luminance(back) < Luminance(fore));
			Assert.True(tip.CornerRadius.TopLeft >= 6, $"{theme}: corner {tip.CornerRadius}");
			Assert.Equal(13, tip.FontSize);
			Assert.True(tip.Bounds.Width <= 320.5, $"{theme}: {tip.Bounds.Width:0} wide");
		}
		// The long ones wrap instead of growing past the width.
		Assert.True(stack.Children.OfType<ToolTip>().Last().Bounds.Height > stack.Children.OfType<ToolTip>().First().Bounds.Height + 10);

		using var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		if (Environment.GetEnvironmentVariable("KOPRU_TOOLTIP_RENDER_DIR") is { Length: > 0 } dir)
		{
			Directory.CreateDirectory(dir);
			frame!.Save(Path.Combine(dir, $"tooltip-{theme}.png"));
		}
		window.Close();
	}

	// WCAG 2 contrast ratio and relative luminance.
	private static double Contrast(Color a, Color b)
	{
		double la = Luminance(a), lb = Luminance(b);
		return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
	}

	private static double Luminance(Color c)
	{
		static double Channel(byte v)
		{
			double s = v / 255.0;
			return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
		}
		return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
	}
}
