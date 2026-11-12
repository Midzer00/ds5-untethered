// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The Overview fits the window at every common size and shape, in both languages, through the real scale path (the window's
// own UiScale factor): nothing scrolls or is cut off, every Quick Settings row is inside its card, consecutive controls keep
// a clear gap (more with spare height, never less than the minimum), no label or tile name is trimmed, nothing overlaps, and
// the two columns end on one line. The minimum window (960x640) is scaled down (UiScale.MinFactor), so it lays out on at
// least the design size like every other window.
public sealed class OverviewFitTests
{
	// Window sizes (screen pixels); the window turns them into a layout size by its UiScale factor.
	public static readonly (double Width, double Height)[] Sizes =
	[
		(UiScale.MinWindowWidth, UiScale.MinWindowHeight), (1100, 700), (1280, 720), (1366, 768), (1536, 864), (1600, 900), (1920, 1080), (2560, 1440), (2560, 1080), (1100, 1000), (1400, 1050),
	];

	// Clear space between consecutive control rows of the card, between the tile rows, and between the groups (tiles, sound, controller).
	private const double MinRowGap = 10;
	private const double MinTileGap = 8;
	private const double MinGroupGap = 16;
	// The gaps grow with spare height up to a cap (a little over the rows' 18 / the groups' 28 for rounding).
	private const double MaxRowGap = 20;
	private const double MaxGroupGap = 30;

	[AvaloniaFact]
	public void Overview_Fits_AtEveryWindowSize_InBothLanguages()
	{
		ForEachLanguage((window, code) =>
		{
			foreach ((double w, double h) in Sizes)
			{
				string at = $"{code} {w}x{h}";
				Layout layout = Show(window, w, h);
				Assert.Equal(UiScale.Factor(w, h), window.ContentScale, 3);
				CheckRhythm(layout, at);

				// Nothing scrolls and nothing is cut off at the bottom: the footer (the health line or chips) is the page's last
				// row and ends inside the page area.
				Assert.DoesNotContain(layout.View.GetVisualAncestors().OfType<ScrollViewer>(), s => s.Extent.Height > s.Viewport.Height + 0.5);
				double viewHeight = layout.View.Bounds.Height;
				Assert.True(layout.FooterBottom <= viewHeight + 0.5, $"{at}: the page ends at {layout.FooterBottom:0.#}, below the visible {viewHeight:0.#}");
				Assert.True(layout.CardBottom <= viewHeight + 0.5, $"{at}: the quick settings end below the page");

				// Labels and tile names are never trimmed (device names may be: they have the full name as tooltip).
				foreach (TextBlock label in layout.Card.GetVisualDescendants().OfType<TextBlock>()
					.Where(t => t.IsEffectivelyVisible && (t.Classes.Contains("label") || t.Classes.Contains("tileLabel"))))
				{
					Assert.False(label.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{at}: \"{label.Text}\" is trimmed");
				}
				if ((w, h) is (960, 640) or (1100, 700) or (1920, 1080) or (1100, 1000))
				{
					Save(window, $"overview-fit-{w}x{h}-{code}.png");
				}
			}
		});
	}

	// Spare height goes into the gaps: a tall window spreads the rows more than the design size, up to the cap.
	[AvaloniaFact]
	public void SpareHeight_WidensTheGaps_UpToTheCap()
	{
		ForEachLanguage((window, code) =>
		{
			double design = Show(window, 1100, 700).RowGaps.Min();
			double tall = Show(window, 1100, 1000).RowGaps.Min();
			Assert.True(tall > design + 2, $"{code}: {design:0.#} at 1100x700, {tall:0.#} at 1100x1000");
			Assert.True(tall <= MaxRowGap, $"{code}: {tall:0.#}");
		});
	}

