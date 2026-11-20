// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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

// The experimental Xbox Trigger Rumble setting (Settings > Controllers): off by default for new and existing users; off,
// Automatic never gives Xbox Series (an Xbox Series choice still holds, without trigger rumble), the Games page's Xbox
// view says so with Turn On, and the Trigger Rumble rows are hidden. With KOPRU_OPTIN_PREVIEW_DIR set, renders optin2-settings-{off|on}-{w}x{h}-tr.png,
// optin2-haptics-{off|on}-1152x700-tr.png and optin-games-tr.png there (made-up games).
public sealed class XboxTriggerOptInAppTests
{
	// Made-up games: Pixel Racer (found by Steam, 9009) and Hollow Peak (Epic) are in the fake scan; Thunder Kart Rally is
	// not installed.
	private static readonly XboxTriggerGames Fixture = XboxTriggerGames.Parse("""
		{ "games": [
		  { "name": "Pixel Racer", "steamAppId": 9009, "exe": [] },
		  { "name": "Hollow Peak", "steamAppId": null, "exe": [] },
		  { "name": "Thunder Kart Rally", "steamAppId": 777001, "exe": [] }
		] }
		""");

	private static readonly (double Width, double Height)[] Sizes = [(1100, 700), (1152, 700)];

	private static async Task<(MainWindowViewModel Vm, FakeControllerService Service, MemorySettingsStore Store)> OpenAsync(AppSettings? settings = null)
	{
		var service = new FakeControllerService(animate: false);
		service.Games.SetCatalog(new GameCatalog([new CatalogGame("Glass Comet", ["GlassComet.exe"], null, true, false, "high")]));
		service.Watcher.Exit("Cyberpunk2077.exe");
		service.Watcher.Exe = null;
		service.Games.Tick();
		service.Games.PlayStation = new PlayStationSupport([]);
		service.Games.TriggerGames = Fixture;
		service.Games.XboxSeriesOsSupported = true;
		var store = new MemorySettingsStore(settings ?? new AppSettings { GameListCheckedAt = DateTimeOffset.Now });
		var vm = new MainWindowViewModel(service, new AppEnvironment(store, InstalledGames: FakeFoundGames.Installed(FakeFoundGames.Folders())));
		await vm.SupportedGames.ScanTask;
		return (vm, service, store);
	}

	private static void WithLanguage(Language language, Action action)
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = language;
			action();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static string? PreviewDir => Environment.GetEnvironmentVariable("KOPRU_OPTIN_PREVIEW_DIR") is { Length: > 0 } dir ? dir : null;

	private static void Save(Window window, string name)
	{
		if (PreviewDir is { } dir)
		{
			Directory.CreateDirectory(dir);
			Thread.Sleep(320);
			FitCheck.Settle(window);
			using var frame = window.CaptureRenderedFrame();
			frame!.Save(Path.Combine(dir, name));
		}
	}

	// ---- the setting ----

	[AvaloniaFact]
	public async Task Setting_IsOffByDefault_ForNewAndExistingUsers()
	{
		Assert.False(new AppSettings().XboxTriggerRumbleOn);
		// A settings file from before the setting (no such field): off.
		AppSettings? old = JsonSerializer.Deserialize<AppSettings>("""{ "RumbleLevel": 40, "XboxTriggerGames": ["steam:9009"] }""");
		Assert.False(old!.XboxTriggerRumbleOn);
		Assert.False(old.ToGameMemory(new GameCatalog([])).XboxTriggerRumbleOn);

		(MainWindowViewModel vm, FakeControllerService service, MemorySettingsStore store) = await OpenAsync();
		Assert.False(vm.Settings.XboxTriggerRumbleOn);
		Assert.False(service.Games.XboxTriggerRumbleOn);
		vm.Settings.SaveNow();
		Assert.False(store.Saved!.XboxTriggerRumbleOn);

		// On is saved, and read back.
		vm.Settings.XboxTriggerRumbleOn = true;
		Assert.True(service.Games.XboxTriggerRumbleOn);
		vm.Settings.SaveNow();
		Assert.True(store.Saved!.XboxTriggerRumbleOn);
		(MainWindowViewModel again, FakeControllerService next, _) = await OpenAsync(store.Saved);
		Assert.True(again.Settings.XboxTriggerRumbleOn);
		Assert.True(next.Games.XboxTriggerRumbleOn);
	}

