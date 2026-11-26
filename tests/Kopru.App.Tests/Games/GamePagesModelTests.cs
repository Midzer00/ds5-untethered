// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.Tests;

// The two game pages of the one-profile model: "Oyunlar" (Supported | Unsupported, the default controller type, a type
// waiting for the running game with Apply Now, "Oyna" where we know how to start a game) and "Titreşim ve Tetikler" (the
// active profile's game effects and classic rumble, no game list; a link to the unsupported games). Each setting has one
// place. Writes games-model-{all|installed|unsupported|pending|haptics}-{w}x{h}-tr.png to
// KOPRU_PAGE_RENDER_DIR, and haptics-fit-{w}x{h}-{tr|en}.png to KOPRU_FIT_RENDER_DIR, when they are set.
public sealed class GamePagesModelTests
{
	[AvaloniaFact]
	public void PageNames_AreGamesAndHapticsAndTriggers()
	{
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
		Assert.Equal(Text.PageGames(), vm.SupportedGames.Title);
		Assert.Equal(Text.PageHapticsTriggers(), vm.OtherGames.Title);
		Assert.Equal("Oyunlar", Text.PageGames(Language.Turkish));
		Assert.Equal("Titreşim ve Tetikler", Text.PageHapticsTriggers(Language.Turkish));
		Assert.Contains(vm.NavItems, n => n.Title == Text.PageGames());
		Assert.Contains(vm.NavItems, n => n.Title == Text.PageHapticsTriggers());
	}

	// The running game and what it sees on the Games page; "Profil: …" (the active profile) on Haptics And Triggers.
	[AvaloniaFact]
	public void Headers_TheRunningGame_AndTheEditedProfile()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		fake.Games.Tick();
		Assert.Equal("Cyberpunk 2077 · DualSense", vm.SupportedGames.CurrentGameText);
		Assert.True(vm.SupportedGames.IsActive);
		Assert.Equal(Text.ControllerProfileLine(Text.ProfileDefault()), vm.OtherGames.ProfileText);

