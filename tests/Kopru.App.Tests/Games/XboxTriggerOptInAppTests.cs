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
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.Tests;

// The experimental Xbox Trigger Rumble setting: off by default for new and existing users; off, Automatic never gives Xbox
// Series (an Xbox Series choice still holds, without trigger rumble). Haptics And Triggers sets it with the profile's feel
// in one row (Off | Xbox-Like | Soft | Sharp), the Games page's Xbox view has a switch for it, and a game's pop-up hides its
// Trigger Rumble row while it is off. With KOPRU_OPTIN_PREVIEW_DIR set, renders optin2-settings-{off|on}-{w}x{h}-tr.png,
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

	// Haptics And Triggers, Xbox And DS4 Rumble tab: one Xbox Trigger Rumble row (title, muted Experimental tag, short
	// description, the setting's hint as its tooltip) with Off | Xbox-Like | Soft | Sharp on the right; the old switch row and
	// the separate Trigger Rumble row are gone, and Settings has neither.
	[AvaloniaFact]
	public void HapticsPage_XboxTriggerRow_WithTitleTagLineAndChoice_AndNoSwitchRow()
	{
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				(MainWindowViewModel vm, FakeControllerService fake) = SettingsFitTests.FullCase(code);
				OtherGamesViewModel page = vm.OtherGames;
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
				string[] names = code == "tr" ? ["Kapalı", "Xbox Gibi", "Yumuşak", "Keskin"] : ["Off", "Xbox-Like", "Soft", "Sharp"];
				foreach ((double width, double height) in FitCheck.ReviewSizes)
				{
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.OpenSettingsAt(SettingsViewModel.ControllersTab);
					FitCheck.Settle(window);
					Assert.Empty(SettingsFitTests.Check(window));
					SettingsView settingsView = window.GetVisualDescendants().OfType<SettingsView>().Single();
					List<Control> settingsControls = settingsView.GetVisualDescendants().OfType<Control>().ToList();
					Assert.DoesNotContain(settingsControls, c => c.Name is "ExperimentalPanel" or "XboxTriggerRumblePanel" or "XboxTriggerRumbleRow" or "XboxTriggerRumbleSwitch"
						or "XboxTriggerRow" or "HapticsPanel" or "PauseHapticsRow");

					vm.Navigate(vm.OtherGames);
					page.SelectedTab = OtherGamesViewModel.RumbleTab;
					FitCheck.Settle(window);
					OtherGamesView view = window.GetVisualDescendants().OfType<OtherGamesView>().Single();
					List<Control> all = view.GetVisualDescendants().OfType<Control>().ToList();
					Assert.DoesNotContain(all, c => c.Name is "XboxTriggerRumbleRow" or "XboxTriggerRumbleSwitch" or "TriggerRumbleRow");
					Assert.Empty(all.OfType<ToggleSwitch>().Where(t => t.TemplatedParent == null && t.IsEffectivelyVisible));
					Border card = all.OfType<Border>().Single(b => b.Name == "ClassicRumbleCard");
					Grid row = all.OfType<Grid>().Single(g => g.Name == "XboxTriggerRow");
					Assert.Same(card, row.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("panel")));
					Assert.Contains("settingRow", row.Classes);
					Assert.Equal(page.XboxTriggerRumbleHint, ToolTip.GetTip(row));
					TextBlock title = all.OfType<TextBlock>().Single(t => t.Name == "XboxTriggerRumbleTitle");
					TextBlock tag = all.OfType<TextBlock>().Single(t => t.Name == "XboxTriggerRumbleTag");
					TextBlock line = all.OfType<TextBlock>().Single(t => t.Name == "XboxTriggerRumbleLine");
					Assert.Equal(page.XboxTriggerRumbleTitle, title.Text);
					Assert.Equal(page.ExperimentalTag, tag.Text);
					Assert.Contains("caption", tag.Classes);
					Assert.Equal(KeepLastWords.Apply(page.XboxTriggerRumbleShort), line.Text);
					Assert.All(new[] { title, tag, line }, t => Assert.True(t.IsEffectivelyVisible));
					Rect cardBox = FitCheck.Bounds(card, window);
					Rect titleBox = FitCheck.Bounds(title, window), tagBox = FitCheck.Bounds(tag, window), lineBox = FitCheck.Bounds(line, window);
					Assert.True(tagBox.Left > titleBox.Right && Math.Abs(tagBox.Center.Y - titleBox.Center.Y) < 4, $"{code} {width}: the tag is not beside the title");
					Assert.True(lineBox.Top >= titleBox.Bottom, $"{code} {width}: the line is not under the title");

					ListBox choice = all.OfType<ListBox>().Single(l => l.Name == "XboxTriggerChoice");
					Assert.Equal(names, choice.ItemsSource!.Cast<LabelledChoice>().Select(c => c.Name));
					Assert.Equal(page.XboxTriggerRumbleLabel, Avalonia.Automation.AutomationProperties.GetName(choice));
					Rect choiceBox = FitCheck.Bounds(choice, window);
					Assert.True(choiceBox.Left > Math.Max(tagBox.Right, lineBox.Right) && choiceBox.Right <= cardBox.Right, $"{code} {width}: the choice is not on the right");
					// Every name whole (none cut to an ellipsis).
					Assert.All(choice.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)),
						t => Assert.False(t.TextLayout.TextLines.Any(l => l.HasCollapsed), $"{code} {width}: \"{t.Text}\" is cut"));
					Assert.Empty(FitCheck.CutText(row));
					// Off by default: Off is selected.
					Assert.Equal(0, choice.SelectedIndex);

					// Soft: the setting is on and the profile's feel is Soft; off from elsewhere: Off is selected again.
					choice.SelectedIndex = 2;
					FitCheck.Settle(window);
					Assert.True(fake.Games.XboxTriggerRumbleOn);
					Assert.Equal(TriggerRumbleFeel.Soft, vm.Settings.TriggerRumble);
					fake.Games.XboxTriggerRumbleOn = false;
					Dispatcher.UIThread.RunJobs();
					Assert.Equal(0, choice.SelectedIndex);
					Assert.Equal(TriggerRumbleFeel.Soft, vm.Settings.TriggerRumble);
					vm.Settings.TriggerRumble = TriggerRumbleFeel.XboxLike;
					page.SelectedTab = OtherGamesViewModel.GameEffectsTab;
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The merged row's choice: Off turns the setting off and keeps the profile's feel; any other turns it on and picks that feel
	// for the active profile (another profile keeps its own).
	[AvaloniaFact]
	public async Task XboxTriggerIndex_SetsTheSettingAndTheProfilesFeel()
	{
		(MainWindowViewModel vm, FakeControllerService service, _) = await OpenAsync();
		ProfileFeelViewModel feel = vm.OtherGames.Feel;
		List<string> changed = [];
		feel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
		Assert.False(vm.Settings.XboxTriggerRumbleOn);
		Assert.Equal(TriggerRumbleFeel.XboxLike, vm.Settings.TriggerRumble);
		Assert.Equal(0, feel.XboxTriggerIndex);
		WithLanguage(Language.English, () => Assert.Equal(["Off", "Xbox-Like", "Soft", "Sharp"], feel.TriggerRumbleChoices.Select(c => c.Name)));
		WithLanguage(Language.Turkish, () => Assert.Equal(["Kapalı", "Xbox Gibi", "Yumuşak", "Keskin"], feel.TriggerRumbleChoices.Select(c => c.Name)));

		feel.XboxTriggerIndex = 3;
		Assert.True(vm.Settings.XboxTriggerRumbleOn);
		Assert.True(service.Games.XboxTriggerRumbleOn);
		Assert.Equal(TriggerRumbleFeel.Sharp, vm.Settings.TriggerRumble);
		Assert.Equal(TriggerRumbleFeel.Sharp, vm.Settings.Profiles.Find(vm.Settings.Profiles.ActiveId)!.Values.TriggerRumble);
		Assert.Equal(3, feel.XboxTriggerIndex);

		changed.Clear();
		feel.XboxTriggerIndex = 0;
		Assert.False(vm.Settings.XboxTriggerRumbleOn);
		Assert.Equal(TriggerRumbleFeel.Sharp, vm.Settings.TriggerRumble);
		Assert.Equal(0, feel.XboxTriggerIndex);
		Assert.Contains(nameof(ProfileFeelViewModel.XboxTriggerIndex), changed);

		// Out of range: nothing changes.
		feel.XboxTriggerIndex = -1;
		feel.XboxTriggerIndex = 4;
		Assert.False(vm.Settings.XboxTriggerRumbleOn);
		Assert.Equal(TriggerRumbleFeel.Sharp, vm.Settings.TriggerRumble);

		// On from elsewhere (the Games page's switch): the row shows the profile's feel and says so.
		changed.Clear();
		vm.Settings.XboxTriggerRumbleOn = true;
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(3, feel.XboxTriggerIndex);
		Assert.Contains(nameof(ProfileFeelViewModel.XboxTriggerIndex), changed);

		// On with the profile's feel Off: Off shows (nothing plays).
		vm.Settings.TriggerRumble = TriggerRumbleFeel.Off;
		Assert.Equal(0, feel.XboxTriggerIndex);
		feel.XboxTriggerIndex = 1;
		Assert.True(vm.Settings.XboxTriggerRumbleOn);
		Assert.Equal(TriggerRumbleFeel.XboxLike, vm.Settings.TriggerRumble);

		// Another profile's feel stays its own: a game's profile edited in its pop-up keeps Soft while the active one changes.
		vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == "Glass Comet"));
		vm.SupportedGames.Panel.UseOwnSettingsCommand.Execute(null);
		vm.SupportedGames.Panel.SaveProfileNameCommand.Execute(null);
		vm.SupportedGames.Panel.Feel.TriggerRumbleIndex = 2;
		feel.XboxTriggerIndex = 3;
		Assert.Equal(TriggerRumbleFeel.Soft, vm.Settings.Profiles.Profiles.Single(p => p.Name == "Glass Comet").Values.TriggerRumble);
		Assert.Equal(TriggerRumbleFeel.Sharp, vm.Settings.TriggerRumble);
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

					// Haptics & Triggers shows it on (the profile's feel, Xbox-Like by default); Off there, the Games switch follows.
					vm.Navigate(vm.OtherGames);
					vm.OtherGames.SelectedTab = OtherGamesViewModel.RumbleTab;
					FitCheck.Settle(window);
					ListBox hapticsChoice = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "XboxTriggerChoice");
					Assert.Equal(1, hapticsChoice.SelectedIndex);
					hapticsChoice.SelectedIndex = 0;
					Dispatcher.UIThread.RunJobs();
					Assert.False(service.Games.XboxTriggerRumbleOn);
					Assert.False(toggle.IsChecked);
					// And back on from there (Sharp): the Games switch is on again.
					hapticsChoice.SelectedIndex = 3;
					Dispatcher.UIThread.RunJobs();
					Assert.True(service.Games.XboxTriggerRumbleOn);
					Assert.True(toggle.IsChecked);
					vm.OtherGames.Feel.XboxTriggerIndex = 0;
					vm.Settings.TriggerRumble = TriggerRumbleFeel.XboxLike;
					vm.OtherGames.SelectedTab = OtherGamesViewModel.GameEffectsTab;
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

	// Haptics And Triggers keeps its Xbox Trigger Rumble row whether the setting is on or off (Off is one of its choices); a
	// game's pop-up shows its Trigger Rumble row only while the setting is on.
	[AvaloniaFact]
	public async Task TriggerRumbleRows_HapticsRowAlways_PopUpRowOnlyWhileTheSettingIsOn()
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
			vm.OtherGames.SelectedTab = OtherGamesViewModel.RumbleTab;
			FitCheck.Settle(window);
			Grid row = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "XboxTriggerRow");
			Border card = row.GetVisualAncestors().OfType<Border>().First(b => b.Name == "ClassicRumbleCard");
			Assert.True(row.IsEffectivelyVisible);
			Assert.Empty(FitCheck.Inside(card, window));
			Save(window, "optin2-haptics-off-1152x700-tr.png");
			vm.Settings.XboxTriggerRumbleOn = true;
			FitCheck.Settle(window);
			Assert.True(vm.OtherGames.Feel.ShowsTriggerRumble);
			Assert.True(row.IsEffectivelyVisible);
			Assert.Empty(FitCheck.Inside(card, window));
			Save(window, "optin2-haptics-on-1152x700-tr.png");

			// A game's pop-up: only while on.
			vm.Settings.XboxTriggerRumbleOn = false;
			vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
			vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == "Glass Comet"));
			vm.SupportedGames.Panel.UseOwnSettingsCommand.Execute(null);
			vm.SupportedGames.Panel.SaveProfileNameCommand.Execute(null);
			vm.SupportedGames.Panel.SelectedSegment = GamePanelViewModel.TriggersSegment;
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
