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

				// Nothing scrolls and nothing is cut off at the bottom: the footer (the health line) is the page's last
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

	// The controller type states: what the game sees (DualSense), DualShock 4 with a change held back for the running game
	// ("Şimdi Uygula" in the hero and the sidebar), Low Power (its note under the card's title, the Speaker tile off with
	// the reason), and headphones in the controller (the sound output picker shows beside the microphone's).
	// Every size and both languages fit like the default state; the hero's and the sidebar's pad lines are whole
	// and inside their cards. With KOPRU_FIT_RENDER_DIR set: overview-{state}-{w}x{h}-{lang}.png at 1100x700 and 1920x1080.
	[AvaloniaFact]
	public void Overview_Fits_InEveryControllerTypeState()
	{
		string? dir = Environment.GetEnvironmentVariable("KOPRU_FIT_RENDER_DIR") is { Length: > 0 } d ? d : null;
		foreach (string state in new[] { "dualsense", "ds4-pending", "lowpower", "headphones" })
		{
			ForEachLanguage((window, code, fake) =>
			{
				var vm = (MainWindowViewModel)window.DataContext!;
				Apply(vm, fake, state);
				foreach ((double w, double h) in Sizes)
				{
					string at = $"{state} {code} {w}x{h}";
					Layout layout = Show(window, w, h);
					CheckRhythm(layout, at);
					double viewHeight = layout.View.Bounds.Height;
					Assert.True(layout.FooterBottom <= viewHeight + 0.5, $"{at}: the page ends at {layout.FooterBottom:0.#}, below the visible {viewHeight:0.#}");
					foreach (TextBlock label in layout.Card.GetVisualDescendants().OfType<TextBlock>()
						.Where(t => t.IsEffectivelyVisible && (t.Classes.Contains("label") || t.Classes.Contains("tileLabel") || t.Classes.Contains("tileState"))))
					{
						Assert.False(label.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{at}: \"{label.Text}\" is trimmed");
					}
					CheckPadLines(window, layout, state, at);
					if (dir != null && (w, h) is (1100, 700) or (1920, 1080))
					{
						Directory.CreateDirectory(dir);
						using var frame = window.CaptureRenderedFrame();
						frame!.Save(Path.Combine(dir, $"overview-{state}-{w}x{h}-{code}.png"));
					}
				}
			});
		}
	}

	private static void Apply(MainWindowViewModel vm, FakeControllerService fake, string state)
	{
		switch (state)
		{
			case "ds4-pending":
				// DualShock 4 as the default type (swapped with no game running), then the game back and its own Xbox 360
				// choice held back while it runs.
				fake.Watcher.Exit("Cyberpunk2077.exe");
				fake.Games.Tick();
				fake.Games.SetForcedPadType(Kopru.Core.Virtual.VirtualPadType.DualShock4);
				// A listed game: Automatic would give it DualSense; DualShock 4 as its choice when it starts.
				fake.Games.SetPadType("Cyberpunk 2077", Kopru.Core.Virtual.VirtualPadType.DualShock4);
				fake.Watcher.Exe = "Cyberpunk2077.exe";
				fake.Games.Tick();
				fake.Games.SetPadType(fake.Games.ActiveGame!.Key, Kopru.Core.Virtual.VirtualPadType.Xbox360);
				Assert.True(vm.Settings.HasPendingPadType);
				Assert.Equal(Kopru.Core.Virtual.VirtualPadType.DualShock4, vm.Settings.CurrentPadType);
				break;
			case "lowpower":
				vm.Settings.LowPowerOn = true;
				break;
			case "headphones":
				fake.HeadphonesPlugged = true;
				break;
		}
		fake.RaiseStateChanged();
		Settle();
	}

	private static void CheckPadLines(MainWindow window, Layout layout, string state, string at)
	{
		// The hero: "Oyun Şunu Görüyor: ..." whole, inside its card; with a held-back type the apply button too.
		TextBlock hero = layout.View.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "HeroPadStatus");
		Assert.True(hero.IsEffectivelyVisible, $"{at}: no pad status in the hero");
		Assert.False(hero.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{at}: \"{hero.Text}\" is trimmed");
		Border heroCard = hero.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("panel"));
		Button apply = layout.View.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "HeroApplyPadType");
		Assert.Equal(state == "ds4-pending", apply.IsEffectivelyVisible);
		// Every line of the hero is whole (the game name, the pad lines, the held-back type).
		foreach (TextBlock line in heroCard.GetVisualDescendants().OfType<TextBlock>().Where(b => b.IsEffectivelyVisible && !string.IsNullOrEmpty(b.Text)))
		{
			Assert.False(line.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{at}: \"{line.Text}\" is cut in the hero");
		}
		foreach (Control c in new Control[] { hero, apply }.Where(c => c.IsEffectivelyVisible))
		{
			Rect r = Rect(c, heroCard);
			Assert.True(r.Top >= -0.5 && r.Bottom <= heroCard.Bounds.Height + 0.5 && r.Right <= heroCard.Bounds.Width + 0.5, $"{at}: {c.Name} leaves the hero ({r})");
		}
		// The controller drawing is large (a fixed 160 px column, about 1.35 times its old share) and the game's name above
		// the pad line stays readable: at most two lines, never cut in the hero.
		Viewbox art = layout.View.GetVisualDescendants().OfType<Viewbox>().Single(v => v.Name == "HeroArt");
		Assert.True(art.Bounds.Width >= 155, $"{at}: the drawing is {art.Bounds.Width:0.#} wide");
		Assert.True(Rect(art, heroCard).Bottom <= heroCard.Bounds.Height + 0.5, $"{at}: the drawing leaves the hero");
		// The sidebar card: the same line, whole.
		TextBlock side = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PadStatus");
		Assert.True(side.IsEffectivelyVisible);
		Assert.False(side.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{at}: sidebar \"{side.Text}\" is trimmed");
		Border sideCard = side.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("panel"));
		// (In window pixels: the content is scaled by UiScale.)
		double sideBottom = sideCard.TranslatePoint(new Point(0, sideCard.Bounds.Height), window)!.Value.Y;
		Assert.True(sideBottom <= window.Bounds.Height + 0.5, $"{at}: the sidebar card ends at {sideBottom:0.#}, below the window ({window.Bounds.Height:0.#})");
		// Its last line stays inside the card.
		Assert.True(Rect(side, sideCard).Bottom <= sideCard.Bounds.Height + 0.5, $"{at}: the sidebar pad line leaves its card");
		// The quick "Kumanda Türü" picker shows its choice whole ("Otomatik (Oyuna Göre)" is the longest).
		ComboBox picker = layout.Card.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "QuickPadTypePicker");
		foreach (TextBlock t in picker.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
		{
			Assert.False(t.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{at}: the controller type picker cuts \"{t.Text}\" ({t.Bounds.Width:0.#} for {t.DesiredSize.Width:0.#}, picker {picker.Bounds.Width:0.#}, font {t.FontSize})");
		}
		// Sound pickers: the output picker only with headphones in the controller (under the Speaker tile, the microphone under
		// the Microphone tile); without them the microphone picker takes the whole row, edge to edge with the tiles.
		ComboBox output = layout.Card.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "QuickOutputPicker");
		ComboBox input = layout.Card.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "QuickInputPicker");
		Assert.Equal(state == "headphones", output.IsEffectivelyVisible);
		Rect tilesBox = Rect(layout.Card.GetVisualDescendants().OfType<Grid>().Single(g => g.Classes.Contains("tiles")), layout.Card);
		Rect inputBox = Rect(input, layout.Card);
		Assert.InRange(inputBox.Right, tilesBox.Right - 0.5, tilesBox.Right + 0.5);
		if (output.IsEffectivelyVisible)
		{
			Rect outputBox = Rect(output, layout.Card);
			Assert.InRange(outputBox.Left, tilesBox.Left - 0.5, tilesBox.Left + 0.5);
			Assert.InRange(outputBox.Top, inputBox.Top - 0.5, inputBox.Top + 0.5);
			Assert.True(outputBox.Right < inputBox.Left, $"{at}: the sound pickers overlap");
		}
		else
		{
			Assert.InRange(inputBox.Left, tilesBox.Left - 0.5, tilesBox.Left + 0.5);
		}
		// Low Power: its note under the card's title, whole.
		TextBlock note = layout.Card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "LowPowerOffNote");
		Assert.Equal(state == "lowpower", note.IsEffectivelyVisible);
		if (note.IsEffectivelyVisible)
		{
			Assert.False(note.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{at}: \"{note.Text}\" is trimmed");
		}
	}

	// The live card in its three looks (a good link at rest, triggers, haptics and the microphone moving, a weak link with
	// spikes and the advice line) and both views: every size and both languages fit like the default state, the card ends on
	// the quick settings' line and keeps its height whichever view is shown, and its texts, gauge, chart and signal lanes are
	// whole and inside the card.
	[AvaloniaFact]
	public void LiveCard_Fits_InEveryState()
	{
		foreach (string state in LiveCardRenderTests.States)
		{
			ForEachLanguage((window, code, fake) =>
			{
				var vm = (MainWindowViewModel)window.DataContext!;
				LiveCardRenderTests.Apply(vm, fake, state);
				Settle();
				foreach ((double w, double h) in Sizes)
				{
					double? cardHeight = null;
					foreach (int view in new[] { LivePanelViewModel.LinkView, LivePanelViewModel.SignalsView })
					{
						vm.Overview.Live.SelectedView = view;
						if (view == LivePanelViewModel.SignalsView)
						{
							LiveCardRenderTests.Animate(vm, fake);
						}
						string at = $"live {state} view {view} {code} {w}x{h}";
						Layout layout = Show(window, w, h);
						CheckRhythm(layout, at);
						Assert.True(layout.FooterBottom <= layout.View.Bounds.Height + 0.5, $"{at}: the page ends below the window");
						Border live = layout.View.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("panel") && b.Classes.Contains("fill") && !b.Classes.Contains("quickCard"));
						cardHeight ??= live.Bounds.Height;
						Assert.True(Math.Abs(live.Bounds.Height - cardHeight.Value) < 0.5, $"{at}: the card is {live.Bounds.Height:0.#} high, {cardHeight:0.#} in the Link view");
						Thickness pad = live.Padding;
						foreach (TextBlock text in live.GetVisualDescendants().OfType<TextBlock>().Where(b => b.IsEffectivelyVisible && !string.IsNullOrEmpty(b.Text)))
						{
							Assert.False(text.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{at}: \"{text.Text}\" is cut");
							Rect r = Rect(text, live);
							Assert.True(r.Bottom <= live.Bounds.Height - pad.Bottom + 0.5 && r.Right <= live.Bounds.Width - pad.Right + 0.5, $"{at}: \"{text.Text}\" leaves the live card");
						}
						foreach (Control drawing in live.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is Kopru.App.Controls.Sparkline
							or Kopru.App.Controls.SignalTrace or Kopru.App.Controls.QualityGauge))
						{
							Rect r = Rect(drawing, live);
							Assert.True(r.Height >= 30 && r.Bottom <= live.Bounds.Height - pad.Bottom + 0.5 && r.Right <= live.Bounds.Width - pad.Right + 0.5,
								$"{at}: {drawing.GetType().Name} {drawing.Name} at {r}");
						}
						// The footer line ("Last 60 s" / "Last 10 s") ends on the card's content bottom in both views.
						TextBlock note = live.GetVisualDescendants().OfType<TextBlock>().Single(b => b.IsEffectivelyVisible && b.Name is "ChartNote" or "SignalsNote");
						Assert.InRange(Rect(note, live).Bottom, live.Bounds.Height - pad.Bottom - 1, live.Bounds.Height - pad.Bottom + 0.5);
						// A weak link: the notice on top of the card in both views, whole, above the card's title.
						TextBlock advice = live.GetVisualDescendants().OfType<TextBlock>().Single(b => b.Name == "LinkAdvice");
						Assert.Equal(state == "weak", advice.IsEffectivelyVisible);
						if (advice.IsEffectivelyVisible)
						{
							Assert.False(advice.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{at}: the link notice is cut");
							Rect r = Rect(advice, live);
							Assert.True(r.Top >= pad.Top - 0.5 && r.Right <= live.Bounds.Width - pad.Right + 0.5, $"{at}: the link notice leaves the card ({r})");
							TextBlock title = live.GetVisualDescendants().OfType<TextBlock>().First(b => b.IsEffectivelyVisible && b.Classes.Contains("h2"));
							Assert.True(r.Bottom < Rect(title, live).Top, $"{at}: the link notice runs into the title");
						}
						Assert.Equal(state == "good-active" && view == LivePanelViewModel.SignalsView,
							live.GetVisualDescendants().OfType<Kopru.App.Controls.SignalTrace>().Single(t => t.Name == "MicTrace").IsEffectivelyVisible);
					}
					vm.Overview.Live.SelectedView = LivePanelViewModel.LinkView;
				}
			});
		}
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
		double cardBottom = Rect(card, view).Bottom;
		double liveBottom = Rect(live, view).Bottom;
		double cardsBottom = Math.Max(cardBottom, liveBottom);
		return new Layout(view, card, tiles, controls, gaps, cardBottom, liveBottom,
			footer.Length > 0 ? footer.Min(c => Rect(c, view).Top) : cardsBottom,
			footer.Length > 0 ? footer.Max(c => Rect(c, view).Bottom) : cardsBottom);
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
	private static void ForEachLanguage(Action<MainWindow, string> check) => ForEachLanguage((window, code, _) => check(window, code));

	private static void ForEachLanguage(Action<MainWindow, string, FakeControllerService> check)
	{
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in FitCheck.ShippedLanguages())
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
					check(window, code, fake);
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
