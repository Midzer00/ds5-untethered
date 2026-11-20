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