	[AvaloniaFact]
	public async Task Off_AutomaticNeverGivesXboxSeries_AChoiceStillHolds()
	{
		(MainWindowViewModel vm, FakeControllerService service, _) = await OpenAsync();
		service.Games.LoadMemory(service.Games.SnapshotMemory() with { XboxTriggerGames = ["steam:9020"] });
		Dispatcher.UIThread.RunJobs();
		// Listed, learned and unknown games: Xbox 360.
		Assert.Equal(VirtualPadType.Xbox360, service.Games.AutomaticPadTypeFor("steam:9009"));
		Assert.Equal(VirtualPadType.Xbox360, service.Games.AutomaticPadTypeFor("steam:9020"));
		Assert.Equal(VirtualPadType.Xbox360, service.Games.AutomaticPadTypeFor("Thunder Kart Rally"));

		// The Xbox Series choice is an ordinary type (no Experimental mark) and still honoured.
		PadChoiceOption series = vm.Settings.PadType.Options.Single(o => o.Type == VirtualPadType.XboxSeries);
		WithLanguage(Language.Turkish, () => Assert.Equal("Xbox Series", series.Name));
		WithLanguage(Language.English, () => Assert.Equal("Xbox Series", series.Name));
		vm.Settings.PadType.Selected = series;
		Assert.Equal(VirtualPadType.XboxSeries, service.Games.CurrentPadType);
		vm.Settings.PadType.Selected = vm.Settings.PadType.Options[0];
		GameItemViewModel row = vm.SupportedGames.Games.First();
		row.PadType!.Selected = row.PadType.Options.Single(o => o.Type == VirtualPadType.XboxSeries);
		Assert.Equal(VirtualPadType.XboxSeries, service.Games.PadTypeFor(row.Key));
		Assert.Equal("Xbox Series", row.PadType.Selected!.Name);

		// On: the listed and learned games get Xbox Series, the others keep Xbox 360.
		vm.Settings.XboxTriggerRumbleOn = true;
		Assert.Equal(VirtualPadType.XboxSeries, service.Games.AutomaticPadTypeFor("steam:9009"));
		Assert.Equal(VirtualPadType.XboxSeries, service.Games.AutomaticPadTypeFor("steam:9020"));
		Assert.Equal(VirtualPadType.Xbox360, service.Games.AutomaticPadTypeFor("steam:9030"));
	}