	// With KOPRU_RENDER_SCREENS=1: the frame to docs/screens.
	private static void Save(MainWindow window, string name)
	{
		using var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		if (Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") != "1")
		{
			return;
		}
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "screens")))
		{
			dir = dir.Parent;
		}
		if (dir != null)
		{
			frame!.Save(Path.Combine(dir.FullName, "docs", "screens", name));
		}
	}

	private static void CheckRhythm(Layout layout, string at)
	{
		// Every control of the card is inside the card's content area.
		Thickness pad = layout.Card.Padding;
		foreach (Control c in layout.Controls.Concat(layout.Tiles))
		{
			Rect r = Rect(c, layout.Card);
			Assert.True(r.Top >= pad.Top - 0.5 && r.Bottom <= layout.Card.Bounds.Height - pad.Bottom + 0.5, $"{at}: {c.GetType().Name} {c.Name} is outside the card ({r})");
			Assert.True(r.Left >= pad.Left - 0.5 && r.Right <= layout.Card.Bounds.Width - pad.Right + 0.5, $"{at}: {c.GetType().Name} {c.Name} is outside the card ({r})");
		}
		// Tile rows: a steady small gap.
		foreach (double gap in Gaps(layout.Tiles, layout.Card))
		{
			Assert.InRange(gap, MinTileGap, MinTileGap + 0.5);
		}
		// Control rows: never closer than the minimum, never sparse.
		Assert.Equal(7, layout.RowGaps.Length + 1);
		string all = string.Join(", ", layout.RowGaps.Select(x => x.ToString("0.#")));
		for (int i = 0; i < layout.RowGaps.Length; i++)
		{
			// Gap 1 (mic button row to controller type) is a group gap, checked below.
			if (i != 1)
			{
				Assert.True(layout.RowGaps[i] >= MinRowGap - 0.5 && layout.RowGaps[i] <= MaxRowGap, $"{at}: gaps {all}");
			}
		}
		// The groups: tiles to the device pickers, and the mic button row to the controller type, a larger gap than the rows.
		double tilesToPickers = Rect(layout.Controls[0], layout.Card).Top - layout.Tiles.Max(t => Rect(t, layout.Card).Bottom);
		double soundToController = layout.RowGaps[1];
		foreach (double group in new[] { tilesToPickers, soundToController })
		{
			Assert.True(group >= MinGroupGap - 0.5 && group <= MaxGroupGap, $"{at}: group gap {group:0.#}");
			Assert.True(group > layout.RowGaps[0] + 2, $"{at}: group gap {group:0.#} vs row gap {layout.RowGaps[0]:0.#}");
		}
		// Label and control of a row never overlap.
		foreach (Grid row in layout.Card.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("quickRow") && g.IsEffectivelyVisible))
		{
			TextBlock label = row.Children.OfType<TextBlock>().Single(t => t.Classes.Contains("label"));
			Control control = row.Children.OfType<Control>().Single(c => c is ComboBox or ListBox);
			Assert.True(Rect(label, row).Right <= Rect(control, row).Left, $"{at}: \"{label.Text}\" runs into its control");
		}
		// Both columns end on one line, and the footer starts under the cards.
		Assert.InRange(layout.LiveBottom, layout.CardBottom - 1, layout.CardBottom + 1);
		Assert.True(layout.FooterTop >= layout.CardBottom, $"{at}: the footer overlaps the cards");
	}

	private sealed record Layout(OverviewView View, Border Card, Control[] Tiles, Control[] Controls, double[] RowGaps, double CardBottom, double LiveBottom,
		double FooterTop, double FooterBottom);

	private static Layout Show(MainWindow window, double w, double h)
	{
		window.Width = w;
		window.Height = h;
		Settle();
		OverviewView view = window.GetVisualDescendants().OfType<OverviewView>().Single();
		Border card = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("quickCard"));
		Border live = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("panel") && b.Classes.Contains("fill") && !b.Classes.Contains("quickCard"));
		Grid tileGrid = card.GetVisualDescendants().OfType<Grid>().Single(g => g.Classes.Contains("tiles"));
		Control[] tiles = tileGrid.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.IsEffectivelyVisible).Cast<Control>().ToArray();
		// The card's controls, top to bottom; the two device pickers share a row.
		Control[] controls = card.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && (c is ComboBox || (c is ListBox && c.Parent is Grid)))
			.OrderBy(c => Rect(c, card).Top).ToArray();
		Control[][] rows = controls.GroupBy(c => Math.Round(Rect(c, card).Top)).Select(g => g.ToArray()).ToArray();
		double[] gaps = rows.Zip(rows.Skip(1), (a, b) => b.Min(c => Rect(c, card).Top) - a.Max(c => Rect(c, card).Bottom)).ToArray();
		Grid page = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Classes.Contains("page"));
		Control[] footer = page.Children.Where(c => Grid.GetRow(c) == 2 && c.IsVisible).ToArray();
		return new Layout(view, card, tiles, controls, gaps, Rect(card, view).Bottom, Rect(live, view).Bottom,
			footer.Min(c => Rect(c, view).Top), footer.Max(c => Rect(c, view).Bottom));
	}

	private static double[] Gaps(Control[] items, Visual to)
	{
		double[][] rows = items.Select(c => Rect(c, to)).GroupBy(r => Math.Round(r.Top)).OrderBy(g => g.Key).Select(g => new[] { g.Min(r => r.Top), g.Max(r => r.Bottom) }).ToArray();
		return rows.Zip(rows.Skip(1), (a, b) => b[0] - a[1]).ToArray();
	}

	private static Rect Rect(Visual c, Visual to)
	{
		Point p = c.TranslatePoint(new Point(0, 0), to)!.Value;
		return new Rect(p, c.Bounds.Size);
	}

	// The Overview with the bridge on and a game running (the live parts filled), in Turkish and English.
	private static void ForEachLanguage(Action<MainWindow, string> check)
	{
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				var fake = new FakeControllerService(animate: false);
				var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = code })));
				vm.Overview.ToggleBridgeCommand.Execute(null);
				fake.Tick();
				var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
				window.Show();
				vm.SelectedNav = vm.NavItems[0];
				try
				{
					check(window, code);
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

	private static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Dispatcher.UIThread.RunJobs();
	}
}