		var changed = new List<string?>();
		vm.OtherGames.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
		vm.Settings.ActivateProfile(vm.Settings.Profiles.Create("Yarış", vm.Settings.Profiles.Default.Values)!.Id);
		Assert.Equal(Text.ControllerProfileLine("Yarış"), vm.OtherGames.ProfileText);
		Assert.Contains(nameof(OtherGamesViewModel.ProfileText), changed);

		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(Text.NoGameYet(), vm.SupportedGames.CurrentGameText);
		Assert.False(vm.SupportedGames.IsActive);
	}

	// A type chosen for the running game waits: the note, the game and type, and Apply Now above the list; Apply Now puts it in.
	[AvaloniaFact]
	public void PendingType_ShowsTheNote_AndApplyNowPutsItIn()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.Turkish;
			MainWindowViewModel vm = GameListScrollTests.Open(installedOnly: false, withRunningGame: true);
			var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
			window.Show();
			vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
			FitCheck.Settle(window);
			Border pending = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PendingPadType");
			Assert.False(pending.IsEffectivelyVisible);

			MakePending(vm);
			FitCheck.Settle(window);
			Assert.True(pending.IsEffectivelyVisible);
			Assert.Equal("Cyberpunk 2077 · Xbox 360", vm.SupportedGames.PendingGameLine);
			string[] shown = pending.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToArray();
			Assert.Contains(Text.PadTypeDeferred(), shown);
			Assert.Contains("Cyberpunk 2077 · Xbox 360", shown);
			Button apply = pending.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ApplyPadTypeNowButton");
			Assert.Equal(Text.PadTypeApplyNow(), apply.Content);
			Border card = pending.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("panel"));
			Assert.Empty(FitCheck.CutText(pending).Concat(FitCheck.Inside(card, window)));

			apply.Command!.Execute(apply.CommandParameter);
			FitCheck.Settle(window);
			Assert.False(vm.Settings.HasPendingPadType);
			Assert.Equal(VirtualPadType.Xbox360, vm.Settings.CurrentPadType);
			Assert.False(pending.IsEffectivelyVisible);
			Assert.Equal("Cyberpunk 2077 · Xbox 360", vm.SupportedGames.CurrentGameText);
			window.Close();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// One place for each setting: the profile's sliders are on Haptics And Triggers only (the game's light is the Light
	// page's); the Games page has no sliders or switches.
	[AvaloniaFact]
	public void EachSetting_HasOnePlace()
	{
		MainWindowViewModel vm = GameListScrollTests.Open(installedOnly: false);
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
		FitCheck.Settle(window);
		SupportedGamesView games = window.GetVisualDescendants().OfType<SupportedGamesView>().Single();
		Assert.Empty(games.GetVisualDescendants().OfType<Slider>());
		// The one exception: the Xbox view's Xbox Trigger Rumble switch, the same setting as Haptics And Triggers' chips (kept in step).
		Assert.Empty(games.GetVisualDescendants().OfType<ToggleSwitch>().Where(t => t.TemplatedParent == null && t.Name != "GamesXboxTriggerRumbleSwitch"));

		vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.OtherGames);
		FitCheck.Settle(window);
		OtherGamesView haptics = window.GetVisualDescendants().OfType<OtherGamesView>().Single();
		string[] hapticsSwitches = haptics.GetVisualDescendants().OfType<ToggleSwitch>()
			.Where(t => t.TemplatedParent == null)
			.Select(t => t.Name ?? string.Empty)
			.ToArray();
		// Xbox Trigger Rumble is a row of chips here (Off and the three feels), not a switch.
		Assert.Equal(["PauseHapticsSwitch"], hapticsSwitches);
		Assert.Empty(haptics.GetVisualDescendants().OfType<GameRowList>());
		string[] cards = haptics.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("panel")).Select(b => b.Name ?? string.Empty).ToArray();
		Assert.Equal(["GameEffectsCard", "ClassicRumbleCard"], cards);
		Assert.Equal(2, CardSliders(haptics, "GameEffectsCard"));
		vm.OtherGames.SelectedTab = OtherGamesViewModel.RumbleTab;
		FitCheck.Settle(window);
		Assert.Equal(2, CardSliders(haptics, "ClassicRumbleCard"));
		Assert.Empty(haptics.GetVisualDescendants().OfType<ToggleSwitch>().Where(t => t.TemplatedParent == null && t.IsEffectivelyVisible));
		window.Close();
	}

	// Haptics And Triggers has two tabs under its header, named after the cards (the game's own effects first, the default);
	// each shows one card, which starts with its caption (no icon or title line: the tab is the title). The selected tab is
	// kept while the app runs, across page switches.
	[AvaloniaFact]
	public void HapticsPage_TwoTabs_OneCardEach_KeptWhileTheAppRuns()
	{
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				MainWindowViewModel vm = GameListScrollTests.Open(installedOnly: false, code);
				string[] names = code == "tr" ? ["Oyunun Kendi Efektleri", "Xbox ve DS4 Titreşimi"] : ["Game's Own Effects", "Xbox And DS4 Rumble"];
				Assert.Equal(names, vm.OtherGames.Tabs.Select(t => t.Name));
				Assert.Equal([Text.GameEffectsCard(), Text.ClassicRumbleCard()], vm.OtherGames.Tabs.Select(t => t.Name));
				Assert.Equal(OtherGamesViewModel.GameEffectsTab, vm.OtherGames.SelectedTab);

				var window = new MainWindow { DataContext = vm, Width = 1152, Height = 700 };
				window.Show();
				vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.OtherGames);
				FitCheck.Settle(window);
				OtherGamesView view = window.GetVisualDescendants().OfType<OtherGamesView>().Single();
				ListBox tabs = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "HapticsTabs");
				Assert.Same(Application.Current!.FindResource("Segmented"), tabs.Theme);
				Assert.Equal(0, tabs.SelectedIndex);
				Border game = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "GameEffectsCard");
				Border rumble = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ClassicRumbleCard");
				Grid header = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Classes.Contains("pageHeader"));
				Assert.True(FitCheck.Bounds(tabs, window).Top >= FitCheck.Bounds(header, window).Bottom);
				Assert.True(game.IsEffectivelyVisible);
				Assert.False(rumble.IsEffectivelyVisible);
				Assert.True(FitCheck.Bounds(game, window).Top >= FitCheck.Bounds(tabs, window).Bottom);

				tabs.SelectedIndex = 1;
				FitCheck.Settle(window);
				Assert.Equal(OtherGamesViewModel.RumbleTab, vm.OtherGames.SelectedTab);
				Assert.False(game.IsEffectivelyVisible);
				Assert.True(rumble.IsEffectivelyVisible);

				// No icon or title line in either card: the caption comes first.
				foreach ((Border card, string caption) in new[] { (game, Text.GameEffectsCardHint()), (rumble, Text.ClassicRumbleCardHint()) })
				{
					Assert.Empty(card.GetVisualDescendants().OfType<IconSlot>().Where(i => i.GetVisualAncestors().OfType<StackPanel>().Any(p => p.Classes.Contains("cardHeader"))));
					Assert.DoesNotContain(card.GetVisualDescendants().OfType<TextBlock>(), t => t.Classes.Contains("h2"));
					TextBlock first = card.GetLogicalDescendants().OfType<TextBlock>().First();
					Assert.Equal(caption, first.Text);
					Assert.Contains("caption", first.Classes);
				}

				// Another page and back: still the rumble tab; out of range values are ignored.
				vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
				FitCheck.Settle(window);
				vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.OtherGames);
				FitCheck.Settle(window);
				Assert.Equal(OtherGamesViewModel.RumbleTab, vm.OtherGames.SelectedTab);
				Assert.True(view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ClassicRumbleCard").IsEffectivelyVisible);
				vm.OtherGames.SelectedTab = -1;
				vm.OtherGames.SelectedTab = 2;
				Assert.Equal(OtherGamesViewModel.RumbleTab, vm.OtherGames.SelectedTab);
				window.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The Try row: its label alone, the hint as the label's tooltip; the rumble feel and Xbox Trigger Rumble are segmented
	// choices in the shared control widths.
	[AvaloniaFact]
	public void HapticsPage_TryHintIsATooltip_ChoicesAreSegmented()
	{
		MainWindowViewModel vm = GameListScrollTests.Open(installedOnly: false, "tr");
		var window = new MainWindow { DataContext = vm, Width = 960, Height = 640 };
		window.Show();
		vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.OtherGames);
		vm.OtherGames.SelectedTab = OtherGamesViewModel.RumbleTab;
		FitCheck.Settle(window);
		OtherGamesView view = window.GetVisualDescendants().OfType<OtherGamesView>().Single();
		Grid tryRow = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TryRow");
		TextBlock label = tryRow.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "TryLabel");
		Assert.Equal(vm.OtherGames.TryLabel, label.Text);
		Assert.Equal(vm.OtherGames.TryHint, ToolTip.GetTip(label));
		Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.OtherGames.TryHint);
		object segmented = Application.Current!.FindResource("Segmented")!;
		ListBox feel = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "RumbleFeelChoice");
		ListBox xbox = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "XboxTriggerChoice");
		Assert.Same(segmented, feel.Theme);
		Assert.Same(segmented, xbox.Theme);
		Assert.Equal((double)Application.Current!.FindResource("ControlWidth")!, feel.Bounds.Width, 0.5);
		Assert.Equal((double)Application.Current!.FindResource("ControlWidthWide")!, xbox.Bounds.Width, 0.5);
		Assert.Equal(["Yumuşak", "Sert"], feel.ItemsSource!.Cast<LabelledChoice>().Select(c => c.Name));
		Assert.Empty(view.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.TemplatedParent == null && t.IsEffectivelyVisible));
		feel.SelectedIndex = 1;
		Assert.True(vm.OtherGames.Feel.IsPunchy);
		feel.SelectedIndex = 0;
		Assert.True(vm.OtherGames.Feel.IsSoft);
		window.Close();
	}

	// Haptics And Triggers at every size of the fit matrix, in both languages, on both tabs: the shared row metrics (every
	// row at least SettingRowHeight, its text 10 clear of the dividers), no scrolling at all (the page's scroll viewer is only a
	// safety net: its extent is never taller than its viewport), every card's controls inside it and no text cut off. The
	// Try row is the last row of the rumble tab, in view without scrolling.
	[AvaloniaFact]
	public void HapticsPage_FitsAtEverySize()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_FIT_RENDER_DIR") is { Length: > 0 } d ? d : null;
		Language? before = Localizer.Chosen;
		List<string> problems = [];
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height) in FitCheck.Matrix.Union(FitCheck.ReviewSizes))
				{
					MainWindowViewModel vm = GameListScrollTests.Open(installedOnly: false, code);
					// The fourth try button (the fallback effect) shows: the widest Try row.
					vm.OtherGames.Feel.FallbackIndex = 1;
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.OtherGames);
					foreach ((int tab, string tabName) in new[] { (OtherGamesViewModel.GameEffectsTab, "game"), (OtherGamesViewModel.RumbleTab, "rumble") })
					{
						vm.OtherGames.SelectedTab = tab;
						FitCheck.Settle(window);
						string where = $"{width}x{height} {code} {tabName}";
						problems.AddRange(CheckHapticsTab(window, where));
						if (outDir != null)
						{
							using var frame = window.CaptureRenderedFrame();
							Directory.CreateDirectory(outDir);
							frame!.Save(Path.Combine(outDir, $"haptics-fit-{width}x{height}-{code}-{tabName}.png"));
						}
					}
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
		Assert.Empty(problems);
	}

	// The fit checks of the shown Haptics And Triggers tab (also used by the pseudo-locale test).
	internal static IEnumerable<string> CheckHapticsTab(Window window, string where)
	{
		List<string> problems = [];
		OtherGamesView view = window.GetVisualDescendants().OfType<OtherGamesView>().Single();
		ScrollViewer scroll = view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PageScroll");
		problems.AddRange(FitCheck.Scrolls(scroll, where));
		problems.AddRange(FitCheck.CutText(view).Select(p => $"{where}: {p}"));
		double rowHeight = (double)Application.Current!.FindResource("SettingRowHeight")!;
		foreach (Grid row in view.GetVisualDescendants().OfType<Grid>().Where(g => g.IsEffectivelyVisible && g.Classes.Contains("settingRow")))
		{
			StackPanel text = row.Children.OfType<StackPanel>().Single(p => p.Classes.Contains("text"));
			Rect t = FitCheck.Bounds(text, row);
			string label = text.GetVisualDescendants().OfType<TextBlock>().First().Text ?? "?";
			if (row.Bounds.Height < rowHeight - 0.5 || t.Top < 9.5 || row.Bounds.Height - t.Bottom < 9.5)
			{
				problems.Add($"{where}: row \"{label}\" is squeezed (height {row.Bounds.Height:0.#}, text {t.Top:0.#} to {t.Bottom:0.#})");
			}
		}
		Grid tryRow = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TryRow");
		if (tryRow.IsEffectivelyVisible)
		{
			Rect r = FitCheck.Bounds(tryRow, scroll);
			if (r.Top < -0.5 || r.Bottom > scroll.Viewport.Height + 0.5)
			{
				problems.Add($"{where}: the Try row {r} is not in view ({scroll.Viewport.Height:0.#} high)");
			}
		}
		Border[] cards = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("panel") && b.IsEffectivelyVisible).ToArray();
		if (cards.Length != 1)
		{
			problems.Add($"{where}: {cards.Length} cards shown, not one");
		}
		foreach (Border card in cards)
		{
			problems.AddRange(FitCheck.Inside(card, window).Select(p => $"{where}: {p}"));
		}
		return problems;
	}

	// The PNGs for the review: Games (All, Installed, a pending type) and Haptics And Triggers in Turkish at 1100x700 and
	// 1920x1080.
	[AvaloniaFact]
	public async Task RenderGamePages()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_PAGE_RENDER_DIR");
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.Turkish;
			foreach ((double width, double height) in new[] { (1100.0, 700.0), (1920.0, 1080.0) })
			{
				foreach (string shot in new[] { "all", "installed", "unsupported", "pending", "haptics" })
				{
					MainWindowViewModel vm = GameListScrollTests.Open(installedOnly: shot == "installed", withRunningGame: true, unsupported: shot == "unsupported");
					await vm.SupportedGames.ScanTask;
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					if (shot == "pending")
					{
						MakePending(vm);
					}
					vm.SelectedNav = vm.NavItems.Single(n => n.Page == (shot == "haptics" ? (PageViewModel)vm.OtherGames : vm.SupportedGames));
					FitCheck.Settle(window);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();
					using var frame = window.CaptureRenderedFrame();
					Assert.NotNull(frame);
					if (!string.IsNullOrEmpty(outDir))
					{
						Directory.CreateDirectory(outDir);
						frame!.Save(Path.Combine(outDir, $"games-model-{shot}-{width}x{height}-tr.png"));
					}
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// Supported | Unsupported: the catalog games on one side, the found and added games the list does not know on the other;
	// "All | Installed" only filters the supported side. The same rows and the same per-game memory on both.
	[AvaloniaFact]
	public async Task Views_SplitSupportedAndUnsupported()
	{
		MainWindowViewModel vm = GameListScrollTests.Open(installedOnly: false);
		SupportedGamesViewModel games = vm.SupportedGames;
		await games.ScanTask;
		Dispatcher.UIThread.RunJobs();
		Assert.False(games.ShowUnsupported);
		Assert.All(games.Games, g => Assert.False(g.IsUnsupported));
		Assert.Equal(Text.UnsupportedViewCount(games.UnsupportedGames.Count), games.ViewFilters[2].Name);
		Assert.True(games.ShowsInstalledChoice);

		games.ViewIndex = 2;
		Assert.True(games.ShowUnsupported);
		Assert.False(games.ShowsInstalledChoice);
		Assert.Equal(games.UnsupportedGames, games.Games);
		Assert.Contains(games.Games, g => g.IsFound);
		Assert.Contains(games.Games, g => g.IsUserAdded);
		Assert.True(games.ShowsAddHint);

		// A choice made on the unsupported side is the game's one choice.
		GameItemViewModel row = games.Games.First(g => g.IsUserAdded);
		row.PadType!.Selected = row.PadType.Options.Single(o => o.Type == VirtualPadType.DualShock4);
		games.ViewIndex = 0;
		games.ViewIndex = 2;
		Assert.Equal(VirtualPadType.DualShock4, games.Games.Single(g => g.Key == row.Key).PadType!.Selected!.Type);

		// "Installed" on the supported side: catalog games found here only.
		games.ViewIndex = 0;
		vm.Settings.InstalledGamesOnly = true;
		Assert.All(games.Games, g => Assert.True(g.IsInstalled && !g.IsUnsupported));
	}

	// "Desteklenmeyen Oyunlar" on Haptics And Triggers opens the Games page on that view.
	[AvaloniaFact]
	public void HapticsLink_OpensTheUnsupportedView()
	{
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
		vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.OtherGames);
		Assert.Equal(Text.UnsupportedGamesTitle(), vm.OtherGames.UnsupportedLinkText);
		vm.OtherGames.ShowUnsupportedGamesCommand.Execute(null);
		Assert.Same(vm.SupportedGames, vm.CurrentPage);
		Assert.True(vm.SupportedGames.ShowUnsupported);
	}

	// How "Oyna" starts a game (no real launch): Steam and Epic through their launchers, an added game or a catalog game
	// found in a folder by its exe in that folder; nothing when we don't know how.
	[Fact]
	public void LaunchCommands_AreBuiltFromWhatWeKnow()
	{
		static bool Exists(string path) => path is @"C:\Games\Indie\Indie.exe" or @"D:\Games\Returnal\Returnal-Win64-Shipping.exe";
		var catalog = new CatalogGame("Returnal", ["Returnal-Win64-Shipping.exe"], 1649240, true, true);
		var catalogRow = new KnownGame("Returnal", "Returnal-Win64-Shipping.exe", true, true, false, catalog, "returnal");

		GameLaunch steam = GameLauncher.For(new KnownGame("Pixel Racer", "Racer.exe", false, false, false, null, "steam:9009"),
			new FoundGame("Pixel Racer", InstalledSource.Steam, @"C:\Steam\steamapps\common\PixelRacer", "9009"), null, Exists)!;
		Assert.Equal((LaunchVia.Steam, "steam://rungameid/9009", "steam"), (steam.Via, steam.Target, steam.ViaName));

		GameLaunch epic = GameLauncher.For(new KnownGame("Hollow Peak", "HollowPeak.exe", false, false, false, null, "epic:HollowPeakApp"),
			new FoundGame("Hollow Peak", InstalledSource.Epic, @"E:\Epic\Hollow Peak", "HollowPeakApp", @"E:\Epic\Hollow Peak\HollowPeak.exe"), null, Exists)!;
		Assert.Equal((LaunchVia.Epic, "com.epicgames.launcher://apps/HollowPeakApp?action=launch&silent=true"), (epic.Via, epic.Target));

		GameLaunch added = GameLauncher.For(new KnownGame("Indie Game", "Indie.exe", false, false, true, null, "indie", @"C:\Games\Indie\Indie.exe"), null, null, Exists)!;
		Assert.Equal((LaunchVia.Exe, @"C:\Games\Indie\Indie.exe", @"C:\Games\Indie"), (added.Via, added.Target, added.WorkingDirectory));
		Assert.Null(GameLauncher.For(new KnownGame("Gone", "Gone.exe", false, false, true, null, "gone", @"C:\Games\Gone\Gone.exe"), null, null, Exists));

		Assert.Equal("steam://rungameid/1649240", GameLauncher.For(catalogRow, null, new InstalledGame(catalog, InstalledSource.Steam, @"C:\Steam\steamapps\common\Returnal"), Exists)!.Target);
		GameLaunch folder = GameLauncher.For(catalogRow, null, new InstalledGame(catalog, InstalledSource.Folder, @"D:\Games\Returnal"), Exists)!;
		Assert.Equal((LaunchVia.Exe, @"D:\Games\Returnal\Returnal-Win64-Shipping.exe", @"D:\Games\Returnal"), (folder.Via, folder.Target, folder.WorkingDirectory));
		Assert.Null(GameLauncher.For(catalogRow, null, new InstalledGame(catalog, InstalledSource.Epic, @"E:\Elsewhere"), Exists));
		Assert.Null(GameLauncher.For(catalogRow, null, null, Exists));

		// The shell opens the URL schemes and the exe (in its folder).
		Assert.True(GameLauncher.StartInfo(steam).UseShellExecute);
		Assert.Equal(@"C:\Games\Indie", GameLauncher.StartInfo(added).WorkingDirectory);
	}

	// "Oyna" for a catalog game installed on this PC, from every source: Steam by the manifest's app id (even when the
	// catalog has another one, e.g. an Enhanced edition, or none), Epic by its AppName or else its launch exe, and the
	// uninstall registry, a folder or a game seen running by the catalog's exe in that folder (at its top, the Unreal layout or
	// bin\x64). Without a way, the reason that is logged once.
	[Fact]
	public void LaunchCommands_ForInstalledCatalogGames_FromEverySource()
	{
		var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			@"D:\Games\GTAV\GTA5_Enhanced.exe", @"E:\Games\Returnal\Binaries\Win64\Returnal-Win64-Shipping.exe", @"F:\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe",
			@"E:\Epic\AlanWake2\AlanWake2.exe"
		};
		bool Exists(string path) => files.Contains(path);
		var gta = new CatalogGame("Grand Theft Auto V Enhanced", ["GTA5_Enhanced.exe"], 271590);
		var gtaNoId = gta with { SteamAppId = null };
		var returnal = new CatalogGame("Returnal", ["Returnal-Win64-Shipping.exe"]);
		var cyberpunk = new CatalogGame("Cyberpunk 2077", ["Cyberpunk2077.exe"], 1091500);
		var wake = new CatalogGame("Alan Wake 2", ["AlanWake2.exe"]);
		static KnownGame Row(CatalogGame g) => new(g.Name, g.Exe[0], true, true, false, g, g.Name);

		// Steam: the manifest's app id wins over the catalog's; the catalog's when the manifest gave none; the exe last.
		Assert.Equal("steam://rungameid/3240220", GameLauncher.For(Row(gta), null, new InstalledGame(gta, InstalledSource.Steam, @"C:\Steam\steamapps\common\GTAV", "3240220"), Exists)!.Target);
		Assert.Equal("steam://rungameid/3240220", GameLauncher.For(Row(gtaNoId), null, new InstalledGame(gtaNoId, InstalledSource.Steam, @"C:\Steam\steamapps\common\GTAV", "3240220"), Exists)!.Target);
		Assert.Equal("steam://rungameid/271590", GameLauncher.For(Row(gta), null, new InstalledGame(gta, InstalledSource.Steam, @"C:\Steam\steamapps\common\GTAV"), Exists)!.Target);
		Assert.Equal(@"D:\Games\GTAV\GTA5_Enhanced.exe", GameLauncher.For(Row(gtaNoId), null, new InstalledGame(gtaNoId, InstalledSource.Steam, @"D:\Games\GTAV"), Exists)!.Target);

		// Epic: the AppName, else the launch exe.
		Assert.Equal("com.epicgames.launcher://apps/Iris?action=launch&silent=true",
			GameLauncher.For(Row(wake), null, new InstalledGame(wake, InstalledSource.Epic, @"E:\Epic\AlanWake2", "Iris", @"E:\Epic\AlanWake2\AlanWake2.exe"), Exists)!.Target);
		GameLaunch epicExe = GameLauncher.For(Row(wake), null, new InstalledGame(wake, InstalledSource.Epic, @"E:\Epic\AlanWake2", null, @"E:\Epic\AlanWake2\AlanWake2.exe"), Exists)!;
		Assert.Equal((LaunchVia.Exe, @"E:\Epic\AlanWake2"), (epicExe.Via, epicExe.WorkingDirectory));

		// Registry, folder, seen: the catalog exe in the found folder.
		Assert.Equal(@"D:\Games\GTAV\GTA5_Enhanced.exe", GameLauncher.For(Row(gtaNoId), null, new InstalledGame(gtaNoId, InstalledSource.Registry, @"D:\Games\GTAV"), Exists)!.Target);
		Assert.Equal(@"E:\Games\Returnal\Binaries\Win64\Returnal-Win64-Shipping.exe",
			GameLauncher.For(Row(returnal), null, new InstalledGame(returnal, InstalledSource.Folder, @"E:\Games\Returnal"), Exists)!.Target);
		GameLaunch seen = GameLauncher.For(Row(cyberpunk), null, new InstalledGame(cyberpunk, InstalledSource.Seen, @"F:\Cyberpunk 2077"), Exists)!;
		Assert.Equal((LaunchVia.Exe, @"F:\Cyberpunk 2077\bin\x64"), (seen.Via, seen.WorkingDirectory));

		// No way: the reason (logged once by the list); a game that is not installed has none to give.
		(GameLaunch? none, string? why) = GameLauncher.Explain(Row(returnal), null, new InstalledGame(returnal, InstalledSource.Registry, @"G:\Elsewhere"), Exists);
		Assert.Null(none);
		Assert.Equal(@"registry: no catalog exe in G:\Elsewhere", why);
		Assert.Equal("registry: no folder", GameLauncher.Explain(Row(returnal), null, new InstalledGame(returnal, InstalledSource.Registry, null), Exists).Reason);
		Assert.Equal((null, null), GameLauncher.Explain(Row(returnal), null, null, Exists));
	}

	// "Oyna" on a found Steam game hands its command to the starter (no real launch); a start that fails shows a toast.
	[AvaloniaFact]
	public async Task Play_StartsTheGame_AndAFailureShowsAToast()
	{
		MainWindowViewModel vm = GameListScrollTests.Open(installedOnly: false, unsupported: true);
		await vm.SupportedGames.ScanTask;
		Dispatcher.UIThread.RunJobs();
		List<(GameLaunch Launch, string Name)> started = [];
		bool succeed = true;
		vm.SupportedGames.StartGame = (launch, name) =>
		{
			started.Add((launch, name));
			return succeed;
		};
		List<AppNotification> toasts = [];
		vm.Notifications.Raised += toasts.Add;

		GameItemViewModel racer = vm.SupportedGames.Games.Single(g => g.Name == "Pixel Racer");
		Assert.True(racer.CanLaunch);
		Assert.Equal(Text.PlayGame(), racer.PlayText);
		racer.PlayGameCommand.Execute(null);
		Assert.Equal(("steam://rungameid/9009", "Pixel Racer"), (started.Single().Launch.Target, started.Single().Name));
		Assert.Empty(toasts);

		succeed = false;
		racer.PlayGameCommand.Execute(null);
		AppNotification toast = Assert.Single(toasts);
		Assert.Equal(Text.GameLaunchFailedTitle(), toast.Title);
		Assert.Equal(Text.GameLaunchFailedBody("Pixel Racer"), toast.Body);
		Assert.Equal(NotificationKind.Error, toast.Kind);

		// An added game whose exe is not on this PC has no Oyna.
		Assert.False(vm.SupportedGames.Games.First(g => g.IsUserAdded).CanLaunch);
	}

	[Fact]
	public void GameListTexts_PlainAndInBothLanguages()
	{
		Assert.Equal("Desteklenen", Text.SupportedView(Language.Turkish));
		Assert.Equal("Desteklenmeyen (3)", Text.UnsupportedViewCount(3, Language.Turkish));
		Assert.Equal("Unsupported (3)", Text.UnsupportedViewCount(3, Language.English));
		Assert.Equal("Oyna", Text.PlayGame(Language.Turkish));
		Assert.Equal("Play", Text.PlayGame(Language.English));
		Assert.Equal("Oyununu bulamadık mı? Oyun Ekle ile ekle.", Text.InstalledAddHint(Language.Turkish));
		Assert.Contains("Xbox ve DS4 Titreşimi", Text.UnsupportedGamesHint(Language.Turkish));
	}

	// Xbox 360 chosen for the running game (Cyberpunk 2077) on its row: it waits for the game to restart.
	private static void MakePending(MainWindowViewModel vm)
	{
		Assert.Equal("Cyberpunk 2077", vm.SupportedGames.Games.Single(g => g.Name == "Cyberpunk 2077").Name);
		GameItemViewModel row = vm.SupportedGames.Games.Single(g => g.Name == "Cyberpunk 2077");
		row.PadType!.Selected = row.PadType.Options.Single(o => o.Type == VirtualPadType.Xbox360);
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.Settings.HasPendingPadType);
	}

	// The sliders shown (the fallback effect's shape sliders show only while an effect is chosen).
	private static int CardSliders(Visual view, string card) =>
		view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == card).GetVisualDescendants().OfType<Slider>().Count(s => s.IsEffectivelyVisible);
}
