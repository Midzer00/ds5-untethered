// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Virtual;

namespace Kopru.App.Tests;

// The Xbox Series controller type in the app: a choice marked experimental, its fallback toast and memory, the Trigger
// Rumble setting (the Xbox And DS4 Rumble card and a game's pop-up), and the Games page's "Xbox Tetik Titreşimi" view, all
// with the experimental Xbox Trigger Rumble setting on (XboxTriggerOptInAppTests covers it off). With
// KOPRU_XBOX_PREVIEW_DIR set, renders games-xbox-tr.png and rumble-card-tr.png there (1152x700, made-up games).
public sealed class XboxSeriesAppTests
{
	// Made-up games: Pixel Racer (found by Steam, 9009) and Hollow Peak (Epic) are in the fake scan; Thunder Kart Rally is
	// not installed; Storm Circuit's store page lists DualSense support; Neon Drift is a listed DualSense game.
	private static readonly XboxTriggerGames Fixture = XboxTriggerGames.Parse("""
		{ "games": [
		  { "name": "Pixel Racer", "steamAppId": 9009, "exe": [] },
		  { "name": "Hollow Peak", "steamAppId": null, "exe": [] },
		  { "name": "Thunder Kart Rally", "steamAppId": 777001, "exe": [] },
		  { "name": "Storm Circuit", "steamAppId": 777002, "exe": [] },
		  { "name": "Neon Drift", "steamAppId": null, "exe": ["neondrift.exe"] }
		] }
		""");