	// The switch has its own Triggers card in the Controllers tab's right column, under DSX Mod Support, styled like the other
	// switch cards: the section label over a panel, the title with a muted Experimental tag, one description line, the switch
	// on the right. The old Experimental card under the profiles is gone; Haptics sits there instead. On and off both fit.
	[AvaloniaFact]
	public void SettingsCard_IsInTheRightColumn_WithTitleTagLineAndSwitch_AndFits()
	{
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				(MainWindowViewModel vm, FakeControllerService fake) = SettingsFitTests.FullCase(code);
				SettingsViewModel page = vm.SettingsPage;
				Assert.Equal(code == "tr" ? "Tetikler" : "Triggers", page.TriggersGroupLabel);
				Assert.Equal(code == "tr" ? "Xbox Tetik Titreşimi" : "Xbox Trigger Rumble", page.XboxTriggerRumbleTitle);
				Assert.Equal(code == "tr" ? "Deneysel" : "Experimental", page.ExperimentalTag);
				Assert.Equal(code == "tr"
					? "Xbox tetik titreşimi olan oyunlarda titreşimi DualSense tetiklerine taşır."
					: "Plays Xbox trigger rumble on the DualSense triggers in games that use it.", page.XboxTriggerRumbleShort);
				Assert.Equal(code == "tr" ? "Xbox Tetik Titreşimi (Deneysel)" : "Xbox Trigger Rumble (Experimental)", page.XboxTriggerRumbleLabel);
				Assert.Equal(code == "tr"
					? "Açıkken Xbox tetik titreşimi olan oyunlara Xbox Series kumanda gösterilir; Xbox Series türünde oyunun tetik titreşimi DualSense tetiklerinde hissedilir. Kapalıyken oyunun tetik titreşimi yok sayılır."
					: "When on, games with Xbox trigger rumble see an Xbox Series controller, and with the Xbox Series type the game's trigger rumble plays on the DualSense triggers. When off, the game's trigger rumble is ignored.",
					page.XboxTriggerRumbleHint);
				foreach ((double width, double height) in Sizes)
				{
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.OpenSettingsAt(SettingsViewModel.ControllersTab);
					FitCheck.Settle(window);
					Assert.Empty(SettingsFitTests.Check(window));
					List<Control> all = window.GetVisualDescendants().OfType<Control>().ToList();
					Assert.DoesNotContain(all, c => c.Name == "ExperimentalPanel");

					// Its own card: a section label over the panel, like DSX Mod Support's, below it in the same column.
					Border panel = all.OfType<Border>().Single(b => b.Name == "XboxTriggerRumblePanel");
					Border mods = all.OfType<Border>().Single(b => b.Name == "ModsPanel");
					Border haptics = all.OfType<Border>().Single(b => b.Name == "HapticsPanel");
					DockPanel card = Assert.IsType<DockPanel>(panel.GetVisualParent());
					Assert.Contains("card", card.Classes);
					Assert.Equal(1, SettingsLayout.GetColumn(card));
					TextBlock section = card.Children.OfType<TextBlock>().Single(t => t.Classes.Contains("section"));
					Assert.Equal(page.TriggersGroupLabel, section.Text);
					Assert.True(section.IsEffectivelyVisible);
					Rect panelBox = FitCheck.Bounds(panel, window), modsBox = FitCheck.Bounds(mods, window), hapticsBox = FitCheck.Bounds(haptics, window);
					Assert.True(panelBox.Top > modsBox.Bottom, $"{code} {width}: not under DSX Mod Support");
					Assert.Equal(modsBox.Left, panelBox.Left, 1);
					Assert.Equal(modsBox.Width, panelBox.Width, 1);
					Assert.True(hapticsBox.Right < panelBox.Left, $"{code} {width}: Haptics not in the left column");

					// The row: title with the muted tag beside it, the description line, the switch on the right.
					Grid row = all.OfType<Grid>().Single(g => g.Name == "XboxTriggerRumbleRow");
					Assert.Same(panel, row.GetVisualAncestors().OfType<Border>().First());
					Assert.Equal(page.XboxTriggerRumbleHint, ToolTip.GetTip(row));
					TextBlock title = all.OfType<TextBlock>().Single(t => t.Name == "XboxTriggerRumbleTitle");
					TextBlock tag = all.OfType<TextBlock>().Single(t => t.Name == "XboxTriggerRumbleTag");
					TextBlock line = all.OfType<TextBlock>().Single(t => t.Name == "XboxTriggerRumbleLine");
					Assert.Equal(page.XboxTriggerRumbleTitle, title.Text);
					Assert.Equal(page.ExperimentalTag, tag.Text);
					Assert.Contains("caption", tag.Classes);
					Assert.Equal(KeepLastWords.Apply(page.XboxTriggerRumbleShort), line.Text);
					Assert.All(new[] { title, tag, line }, t => Assert.True(t.IsEffectivelyVisible));
					Rect titleBox = FitCheck.Bounds(title, window), tagBox = FitCheck.Bounds(tag, window), lineBox = FitCheck.Bounds(line, window);
					Assert.True(tagBox.Left > titleBox.Right && Math.Abs(tagBox.Center.Y - titleBox.Center.Y) < 4, $"{code} {width}: the tag is not beside the title");
					Assert.True(lineBox.Top >= titleBox.Bottom, $"{code} {width}: the line is not under the title");
					ToggleSwitch toggle = all.OfType<ToggleSwitch>().Single(t => t.Name == "XboxTriggerRumbleSwitch");
					Assert.Equal(page.XboxTriggerRumbleLabel, Avalonia.Automation.AutomationProperties.GetName(toggle));
					Rect toggleBox = FitCheck.Bounds(toggle, window);
					Assert.True(toggleBox.Left > Math.Max(tagBox.Right, lineBox.Right) && toggleBox.Right <= panelBox.Right, $"{code} {width}: the switch is not on the right");
					Assert.False(toggle.IsChecked);
					if (code == "tr")
					{
						Save(window, $"optin2-settings-off-{width}x{height}-tr.png");
					}

					// On: the setting is on and the tab still fits; off from elsewhere: the switch follows.
					toggle.IsChecked = true;
					Assert.True(fake.Games.XboxTriggerRumbleOn);
					FitCheck.Settle(window);
					Assert.Empty(SettingsFitTests.Check(window));
					if (code == "tr")
					{
						Save(window, $"optin2-settings-on-{width}x{height}-tr.png");
					}
					fake.Games.XboxTriggerRumbleOn = false;
					Dispatcher.UIThread.RunJobs();
					Assert.False(toggle.IsChecked);
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// ---- the Games page ----

	[AvaloniaFact]
	public async Task GamesPage_XboxView_SwitchRow_SameSetting_RowsNameWhatAutomaticPicks()
	{
		(MainWindowViewModel vm, FakeControllerService service, _) = await OpenAsync();
		SupportedGamesViewModel page = vm.SupportedGames;
		page.ViewIndex = 1;
		// Off: the games are still listed.
		Assert.Equal(["Hollow Peak", "Pixel Racer", "Thunder Kart Rally"], page.Games.Select(g => g.Name));
		WithLanguage(Language.Turkish, () =>
		{
			Assert.Equal("Xbox Tetik Titreşimi", page.XboxTriggerRumbleTitle);
			Assert.Equal("Deneysel", page.ExperimentalTag);
			Assert.Equal("Xbox tetik titreşimi olan oyunlarda titreşimi DualSense tetiklerine taşır.", page.XboxTriggerRumbleShort);
			Assert.All(page.Games, g => Assert.Equal("Otomatik (Xbox 360)", g.PadType!.Options[0].Name));
		});
		WithLanguage(Language.English, () =>
		{
			Assert.Equal("Xbox Trigger Rumble", page.XboxTriggerRumbleTitle);
			Assert.Equal("Experimental", page.ExperimentalTag);
			Assert.Equal("Plays Xbox trigger rumble on the DualSense triggers in games that use it.", page.XboxTriggerRumbleShort);
		});

		// On (the same setting as Settings): the rows say Xbox Series; off again: Xbox 360.
		vm.Settings.XboxTriggerRumbleOn = true;
		Dispatcher.UIThread.RunJobs();
		Assert.True(service.Games.XboxTriggerRumbleOn);
		WithLanguage(Language.Turkish, () => Assert.All(page.Games, g => Assert.Equal("Otomatik (Xbox Series)", g.PadType!.Options[0].Name)));
		vm.Settings.XboxTriggerRumbleOn = false;
		WithLanguage(Language.Turkish, () => Assert.All(page.Games, g => Assert.Equal("Otomatik (Xbox 360)", g.PadType!.Options[0].Name)));
	}

	[AvaloniaFact]
	public async Task GamesPage_XboxView_SwitchRow_Fits_AndFollowsSettings()
	{
		foreach (string code in new[] { "tr", "en" })
		{
			(MainWindowViewModel vm, FakeControllerService service, _) = await OpenAsync(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = code });
			Language? before = Localizer.Chosen;
			Localizer.Chosen = code == "tr" ? Language.Turkish : Language.English;
			try
			{
				foreach ((double w, double h) in Sizes)
				{
					var window = new MainWindow { DataContext = vm, Width = w, Height = h };
					window.Show();
					vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
					vm.SupportedGames.ViewIndex = 0;
					FitCheck.Settle(window);
					Grid row = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "GamesXboxTriggerRumbleRow");
					Assert.False(row.IsEffectivelyVisible);
					vm.SupportedGames.ViewIndex = 1;
					FitCheck.Settle(window);
					List<Control> all = window.GetVisualDescendants().OfType<Control>().ToList();
					Assert.DoesNotContain(all, c => c.Name is "XboxTriggerOffRow" or "XboxTriggerTurnOnButton");
					TextBlock title = all.OfType<TextBlock>().Single(t => t.Name == "GamesXboxTriggerRumbleTitle");
					TextBlock tag = all.OfType<TextBlock>().Single(t => t.Name == "GamesXboxTriggerRumbleTag");
					TextBlock line = all.OfType<TextBlock>().Single(t => t.Name == "GamesXboxTriggerRumbleLine");
					ToggleSwitch toggle = all.OfType<ToggleSwitch>().Single(t => t.Name == "GamesXboxTriggerRumbleSwitch");
					Border card = all.OfType<Border>().Single(b => b.Name == "ListCard");
					Assert.True(row.IsEffectivelyVisible);
					Assert.Contains("settingRow", row.Classes);
					Assert.Equal(Text.XboxTriggerRumbleTitle(), title.Text);
					Assert.Equal(Text.ExperimentalTag(), tag.Text);
					Assert.Contains("caption", tag.Classes);
					Assert.Equal(KeepLastWords.Apply(Text.XboxTriggerRumbleSettingShort()), line.Text);
					Assert.Equal(Text.XboxTriggerRumbleSetting(), Avalonia.Automation.AutomationProperties.GetName(toggle));
					Assert.Equal(Text.XboxTriggerRumbleSettingHint(), ToolTip.GetTip(row));
					Assert.All(new Control[] { title, tag, line, toggle }, c => Assert.True(c.IsEffectivelyVisible));
					Rect titleBox = FitCheck.Bounds(title, window), tagBox = FitCheck.Bounds(tag, window), lineBox = FitCheck.Bounds(line, window);
					Rect toggleBox = FitCheck.Bounds(toggle, window), cardBox = FitCheck.Bounds(card, window);
					Assert.True(tagBox.Left > titleBox.Right && Math.Abs(tagBox.Center.Y - titleBox.Center.Y) < 4, $"{code} {w}: the tag is not beside the title");
					Assert.True(lineBox.Top >= titleBox.Bottom, $"{code} {w}: the line is not under the title");
					Assert.True(toggleBox.Left > Math.Max(tagBox.Right, lineBox.Right) && toggleBox.Right <= cardBox.Right + 0.5, $"{code} {w}: the switch is not on the right");
					Assert.Empty(FitCheck.CutText(row));
					Assert.Empty(FitCheck.Inside(card, window));
					Assert.False(toggle.IsChecked);
					if ((code, w) == ("tr", 1152))
					{
						Save(window, "optin3-games-off-tr.png");
					}

					// The switch turns the setting on; the row stays.
					toggle.IsChecked = true;
					FitCheck.Settle(window);
					Assert.True(service.Games.XboxTriggerRumbleOn);
					Assert.True(vm.Settings.XboxTriggerRumbleOn);
					Assert.True(row.IsEffectivelyVisible);
					Assert.Empty(FitCheck.Inside(card, window));
					if ((code, w) == ("tr", 1152))
					{
						Save(window, "optin3-games-on-tr.png");
					}

					// The Settings card shows it on; off there, the Games switch follows.
					vm.OpenSettingsAt(SettingsViewModel.ControllersTab);
					FitCheck.Settle(window);
					ToggleSwitch settingsToggle = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "XboxTriggerRumbleSwitch");
					Assert.True(settingsToggle.IsChecked);
					settingsToggle.IsChecked = false;
					Dispatcher.UIThread.RunJobs();
					Assert.False(service.Games.XboxTriggerRumbleOn);
					Assert.False(toggle.IsChecked);
					window.Close();
				}
			}
			finally
			{
				Localizer.Chosen = before;
			}
		}
	}

	// ---- the Trigger Rumble rows ----

	[AvaloniaFact]
	public async Task TriggerRumbleRows_ShowOnlyWhileTheSettingIsOn()
	{
		(MainWindowViewModel vm, _, _) = await OpenAsync();
		Language? before = Localizer.Chosen;
		Localizer.Chosen = Language.Turkish;
		try
		{
			Assert.False(vm.OtherGames.Feel.ShowsTriggerRumble);
			var window = new MainWindow { DataContext = vm, Width = 1152, Height = 700 };
			window.Show();
			vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.OtherGames);
			FitCheck.Settle(window);
			Grid row = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TriggerRumbleRow");
			Border card = row.GetVisualAncestors().OfType<Border>().First(b => b.Name == "ClassicRumbleCard");
			Assert.False(row.IsEffectivelyVisible);
			Assert.Empty(FitCheck.Inside(card, window));
			Save(window, "optin2-haptics-off-1152x700-tr.png");
			vm.Settings.XboxTriggerRumbleOn = true;
			FitCheck.Settle(window);
			Assert.True(vm.OtherGames.Feel.ShowsTriggerRumble);
			Assert.True(row.IsEffectivelyVisible);
			Assert.Empty(FitCheck.Inside(card, window));
			Save(window, "optin2-haptics-on-1152x700-tr.png");

			// A game's pop-up: the same.
			vm.Settings.XboxTriggerRumbleOn = false;
			vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
			vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == "Glass Comet"));
			vm.SupportedGames.Panel.UseOwnSettingsCommand.Execute(null);
			vm.SupportedGames.Panel.SaveProfileNameCommand.Execute(null);
			FitCheck.Settle(window);
			Grid panelRow = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "PanelTriggerRumbleRow");
			Assert.False(panelRow.IsEffectivelyVisible);
			vm.Settings.XboxTriggerRumbleOn = true;
			FitCheck.Settle(window);
			Assert.True(panelRow.IsEffectivelyVisible);
			window.Close();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}
}
