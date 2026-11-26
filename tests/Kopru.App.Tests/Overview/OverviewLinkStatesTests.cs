// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The Overview in every link state (Bluetooth, the cable only charging, the cable as the link, no controller, waiting,
// virtual controller off, Bluetooth lost on the cable, a game running with Close Game and its question), in both
// languages, at 1100x700, 1152x700 and the smallest window (960x640): nothing scrolls or leaves the window, the two
// columns end on one line, the hero's texts and connection line, the live card's texts and chain, the header and the
// sidebar card's link line are whole and inside their cards, and Close Game sits just left of Turn Off Controller only while
// a game runs. Plus what the connection line, the sidebar's link and the live card's state panel say in each state.
public sealed class OverviewLinkStatesTests
{
	private static readonly string[] States = ["bt", "charge", "wired", "not-connected", "waiting", "virtual-off", "cable-only", "game", "game-confirm"];
	private static readonly (double W, double H)[] Sizes = [(1100, 700), (1152, 700), (960, 640)];

	[AvaloniaFact]
	public async Task Overview_Fits_InEveryLinkState()
	{
		Language? before = Localizer.Chosen;
		GamePanelViewModel.CloseWait = TimeSpan.FromMilliseconds(1);
		try
		{
			foreach (string language in new[] { "tr", "en" })
			{
				Localizer.Chosen = language == "tr" ? Language.Turkish : Language.English;
				foreach (string state in States)
				{
					string open = state == "game-confirm" ? "game" : state;
					(MainWindowViewModel vm, MainWindow window, FakeControllerService fake) = UiShotsRenderTests.OpenOverview(open, 1100, 700, language);
					try
					{
						if (state == "game-confirm")
						{
							vm.BackToGame.Delay = _ => Task.CompletedTask;
							await vm.BackToGame.CloseGameCommand.ExecuteAsync(null);
							Assert.True(vm.BackToGame.ConfirmsForceClose);
						}
						foreach ((double w, double h) in Sizes)
						{
							window.Width = w;
							window.Height = h;
							Settle();
							Check(window, state, $"{state} {language} {w}x{h}");
						}
					}
					finally
					{
						window.Close();
						fake.Dispose();
					}
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static void Check(MainWindow window, string state, string at)
	{
		OverviewView view = window.GetVisualDescendants().OfType<OverviewView>().Single();
		Border hero = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("panel") && !b.Classes.Contains("fill"));
		Border live = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "LiveCard");
		Border quick = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("quickCard"));
		Grid page = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Classes.Contains("page"));
		Grid header = page.Children.OfType<Grid>().Single(g => Grid.GetRow(g) == 0);

		// Nothing scrolls, the page ends inside the view, both columns end on one line.
		Assert.DoesNotContain(view.GetVisualAncestors().OfType<ScrollViewer>(), s => s.Extent.Height > s.Viewport.Height + 0.5);
		Control[] footers = page.Children.Where(c => Grid.GetRow(c) == 2 && c.IsVisible).ToArray();
		double footer = footers.Length > 0 ? footers.Max(c => Rect(c, view).Bottom) : Math.Max(Rect(live, view).Bottom, Rect(quick, view).Bottom);
		Assert.True(footer <= view.Bounds.Height + 0.5, $"{at}: the page ends at {footer:0.#}, below {view.Bounds.Height:0.#}");
		Assert.InRange(Rect(live, view).Bottom, Rect(quick, view).Bottom - 1, Rect(quick, view).Bottom + 1);

		// Hero and live card: every text whole and inside its card; the drawings inside too.
		foreach (Border card in new[] { hero, live })
		{
			Thickness pad = card.Padding;
			foreach (TextBlock text in card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.TextLayout.TextLines.Count > 0
				&& (!string.IsNullOrEmpty(t.Text) || t.Inlines?.Count > 0)))
			{
				Assert.False(text.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{at}: \"{text.Text}\" is cut ({text.Bounds.Width:0.#} for {text.DesiredSize.Width:0.#})");
				Rect r = Rect(text, card);
				Assert.True(r.Top >= pad.Top - 0.5 && r.Bottom <= card.Bounds.Height - pad.Bottom + 0.5 && r.Right <= card.Bounds.Width - pad.Right + 0.5,
					$"{at}: \"{text.Text}\" leaves its card ({r} in {card.Bounds.Size})");
			}
			foreach (Control drawing in card.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is ChainDiagram or Sparkline or QualityGauge or Viewbox))
			{
				Rect r = Rect(drawing, card);
				Assert.True(r.Top >= pad.Top - 0.5 && r.Bottom <= card.Bounds.Height - pad.Bottom + 0.5 && r.Right <= card.Bounds.Width - pad.Right + 0.5,
					$"{at}: {drawing.GetType().Name} {drawing.Name} leaves its card ({r})");
				if (drawing is ChainDiagram)
				{
					Assert.True(drawing.Bounds.Height + 0.5 >= drawing.DesiredSize.Height, $"{at}: the chain is squeezed");
				}
			}
		}

		// The hero: the drawing large beside the controller's texts; the connection line with a controller only.
		bool connected = state is "bt" or "charge" or "wired" or "virtual-off" or "game" or "game-confirm";
		LinkLine heroLink = hero.GetVisualDescendants().OfType<LinkLine>().Single();
		Assert.Equal(connected, heroLink.IsEffectivelyVisible);
		Viewbox art = hero.GetVisualDescendants().OfType<Viewbox>().Single(v => v.Name == "HeroArt");
		// (Beside the state texts without a controller it gives way to their 180 px, as drawn.)
		Assert.True(art.Bounds.Width >= (connected ? 150 : 110), $"{at}: the drawing is {art.Bounds.Width:0.#} wide");

		// The live card: the numbers while live, the chain with a title and a paragraph otherwise.
		bool isLive = state is "bt" or "charge" or "wired" or "game" or "game-confirm";
		Assert.Equal(isLive, live.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "LiveLink").IsEffectivelyVisible);
		ChainDiagram chain = live.GetVisualDescendants().OfType<ChainDiagram>().Single();
		Assert.Equal(!isLive, chain.IsEffectivelyVisible);
		Assert.Equal(state == "wired", live.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WiredTile").IsEffectivelyVisible);
		Assert.Equal(isLive && state != "wired", live.GetVisualDescendants().OfType<QualityGauge>().Single().IsEffectivelyVisible);
		// The features strip is gone: no row of feature icons under the chart, the footer line ends on the card's bottom.
		if (isLive)
		{
			TextBlock note = live.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChartNote");
			Assert.InRange(Rect(note, live).Bottom, live.Bounds.Height - live.Padding.Bottom - 1, live.Bounds.Height - live.Padding.Bottom + 0.5);
		}

		// The header: the title on one line, every button inside the page and right of the title.
		TextBlock title = header.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("h1"));
		Assert.Single(title.TextLayout.TextLines);
		foreach (Button button in header.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
		{
			Rect r = Rect(button, view);
			Assert.True(r.Right <= Rect(quick, view).Right + 0.5, $"{at}: {button.Name} leaves the page ({r})");
			Assert.True(r.Left >= Rect(title, view).Left + title.TextLayout.WidthIncludingTrailingWhitespace, $"{at}: {button.Name} runs into the title");
			foreach (TextBlock t in button.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
			{
				Assert.False(t.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{at}: \"{t.Text}\" is cut in the header");
			}
		}

		// Close Game: only with a game, just left of Turn Off Controller, as tall and in its style; the question in its place.
		Button close = header.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CloseGameButton");
		Button powerOff = header.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PowerOffButton");
		Border ask = header.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ForceCloseAsk");
		Assert.Equal(state == "game", close.IsEffectivelyVisible);
		Assert.Equal(state == "game-confirm", ask.IsEffectivelyVisible);
		if (close.IsEffectivelyVisible)
		{
			Assert.True(powerOff.IsEffectivelyVisible);
			Assert.Same(powerOff.Theme, close.Theme);
			Assert.Equal(powerOff.Bounds.Height, close.Bounds.Height, 1);
			Assert.Equal(Rect(close, view).Top, Rect(powerOff, view).Top, 1);
			double gap = Rect(powerOff, view).Left - Rect(close, view).Right;
			Assert.InRange(gap, 8, 24);
		}

		// The sidebar card: its link line whole, the card inside the window.
		LinkLine side = window.GetVisualDescendants().OfType<LinkLine>().Single(l => l.Name == "SidebarLink");
		Assert.True(side.IsEffectivelyVisible);
		Assert.False(side.Label!.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{at}: the sidebar's \"{side.Text}\" is cut");
		Border sideCard = side.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("panel"));
		double sideBottom = sideCard.TranslatePoint(new Point(0, sideCard.Bounds.Height), window)!.Value.Y;
		Assert.True(sideBottom <= window.Bounds.Height + 0.5, $"{at}: the sidebar card ends at {sideBottom:0.#}");
		Assert.True(Rect(side, sideCard).Right <= sideCard.Bounds.Width - sideCard.Padding.Right + 0.5, $"{at}: the sidebar link leaves its card");
	}

	// What the hero's connection line, the sidebar's link and the live card's state panel say, and which link is lit.
	[AvaloniaFact]
	public void TheLinkLines_FollowTheLinkState()
	{
		Language? before = Localizer.Chosen;
		Localizer.Chosen = Language.Turkish;
		try
		{
			(string State, LinkUse Bluetooth, LinkUse Usb, string? Hero, string Sidebar, string? Heading)[] cases =
			[
				("bt", LinkUse.InUse, LinkUse.Absent, "Bluetooth İle Bağlı", "Bluetooth", null),
				("charge", LinkUse.InUse, LinkUse.ChargeOnly, "Bluetooth · Kablo Takılı", "Bluetooth · Kablo Takılı", null),
				("wired", LinkUse.Standby, LinkUse.InUse, "Kablo Üzerinden Çalışıyor", "USB Kablosu", null),
				("virtual-off", LinkUse.Standby, LinkUse.Absent, "Sanal Kumanda Kapalı", "Bluetooth", "Sanal Kumanda Kapalı"),
				("not-connected", LinkUse.Absent, LinkUse.Absent, null, "Bağlı Değil", "Kumandayı Aç"),
				("waiting", LinkUse.Absent, LinkUse.Absent, null, "Bağlı Değil", "Kumanda Bekleniyor"),
				("cable-only", LinkUse.Absent, LinkUse.Direct, null, "Kabloyla Bağlı", "Sanal Kumanda Duraklatıldı"),
			];
			foreach ((string state, LinkUse bluetooth, LinkUse usb, string? heroText, string sidebar, string? heading) in cases)
			{
				(MainWindowViewModel vm, MainWindow window, FakeControllerService fake) = UiShotsRenderTests.OpenOverview(state, 1100, 700);
				try
				{
					OverviewViewModel o = vm.Overview;
					Assert.Equal(bluetooth, o.BluetoothUse);
					Assert.Equal(usb, o.UsbUse);
					Assert.Equal(heroText != null, o.ShowsHeroLink);
					if (heroText != null)
					{
						Assert.Equal(heroText, o.HeroLinkText);
					}
					// The cable mode is no longer experimental: no tag on any link line.
					Assert.Empty(window.GetVisualDescendants().OfType<ExperimentalTag>());
					Assert.Equal(sidebar, vm.DeviceConnection);
					Assert.Equal(sidebar, window.GetVisualDescendants().OfType<LinkLine>().Single(l => l.Name == "SidebarLink").Label!.Text);
					Assert.Equal(heading == null, o.IsLive);
					if (heading != null)
					{
						Assert.Equal(heading, o.StateHeading);
						Assert.NotNull(o.StateChain);
						Assert.False(string.IsNullOrEmpty(o.StateText));
						Assert.Equal(state == "cable-only", o.StateChain!.CableCaption != null);
					}
					// The live caption names the measured link; on the cable "Kablolu" and a 2 ms scale.
					if (o.IsLive)
					{
						Assert.Equal(state == "wired" ? "Tipik Gecikme · USB" : "Tipik Gecikme · Bluetooth", o.Live.DelayCaption);
						Assert.Equal(state == "wired" ? "2 ms" : "10 ms", o.Live.ChartTopText);
					}
				}
				finally
				{
					window.Close();
					fake.Dispose();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static Rect Rect(Visual c, Visual to)
	{
		Point p = c.TranslatePoint(new Point(0, 0), to)!.Value;
		return new Rect(p, c.Bounds.Size);
	}

	private static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Dispatcher.UIThread.RunJobs();
	}
}