	private static async Task<(MainWindowViewModel Vm, FakeControllerService Service, MemorySettingsStore Store)> OpenAsync(AppSettings? settings = null)
	{
		var service = new FakeControllerService(animate: false);
		service.Games.SetCatalog(new GameCatalog([new CatalogGame("Neon Drift", ["NeonDrift.exe"], null, true, true, "high"),
			new CatalogGame("Glass Comet", ["GlassComet.exe"], null, true, false, "high")]));
		service.Watcher.Exit("Cyberpunk2077.exe");
		service.Watcher.Exe = null;
		service.Games.Tick();
		service.Games.PlayStation = new PlayStationSupport([new(777002, PlayStationPad.DualSense)]);
		service.Games.TriggerGames = Fixture;
		service.Games.XboxSeriesOsSupported = true;
		var store = new MemorySettingsStore(settings ?? new AppSettings { GameListCheckedAt = DateTimeOffset.Now });
		var vm = new MainWindowViewModel(service, new AppEnvironment(store, InstalledGames: FakeFoundGames.Installed(FakeFoundGames.Folders())));
		await vm.SupportedGames.ScanTask;
		vm.Settings.XboxTriggerRumbleOn = true;
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

	// ---- the controller type ----

	// ---- Trigger Rumble ----

	[AvaloniaFact]
	public async Task TriggerRumble_IsAProfileSetting_SavedAndSentToTheBridge()
	{
		(MainWindowViewModel vm, _, MemorySettingsStore store) = await OpenAsync();
		ProfileFeelViewModel feel = vm.OtherGames.Feel;
		Assert.True(feel.IsTriggerRumbleXboxLike);
		Assert.Equal(1, feel.TriggerRumbleIndex);
		WithLanguage(Language.Turkish, () =>
		{
			Assert.Equal(["Kapalı", "Xbox Gibi", "Yumuşak", "Keskin"], feel.TriggerRumbleChoices.Select(c => c.Name));
			Assert.Equal("Tetik Titreşimi", Text.TriggerRumbleLabel());
			Assert.Contains("Xbox Series", Text.TriggerRumbleHint());
		});
		WithLanguage(Language.English, () =>
		{
			Assert.Equal(["Off", "Xbox-Like", "Soft", "Sharp"], feel.TriggerRumbleChoices.Select(c => c.Name));
			Assert.Equal("Trigger Rumble", Text.TriggerRumbleLabel());
			Assert.Contains("Xbox Series", Text.TriggerRumbleHint());
		});

		feel.TriggerRumbleIndex = 3;
		Assert.Equal(TriggerRumbleFeel.Sharp, vm.Settings.TriggerRumble);
		Assert.Equal(TriggerRumbleFeel.Sharp, vm.Settings.Profiles.Active.Values.TriggerRumble);
		Assert.Equal(TriggerRumbleFeel.Sharp, GameProfiles.For(vm.Settings.ToSettings().Game).TriggerRumble);
		feel.IsTriggerRumbleOff = true;
		Assert.Equal(TriggerRumbleFeel.Off, vm.Settings.TriggerRumble);
		vm.Settings.SaveNow();
		Assert.Equal(TriggerRumbleFeel.Off, store.Saved!.TriggerRumble);

		// A game's own profile keeps its own choice; the active profile is untouched.
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == "Glass Comet"));
		panel.UseOwnSettingsCommand.Execute(null);
		panel.SaveProfileNameCommand.Execute(null);
		panel.Feel.TriggerRumbleIndex = 2;
		Assert.Equal(TriggerRumbleFeel.Soft, vm.Settings.Profiles.Profiles.Single(p => p.Name == "Glass Comet").Values.TriggerRumble);
		Assert.Equal(TriggerRumbleFeel.Off, vm.Settings.TriggerRumble);
	}

	[AvaloniaFact]
	public async Task TriggerRumbleRow_InTheCardAndThePopUp_WithItsHint()
	{
		(MainWindowViewModel vm, _, _) = await OpenAsync();
		Language? before = Localizer.Chosen;
		Localizer.Chosen = Language.Turkish;
		try
		{
			var window = new MainWindow { DataContext = vm, Width = 1152, Height = 700 };
			window.Show();
			vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.OtherGames);
			FitCheck.Settle(window);
			Grid row = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TriggerRumbleRow");
			Border card = row.GetVisualAncestors().OfType<Border>().First(b => b.Name == "ClassicRumbleCard");
			Assert.NotNull(card);
			Assert.Equal(["Kapalı", "Xbox Gibi", "Yumuşak", "Keskin"], row.GetVisualDescendants().OfType<ToggleButton>().Select(t => t.Content as string));
			Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Text.TriggerRumbleHint() && t.IsEffectivelyVisible);
			Assert.Empty(FitCheck.Inside(card, window));
			if (Environment.GetEnvironmentVariable("KOPRU_XBOX_PREVIEW_DIR") is { Length: > 0 } dir)
			{
				Directory.CreateDirectory(dir);
				using var frame = window.CaptureRenderedFrame();
				frame!.Save(Path.Combine(dir, "rumble-card-tr.png"));
			}

			vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
			vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == "Glass Comet"));
			vm.SupportedGames.Panel.UseOwnSettingsCommand.Execute(null);
			vm.SupportedGames.Panel.SaveProfileNameCommand.Execute(null);
			FitCheck.Settle(window);
			ComboBox picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "PanelTriggerRumble");
			Assert.True(picker.IsEffectivelyVisible);
			Assert.Equal(1, picker.SelectedIndex);
			Grid panelRow = picker.GetVisualAncestors().OfType<Grid>().First(g => g.Name == "PanelTriggerRumbleRow");
			Assert.Equal(Text.TriggerRumbleHint(), ToolTip.GetTip(panelRow));
			window.Close();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// ---- the Games page ----

	[AvaloniaFact]
	public async Task GamesPage_XboxTriggerView_ListsTheListAndLearnedGames_OnThisPcFirst()
	{
		(MainWindowViewModel vm, FakeControllerService service, _) = await OpenAsync();
		SupportedGamesViewModel page = vm.SupportedGames;
		WithLanguage(Language.Turkish, () =>
			Assert.Equal(["Desteklenen", "Xbox Tetik Titreşimi", Text.UnsupportedViewCount(page.UnsupportedGames.Count)], page.ViewFilters.Select(f => f.Name)));
		WithLanguage(Language.English, () => Assert.Equal("Xbox Trigger Rumble", page.ViewFilters[1].Name));

		page.ViewIndex = 1;
		Assert.True(page.ShowXboxTriggers);
		Assert.False(page.ShowUnsupported);
		Assert.Equal(Text.XboxTriggerView(), page.ListLabel);
		// On this PC first (found by Steam and Epic), then the list's other games; a listed DualSense game and a game whose
		// store page lists DualSense support are not here (DualSense wins).
		Assert.Equal(["Hollow Peak", "Pixel Racer", "Thunder Kart Rally"], page.Games.Select(g => g.Name));
		Assert.All(page.Games, g => Assert.True(g.IsXboxTrigger));
		Assert.Equal(Text.XboxTriggerGamesHint(), page.AddHintLine);
		Assert.True(page.ShowsAddHint);
		// Every row says what Automatic picks.
		WithLanguage(Language.Turkish, () => Assert.All(page.Games, g => Assert.Equal("Otomatik (Xbox Series)", g.PadType!.Options[0].Name)));
		// Installed only: the games on this PC.
		page.InstalledFilterIndex = 1;
		Assert.Equal(["Hollow Peak", "Pixel Racer"], page.Games.Select(g => g.Name));
		Assert.Equal(2, page.InstalledCount);
		page.InstalledFilterIndex = 0;

		// They are not "unsupported"; the listed DualSense game stays in Supported.
		page.ViewIndex = 2;
		Assert.True(page.ShowUnsupported);
		Assert.DoesNotContain(page.Games, g => g.Name is "Pixel Racer" or "Hollow Peak");
		Assert.Contains(page.Games, g => g.Name.StartsWith("Moss Garden", StringComparison.Ordinal));
		page.ViewIndex = 0;
		Assert.Contains(page.Games, g => g.Name == "Neon Drift");
		Assert.DoesNotContain(page.Games, g => g.IsXboxTrigger);

		// A game learned to send trigger rumble moves to the Xbox view.
		service.Games.LoadMemory(service.Games.SnapshotMemory() with { XboxTriggerGames = ["steam:9020"] });
		Dispatcher.UIThread.RunJobs();
		page.ViewIndex = 1;
		Assert.Contains(page.Games, g => g.Key == "steam:9020");
		Assert.Equal(3, page.Games.Count(g => g.IsOnThisPc));
	}

	[AvaloniaFact]
	public async Task GamesPage_ThreeWaySwitch_FitsTheHeaderLine()
	{
		foreach (string code in new[] { "tr", "en" })
		{
			(MainWindowViewModel vm, _, _) = await OpenAsync(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = code });
			Language? before = Localizer.Chosen;
			Localizer.Chosen = code == "tr" ? Language.Turkish : Language.English;
			try
			{
				foreach ((double w, double h) in new[] { (1100.0, 700.0), (1152.0, 700.0) })
				{
					var window = new MainWindow { DataContext = vm, Width = w, Height = h };
					window.Show();
					vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
					vm.SupportedGames.ViewIndex = 1;
					FitCheck.Settle(window);
					ListBox choice = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "GameViewChoice");
					Button add = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AddGameButton");
					Border card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ListCard");
					Assert.Equal(3, choice.GetVisualDescendants().OfType<ListBoxItem>().Count());
					Assert.True(FitCheck.Bounds(choice, window).Right + 8 <= FitCheck.Bounds(add, window).Left + 0.5, $"{code} {w}: the switch runs into Add Game");
					Assert.Empty(FitCheck.CutText(choice));
					Assert.Empty(FitCheck.Inside(card, window));
					if ((code, w) == ("tr", 1152) && Environment.GetEnvironmentVariable("KOPRU_XBOX_PREVIEW_DIR") is { Length: > 0 } dir)
					{
						Directory.CreateDirectory(dir);
						using var frame = window.CaptureRenderedFrame();
						frame!.Save(Path.Combine(dir, "games-xbox-tr.png"));
					}
					window.Close();
				}
			}
			finally
			{
				Localizer.Chosen = before;
			}
		}
	}

	// ---- profiles from a game's pop-up ----

	[AvaloniaFact]
	public async Task PopUp_NewProfile_AsksForTheName_GameNameThenNumbered()
	{
		(MainWindowViewModel vm, _, _) = await OpenAsync();
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		GameItemViewModel comet = vm.SupportedGames.Games.Single(g => g.Name == "Glass Comet");
		vm.SupportedGames.OpenGameCommand.Execute(comet);

		panel.UseOwnSettingsCommand.Execute(null);
		Assert.True(panel.IsNaming);
		Assert.Equal("Glass Comet", panel.ProfileName);
		Assert.Equal(Text.ProfileNew(), panel.NamingLabel);
		// Esc: nothing made.
		panel.CancelProfileNameCommand.Execute(null);
		Assert.False(panel.IsNaming);
		Assert.Single(vm.Settings.Profiles.Profiles);

		// A name of the user's own.
		panel.UseOwnSettingsCommand.Execute(null);
		panel.ProfileName = "Comet Night";
		panel.SaveProfileNameCommand.Execute(null);
		Assert.False(panel.IsNaming);
		Assert.Equal("Comet Night", vm.Settings.Profiles.Find(vm.Settings.Profiles.ProfileForGame(comet.Key)!)!.Name);

		// The game's name is free: the next one gets it, then "<Game> 2".
		panel.NewProfileCommand.Execute(null);
		Assert.Equal("Glass Comet", panel.ProfileName);
		panel.SaveProfileNameCommand.Execute(null);
		panel.NewProfileCommand.Execute(null);
		Assert.Equal("Glass Comet 2", panel.ProfileName);
		// A blank name keeps the field open.
		panel.ProfileName = "  ";
		panel.SaveProfileNameCommand.Execute(null);
		Assert.True(panel.IsNaming);
		panel.ProfileName = "Glass Comet 2";
		panel.SaveProfileNameCommand.Execute(null);
		Assert.Equal(["Comet Night", "Glass Comet", "Glass Comet 2"], vm.Settings.Profiles.Profiles.Where(p => !p.IsDefault).Select(p => p.Name));
		Assert.Equal("Glass Comet 2", vm.Settings.Profiles.Find(vm.Settings.Profiles.ProfileForGame(comet.Key)!)!.Name);
	}

	[AvaloniaFact]
	public async Task PopUp_RenameAndDuplicate_TheGamesProfile()
	{
		(MainWindowViewModel vm, _, _) = await OpenAsync();
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		GameItemViewModel comet = vm.SupportedGames.Games.Single(g => g.Name == "Glass Comet");
		vm.SupportedGames.OpenGameCommand.Execute(comet);
		// The default profile can't be renamed.
		Assert.False(panel.RenameProfileCommand.CanExecute(null));
		panel.UseOwnSettingsCommand.Execute(null);
		panel.SaveProfileNameCommand.Execute(null);
		Assert.True(panel.RenameProfileCommand.CanExecute(null));
		string first = vm.Settings.Profiles.ProfileForGame(comet.Key)!;
		panel.Feel.RumbleLevel = 160;

		panel.RenameProfileCommand.Execute(null);
		Assert.Equal(Text.ProfileRename(), panel.NamingLabel);
		Assert.Equal("Glass Comet", panel.ProfileName);
		panel.ProfileName = "Comet Calm";
		panel.SaveProfileNameCommand.Execute(null);
		Assert.Equal("Comet Calm", vm.Settings.Profiles.Find(first)!.Name);
		Assert.Equal(first, vm.Settings.Profiles.ProfileForGame(comet.Key));

		panel.DuplicateProfileCommand.Execute(null);
		Assert.Equal(Text.ProfileDuplicate(), panel.NamingLabel);
		Assert.Equal("Glass Comet", panel.ProfileName);
		panel.SaveProfileNameCommand.Execute(null);
		string copy = vm.Settings.Profiles.ProfileForGame(comet.Key)!;
		Assert.NotEqual(first, copy);
		Assert.Equal(160, vm.Settings.Profiles.Find(copy)!.Values.RumbleLevel);
		// Both point at the game: the picker offers both.
		Assert.Contains(comet.Profile!.Options, o => o.Id == first);
		Assert.Contains(comet.Profile!.Options, o => o.Id == copy);
	}

	[AvaloniaFact]
	public void PopUp_SeveralProfilesForTheRunningGame_ThePickerSwitchesLive()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		fake.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		GameItemViewModel running = vm.SupportedGames.Games.Single(g => g.Key == fake.Games.ActiveGame!.Key);
		vm.SupportedGames.OpenGameCommand.Execute(running);
		panel.UseOwnSettingsCommand.Execute(null);
		panel.ProfileName = "Calm";
		panel.SaveProfileNameCommand.Execute(null);
		panel.Feel.RumbleLevel = 40;
		panel.NewProfileCommand.Execute(null);
		panel.ProfileName = "Wild";
		panel.SaveProfileNameCommand.Execute(null);
		Assert.Equal("Wild", vm.Settings.ActiveProfileName);
		panel.Feel.RumbleLevel = 190;
		Assert.Equal(190, vm.Settings.RumbleLevel);

		// Back to "Calm" in the picker while the game runs: active at once, with its own values.
		running.Profile!.Selected = running.Profile.Options.Single(o => o.Name == "Calm");
		Dispatcher.UIThread.RunJobs();
		Assert.Equal("Calm", vm.Settings.ActiveProfileName);
		Assert.Equal(40, vm.Settings.RumbleLevel);
		running.Profile!.Selected = running.Profile.Options.Single(o => o.Name == "Wild");
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(190, vm.Settings.RumbleLevel);
	}

	[AvaloniaFact]
	public async Task PopUp_NameField_GetsTheKeyboard_EnterSaves_EscCancelsOnlyTheName()
	{
		(MainWindowViewModel vm, _, _) = await OpenAsync();
		var window = new MainWindow { DataContext = vm, Width = 1152, Height = 700 };
		window.Show();
		vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == "Glass Comet"));
		FitCheck.Settle(window);
		Assert.False(window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ProfileNameBox").IsEffectivelyVisible);

		panel.UseOwnSettingsCommand.Execute(null);
		FitCheck.Settle(window);
		TextBox box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ProfileNameBox");
		Assert.True(box.IsEffectivelyVisible);
		Assert.Same(box, window.FocusManager?.GetFocusedElement());
		Assert.Equal((0, "Glass Comet".Length), (Math.Min(box.SelectionStart, box.SelectionEnd), Math.Max(box.SelectionStart, box.SelectionEnd)));

		// Esc closes the field, not the pop-up.
		window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		FitCheck.Settle(window);
		Assert.False(panel.IsNaming);
		Assert.True(panel.IsOpen);

		panel.UseOwnSettingsCommand.Execute(null);
		FitCheck.Settle(window);
		window.KeyTextInput("Comet Run");
		FitCheck.Settle(window);
		window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		FitCheck.Settle(window);
		Assert.False(panel.IsNaming);
		Assert.Equal("Comet Run", vm.Settings.Profiles.Profiles.Last().Name);
		Assert.Empty(FitCheck.CutText(window.GetVisualDescendants().OfType<GamePanelView>().Single()));
		window.Close();
	}
}
