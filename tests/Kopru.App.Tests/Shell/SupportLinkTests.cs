// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// Support (GitHub Sponsors): a pink "Destek Ol" / "Support" pill with a heart in the sidebar, right above the controller
// card, and a plain "GitHub Sponsors" link at the right end of the Updates card's title line in Settings > General. A click
// opens the sponsors page through the app's URL launcher and nothing else; the texts are in both languages; the pill's
// colours are readable; the sidebar (logo, pages, pill, card) still fits the smallest and the owner's window.
// With KOPRU_SUPPORT_RENDER_DIR set: pill-{tr|en}.png at 1152x700 and pill-1100-tr.png at 1100x700 (the Overview, a
// controller connected, a game running).
public sealed class SupportLinkTests
{
	private const string SponsorsUrl = "https://github.com/sponsors/Midzer00";

	[AvaloniaFact]
	public void Sidebar_HasTheSupportPill_InBothLanguages_AndItOpensTheSponsorsPage()
	{
		foreach ((Language language, string code, string label, string tip) in new[]
		{
			(Language.Turkish, "tr", "Destek Ol", "Hep ücretsiz. Destek tamamen isteğe bağlı."),
			(Language.English, "en", "Support", "Free forever. Sponsoring is optional."),
		})
		{
			Run(language, code, (window, vm, links) =>
			{
				Button pill = Pill(window);
				Assert.True(pill.IsEffectivelyVisible, $"{code}: no support pill");
				Assert.Equal(label, pill.GetVisualDescendants().OfType<TextBlock>().Single().Text);
				Assert.Equal(tip, ToolTip.GetTip(pill));
				Assert.Equal(label, Avalonia.Automation.AutomationProperties.GetName(pill));
				Assert.Equal(tip, Avalonia.Automation.AutomationProperties.GetHelpText(pill));
				Assert.True(pill.Focusable && pill.IsTabStop, $"{code}: the pill cannot take keyboard focus");
				// A heart in the shared icon slot, in the label's colour.
				IconSlot heart = pill.GetVisualDescendants().OfType<IconSlot>().Single();
				Assert.Same(IconPaths.Heart, heart.Data);
				Assert.Equal(Color.Parse("#1B1B1F"), ((ISolidColorBrush)heart.Stroke!).Color);
				// A full pill: 36 high, fully round, solid GitHub Sponsors pink.
				Assert.Equal(36, pill.Bounds.Height, 1);
				Assert.Equal(new CornerRadius(18), pill.CornerRadius);
				Assert.Equal(Color.Parse("#DB61A2"), ((ISolidColorBrush)pill.Background!).Color);
				Assert.Equal(SupportLinkViewModel.Url, SponsorsUrl);
				Assert.Empty(links.Opened);

				pill.Command!.Execute(null);
				Assert.Equal([SponsorsUrl], links.Opened);
				// Nothing else happens: the page stays.
				Assert.Same(vm.Overview, vm.SelectedNav.Page);
				// The Overview's footer no longer has a support link of its own.
				OverviewView view = window.GetVisualDescendants().OfType<OverviewView>().Single();
				Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => b.Name == "SupportLink");
			});
		}
	}

	// Near-black text on the pink reads at 4.5:1 or more at rest, hovered and pressed (white would not: 3.4:1); the pill
	// and the focus ring stand out from the sidebar at 3:1 or more.
	[AvaloniaFact]
	public void Pill_Colours_AreReadable()
	{
		Color text = Brush("SupportTextBrush");
		foreach (string key in new[] { "SupportBrush", "SupportHoverBrush", "SupportPressedBrush" })
		{
			Assert.True(Contrast(text, Brush(key)) >= 4.5, $"{key}: text contrast {Contrast(text, Brush(key)):0.00}");
		}
		Assert.True(Contrast(Colors.White, Brush("SupportBrush")) < 4.5, "white text would read better: use it");
		// The sidebar: its white tint over the opaque backdrop (the system acrylic is darker still).
		Color sidebar = Over(Brush("SidebarBrush"), Brush("BackdropBrush"));
		Assert.True(Contrast(Brush("SupportBrush"), sidebar) >= 3, "the pill against the sidebar");
		Assert.True(Contrast(Brush("TextBrush"), sidebar) >= 3, "the focus ring against the sidebar");
	}

	[AvaloniaFact]
	public void SettingsGeneral_HasTheSponsorsLink_OnTheUpdatesTitleLine()
	{
		foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
		{
			Run(language, code, (window, vm, links) =>
			{
				vm.Navigate(vm.SettingsPage);
				vm.SettingsPage.SelectedTab = SettingsViewModel.GeneralTab;
				Settle();
				SettingsView view = window.GetVisualDescendants().OfType<SettingsView>().Single();
				Grid row = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "SupportRow");
				Border updates = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "AboutPanel");
				// The About card's title line: the title, the link at its right end.
				DockPanel card = row.GetVisualAncestors().OfType<DockPanel>().First(p => p.Classes.Contains("card"));
				Assert.Contains(updates, card.Children);
				Assert.Equal(vm.SettingsPage.AboutLabel, row.Children.OfType<TextBlock>().Single(t => t.Classes.Contains("section")).Text);
				Button link = row.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SupportLink");
				Assert.True(link.IsEffectivelyVisible, $"{code}: no sponsors link");
				Assert.Equal("GitHub Sponsors", link.GetVisualDescendants().OfType<TextBlock>().Single().Text);
				Assert.Equal(Text.SupportTip(), ToolTip.GetTip(link));
				Rect r = Rect(link, card);
				Assert.True(r.Right <= card.Bounds.Width + 0.5 && r.Bottom <= Rect(updates, card).Top, $"{code}: the link leaves the title line ({r})");
				link.Command!.Execute(null);
				Assert.Equal([SponsorsUrl], links.Opened);
				// The pill stays in the sidebar on every page.
				Assert.True(Pill(window).IsEffectivelyVisible, $"{code}: the pill is gone on the Settings page");
			});
		}
	}

	// The sidebar with the pill fits: the logo, every page row (40 high, apart), the pill (the card's width, right above
	// it) and the controller card, top to bottom without overlapping, all inside the sidebar; the label whole. With one
	// controller, with a controller type held back for the running game (the tallest single card) and with four
	// controllers; at the minimum window, the design size, the owner's window and full HD.
	[AvaloniaFact]
	public void Sidebar_WithThePill_Fits_InBothLanguages()
	{
		string? dir = Environment.GetEnvironmentVariable("KOPRU_SUPPORT_RENDER_DIR") is { Length: > 0 } d ? d : null;
		foreach (string state in new[] { "one", "ds4-pending", "four" })
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Run(language, code, (window, vm, _) =>
				{
					foreach ((double w, double h) in new[] { (UiScale.MinWindowWidth, UiScale.MinWindowHeight), (1100.0, 700.0), (1152.0, 700.0), (1920.0, 1080.0) })
					{
						string at = $"{state} {code} {w}x{h}";
						window.Width = w;
						window.Height = h;
						Settle();
						CheckSidebar(window, vm, at);
						if (dir != null && state == "one" && ((w, h) == (1152, 700) || (code, w, h) == ("tr", 1100, 700)))
						{
							Directory.CreateDirectory(dir);
							Thread.Sleep(320);
							Settle();
							using var frame = window.CaptureRenderedFrame();
							frame!.Save(Path.Combine(dir, w == 1152 ? $"pill-{code}.png" : $"pill-1100-{code}.png"));
						}
					}
				}, state);
			}
		}
	}

	private static void CheckSidebar(MainWindow window, MainWindowViewModel vm, string at)
	{
		DockPanel sidebar = window.GetVisualDescendants().OfType<DockPanel>().First();
		Button pill = Pill(window);
		Border card = sidebar.Children.OfType<Border>().Single(b => b.Classes.Contains("panel"));
		Image logo = sidebar.Children.OfType<Image>().Single();
		List<ListBoxItem> rows = window.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.DataContext is NavItem).ToList();
		Assert.Equal(vm.NavItems.Count, rows.Count);
		Button star = Star(window);
		Rect p = Rect(pill, sidebar), c = Rect(card, sidebar), l = Rect(logo, sidebar), s = Rect(star, sidebar);
		Assert.True(pill.IsEffectivelyVisible && logo.IsEffectivelyVisible && star.IsEffectivelyVisible, $"{at}: the pill, the star or the logo is hidden");
		Assert.Equal(36, p.Height, 1);
		Assert.Equal(36, s.Height, 1);
		// The pill and the star in one row the card's width, right above it.
		Assert.True(Math.Abs(p.Left - c.Left) < 0.5 && Math.Abs(s.Right - c.Right) < 0.5 && s.Left >= p.Right + 4 - 0.5 && Math.Abs(s.Top - p.Top) < 0.5,
			$"{at}: the pill ({p}) and the star ({s}) are not one row the card's width ({c})");
		Assert.True(p.Bottom <= c.Top - 8 + 0.5, $"{at}: the pill ({p}) runs into the card ({c})");
		Assert.True(c.Bottom <= sidebar.Bounds.Height + 0.5, $"{at}: the card ends at {c.Bottom:0.#}, below the sidebar ({sidebar.Bounds.Height:0.#})");
		// The page rows: whole, apart, between the logo and the pill.
		double above = l.Bottom;
		foreach (ListBoxItem row in rows)
		{
			Rect r = Rect(row, sidebar);
			string title = ((NavItem)row.DataContext!).Title;
			Assert.True(row.IsEffectivelyVisible && r.Height >= 40 - 0.5, $"{at}: {title} is {r.Height:0.#} high");
			Assert.True(r.Top >= above - 0.5, $"{at}: {title} ({r.Top:0.#}) overlaps what is above it ({above:0.#}; card {c})");
			above = r.Bottom;
		}
		Assert.True(above <= p.Top - 4 + 0.5, $"{at}: the last page row ends at {above:0.#}, at the pill ({p.Top:0.#})");
		Assert.DoesNotContain(sidebar.GetVisualDescendants().OfType<ScrollViewer>(), s => s.Extent.Height > s.Viewport.Height + 0.5);
		TextBlock text = pill.GetVisualDescendants().OfType<TextBlock>().Single();
		Assert.False(text.TextLayout.TextLines.Any(t => t.HasCollapsed), $"{at}: \"{text.Text}\" is cut");
		Assert.True(text.TextLayout.WidthIncludingTrailingWhitespace <= text.Bounds.Width + 0.5, $"{at}: \"{text.Text}\" is wider than its room");
	}

	private static Button Pill(MainWindow window) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SupportPill");

	private static Button Star(MainWindow window) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StarButton");

	// A star pill beside the support pill: star icon and "Yıldızla" / "Star" (whole, not cut), its name and tooltip "GitHub'da yıldız ver" / "Star on GitHub"; keyboard focusable;
	// a click opens the project's GitHub page through the same URL launcher.
	[AvaloniaFact]
	public void Sidebar_HasTheStarButton_ItOpensTheRepository()
	{
		foreach ((Language language, string code, string tip, string label) in new[]
		{
			(Language.Turkish, "tr", "GitHub'da yıldız ver", "Yıldızla"),
			(Language.English, "en", "Star on GitHub", "Star"),
		})
		{
			Run(language, code, (window, vm, links) =>
			{
				Button star = Star(window);
				Assert.True(star.IsEffectivelyVisible, $"{code}: no star button");
				Assert.Equal(tip, ToolTip.GetTip(star));
				Assert.Equal(tip, Avalonia.Automation.AutomationProperties.GetName(star));
				Assert.True(star.Focusable && star.IsTabStop, $"{code}: the star cannot take keyboard focus");
				Assert.Same(IconPaths.Star, star.GetVisualDescendants().OfType<IconSlot>().Single().Data);
				Assert.Equal(new CornerRadius(18), star.CornerRadius);
				Assert.Equal(36, star.Bounds.Height, 1);
				TextBlock text = star.GetVisualDescendants().OfType<TextBlock>().Single();
				Assert.Equal(label, text.Text);
				Assert.False(text.TextLayout.TextLines.Any(t => t.HasCollapsed), $"{code}: \"{text.Text}\" is cut");
				Assert.True(text.TextLayout.WidthIncludingTrailingWhitespace <= text.Bounds.Width + 0.5, $"{code}: \"{text.Text}\" is wider than its room");
				star.Command!.Execute(null);
				Assert.Equal(["https://github.com/Midzer00/ds5-untethered"], links.Opened);
				Assert.Same(vm.Overview, vm.SelectedNav.Page);
			});
		}
	}

	// With KOPRU_STAR_PREVIEW_DIR set: the sidebar's support row in both languages, 1152 wide with one controller and
	// 1100 wide with four, saved as star-*.png for a look (no checks beyond the star's text being whole).
	[AvaloniaFact]
	public void Sidebar_StarPreview()
	{
		if (Environment.GetEnvironmentVariable("KOPRU_STAR_PREVIEW_DIR") is not { Length: > 0 } dir)
		{
			return;
		}
		Directory.CreateDirectory(dir);
		foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
		{
			foreach ((string state, int width) in new[] { ("one", 1152), ("four", 1100) })
			{
				Run(language, code, (window, _, _) =>
				{
					TextBlock text = Star(window).GetVisualDescendants().OfType<TextBlock>().Single();
					Assert.False(text.TextLayout.TextLines.Any(t => t.HasCollapsed), $"{code} {width}: the star's text is cut");
					using var frame = window.CaptureRenderedFrame();
					frame!.Save(Path.Combine(dir, $"star-{code}-{width}-{state}.png"));
				}, state, width);
			}
		}
	}

	// The Overview with the bridge on and a game running (a made-up game), the URL launcher a recording fake. States:
	// "one" controller, "ds4-pending" (DualShock 4 for every game with the running game's own choice held back), "four"
	// controllers.
	private static void Run(Language language, string code, Action<MainWindow, MainWindowViewModel, RealServiceTests.Links> check, string state = "one", int width = 1152)
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = language;
			var links = new RealServiceTests.Links();
			var fake = new FakeControllerService(animate: false);
			// A made-up game in front (the fake's game exe, under another name).
			fake.Games.SetCatalog(new GameCatalog([new CatalogGame("Neon Drift", ["Cyberpunk2077.exe"], null, true, true, "high")]));
			if (state == "four")
			{
				for (int i = 1; i <= 4; i++)
				{
					fake.ControllerList.Add(new ControllerEntry($"c{i}", i, i % 2 == 0 ? "DualSense Edge" : "DualSense", i % 2 == 0, true, 20 * i, false, BridgeState.Running));
				}
			}
			var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = code }),
				Links: links));
			vm.Overview.ToggleBridgeCommand.Execute(null);
			fake.Tick();
			if (state == "ds4-pending")
			{
				fake.Watcher.Exit("Cyberpunk2077.exe");
				fake.Games.Tick();
				fake.Games.SetForcedPadType(Kopru.Core.Virtual.VirtualPadType.DualShock4);
				fake.Watcher.Exe = "Cyberpunk2077.exe";
				fake.Games.Tick();
				fake.Games.SetPadType(fake.Games.ActiveGame!.Key, Kopru.Core.Virtual.VirtualPadType.Xbox360);
				Assert.True(vm.Settings.HasPendingPadType);
			}
			fake.RaiseStateChanged();
			var window = new MainWindow { DataContext = vm, Width = width, Height = 700 };
			window.Show();
			vm.SelectedNav = vm.NavItems[0];
			Settle();
			try
			{
				check(window, vm, links);
			}
			finally
			{
				window.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static Color Brush(string key)
	{
		Assert.True(Application.Current!.TryFindResource(key, out object? value), key);
		return ((ISolidColorBrush)value!).Color;
	}

	// `top` (with its alpha) over the opaque `below`.
	private static Color Over(Color top, Color below)
	{
		double a = top.A / 255.0;
		byte Mix(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a));
		return Color.FromRgb(Mix(top.R, below.R), Mix(top.G, below.G), Mix(top.B, below.B));
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
