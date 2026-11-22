// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
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
using Kopru.Core.Profiles;

namespace Kopru.App.Tests;

// A game's settings pop-up on the Games page: a click on the row opens it (its pickers and buttons don't), Esc, Close and a
// click on the backdrop close it;
// "Settings For This Game" gives the game a profile of its own whose settings are edited there (only that profile, live when
// it is the active one); "Back To Default" puts the game back. Fits every window size in both languages. Writes
// game-panel-{supported|unsupported}-{w}x{h}-tr.png to KOPRU_PAGE_RENDER_DIR and game-panel-fit-*.png to
// KOPRU_FIT_RENDER_DIR when they are set.
public sealed class GamePanelTests
{
	[AvaloniaFact]
	public async Task ClickingARow_OpensThePanel_ItsPickersDoNot_EscCloses()
	{
		MainWindowViewModel vm = GameListScrollTests.Open(installedOnly: false, withRunningGame: true);
		await vm.SupportedGames.ScanTask;
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
		FitCheck.Settle(window);

		// A picker in a row: no panel.
		ComboBox picker = window.GetVisualDescendants().OfType<ComboBox>().First(b => b.Classes.Contains("rowPicker") && b.DataContext is GameItemViewModel);
		Click(window, picker);
		Assert.False(vm.SupportedGames.Panel.IsOpen);
		picker.IsDropDownOpen = false;
		FitCheck.Settle(window);

		// The row's name: the panel opens on that game.
		TextBlock name = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.DataContext is GameItemViewModel g && t.Text == g.Name && t.IsEffectivelyVisible);
		Click(window, name);
		Assert.Same(name.DataContext, vm.SupportedGames.Panel.Game);
		Border sheet = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "GameDialog");
		Assert.True(sheet.IsEffectivelyVisible);
		// The keyboard is in the dialog.
		Assert.Contains(sheet, (window.FocusManager?.GetFocusedElement() as Visual)?.GetVisualAncestors() ?? []);

		window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		FitCheck.Settle(window);
		Assert.False(vm.SupportedGames.Panel.IsOpen);
		Assert.False(sheet.IsEffectivelyVisible);
		// ... and back on the game's row.
		Border row = Assert.IsType<Border>(window.FocusManager?.GetFocusedElement());
		Assert.Same(name.DataContext, row.DataContext);

		// Enter on the focused row opens it again; a click on the backdrop (outside the dialog) closes it.
		window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		FitCheck.Settle(window);
		Assert.Same(name.DataContext, vm.SupportedGames.Panel.Game);
		Rect dialog = FitCheck.Bounds(sheet, window);
		Rect page = FitCheck.Bounds(window.GetVisualDescendants().OfType<SupportedGamesView>().Single(), window);
		var outside = new Point((page.Left + dialog.Left) / 2, dialog.Top + 10);
		window.MouseDown(outside, MouseButton.Left);
		window.MouseUp(outside, MouseButton.Left);
		FitCheck.Settle(window);
		Assert.False(vm.SupportedGames.Panel.IsOpen);
		window.Close();
	}

	// "Settings For This Game": a profile named after the game (made once), assigned to it; its settings change only that
	// profile; "Back To Default" keeps it for later.
	[AvaloniaFact]
	public void OwnSettings_MakeAProfile_EditOnlyIt_AndGoBack()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		GameItemViewModel returnal = vm.SupportedGames.Games.Single(g => g.Name == "Returnal");
		vm.SupportedGames.OpenGameCommand.Execute(returnal);
		Assert.True(panel.UsesDefault);
		Assert.False(panel.HasOwnProfile);
		Assert.Equal(Text.GameListed(), panel.SourceText);
		double sharedRumble = vm.Settings.RumbleLevel;

		panel.UseOwnSettingsCommand.Execute(null); panel.SaveProfileNameCommand.Execute(null);
		Assert.True(panel.HasOwnProfile);
		ControllerProfile own = vm.Settings.Profiles.Profiles.Single(p => p.Name == "Returnal");
		Assert.Equal(own.Id, vm.Settings.Profiles.ProfileForGame(returnal.Key));
		Assert.Equal(own.Id, returnal.Profile!.Selected!.Id);
		Assert.False(panel.Feel.IsLive);

		panel.Feel.RumbleLevel = 170;
		panel.Feel.IsPunchy = true;
		panel.Feel.IsFallbackWeapon = true;
		panel.Feel.GameHapticsLevel = 220;
		ProfileValues saved = vm.Settings.Profiles.Find(own.Id)!.Values;
		Assert.Equal((170.0, RumbleFeel.Punchy, FallbackTrigger.Weapon, 220.0), (saved.RumbleLevel, saved.RumbleFeel, saved.FallbackTrigger, saved.GameHapticsLevel));
		// The active (default) profile is untouched.
		Assert.Equal(sharedRumble, vm.Settings.RumbleLevel);
		Assert.Equal(sharedRumble, vm.Settings.Profiles.Default.Values.RumbleLevel);
		Assert.Equal(Text.PercentValue(170), panel.Feel.RumbleText);

		// Sound to haptics goes into the game's own profile too, not the active one.
		List<string?> audioChanged = [];
		panel.AudioHaptics.PropertyChanged += (_, e) => audioChanged.Add(e.PropertyName);
		panel.AudioHaptics.On = true;
		panel.AudioHaptics.Strength = 150;
		panel.AudioHaptics.IsFull = true;
		saved = vm.Settings.Profiles.Find(own.Id)!.Values;
		Assert.Equal((true, 150.0, Kopru.Core.Audio.AudioHapticsBand.Full), (saved.AudioHaptics, saved.AudioHapticsStrength, saved.AudioHapticsBand));
		Assert.False(vm.Settings.AudioHapticsOn);
		Assert.False(vm.Settings.Profiles.Default.Values.AudioHaptics);
		Assert.True(panel.AudioHaptics.IsFull);
		Assert.False(panel.AudioHaptics.IsBass);
		Assert.Equal(Text.AudioHapticsStatusWaiting(), panel.AudioHaptics.Status);
		Assert.NotEmpty(audioChanged);

		panel.ResetToDefaultCommand.Execute(null);
		Assert.True(panel.UsesDefault);
		Assert.Null(vm.Settings.Profiles.ProfileForGame(returnal.Key));
		Assert.NotNull(vm.Settings.Profiles.Find(own.Id));
		// Again: a second profile for the game ("Returnal 2"); the first one stays in the picker.
		panel.UseOwnSettingsCommand.Execute(null);
		Assert.Equal("Returnal 2", panel.ProfileName);
		panel.SaveProfileNameCommand.Execute(null);
		Assert.Single(vm.Settings.Profiles.Profiles, p => p.Name == "Returnal");
		Assert.Single(vm.Settings.Profiles.Profiles, p => p.Name == "Returnal 2");
		Assert.Contains(returnal.Profile!.Options, o => o.Id == own.Id);
	}

	// For the running game its own profile becomes the active one: the panel's changes apply now (through SettingsState).
	[AvaloniaFact]
	public void OwnSettings_ForTheRunningGame_ApplyLive()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		fake.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == "Cyberpunk 2077"));
		panel.UseOwnSettingsCommand.Execute(null); panel.SaveProfileNameCommand.Execute(null);
		Assert.Equal("Cyberpunk 2077", vm.Settings.ActiveProfileName);
		Assert.True(panel.Feel.IsLive);
		panel.Feel.RumbleLevel = 140;
		Assert.Equal(140, vm.Settings.RumbleLevel);
		Assert.Equal(140, vm.Settings.Profiles.Active.Values.RumbleLevel);
		// Haptics And Triggers shows the same (active) profile.
		Assert.Equal(140, vm.OtherGames.Feel.RumbleLevel);

		// Sound to haptics of the live profile goes through SettingsState (pushed to the service), and back.
		panel.AudioHaptics.On = true;
		panel.AudioHaptics.Strength = 60;
		Assert.True(vm.Settings.AudioHapticsOn);
		Assert.Equal(60, vm.Settings.AudioHapticsStrength);
		Assert.True(fake.Settings.Game.AudioHaptics.On);
		Assert.True(vm.Settings.Profiles.Active.Values.AudioHaptics);
		bool raised = false;
		panel.AudioHaptics.PropertyChanged += (_, _) => raised = true;
		vm.Settings.IsAudioHapticsFull = true;
		Assert.True(raised);
		Assert.True(panel.AudioHaptics.IsFull);
		fake.AudioHapticsStatus = new Kopru.Core.Audio.AudioHapticsStatus(Kopru.Core.Audio.AudioHapticsStatusKind.Listening, "Cyberpunk 2077",
			Kopru.Core.Audio.AudioCaptureMode.ProcessLoopback);
		Assert.Equal(vm.Settings.AudioHapticsStatus, panel.AudioHaptics.Status);
		Assert.Equal(Text.AudioHapticsStatusListening("Cyberpunk 2077"), panel.AudioHaptics.Status);

		panel.ResetToDefaultCommand.Execute(null);
		Assert.Equal(ProfileBook.DefaultId, vm.Settings.Profiles.ActiveId);
	}

	// Where it comes from, Hide for a found game (the panel closes), and sound to haptics available on this Windows.
	[AvaloniaFact]
	public async Task FoundGame_SourceHide_AndSoundToHapticsAvailable()
	{
		MainWindowViewModel vm = GameListScrollTests.Open(installedOnly: false, unsupported: true);
		await vm.SupportedGames.ScanTask;
		Dispatcher.UIThread.RunJobs();
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		GameItemViewModel racer = vm.SupportedGames.Games.Single(g => g.Name == "Pixel Racer");
		vm.SupportedGames.OpenGameCommand.Execute(racer);
		Assert.Equal("Steam", panel.SourceText);
		Assert.True(panel.IsUnsupported);
		Assert.Equal(Text.HideGame(), panel.HideOrRemoveText);
		Assert.Equal(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041), panel.AudioHaptics.Available);
		Assert.Equal(Text.AudioHapticsStatusOff(), panel.AudioHaptics.Status);

		panel.HideOrRemoveCommand.Execute(null);
		Assert.False(panel.IsOpen);
		Assert.DoesNotContain(vm.SupportedGames.Games, g => g.Name == "Pixel Racer");
	}

	// The panel at every size of the fit matrix, both languages, for a supported and an unsupported game with settings of
	// their own: inside the card, nothing cut off (the name may end in "…"), beside the list where the card is wide.
	[AvaloniaFact]
	public async Task Panel_FitsAtEverySize()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_FIT_RENDER_DIR") is { Length: > 0 } d ? d : null;
		Language? before = Localizer.Chosen;
		List<string> problems = [];
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				// The matrix and the owner's window: 2593x1575 pixels at 225 % Windows scaling, 1152x700 in layout units.
				foreach ((double width, double height) in FitCheck.Matrix.Append((1152, 700)))
				{
					foreach (string kind in new[] { "supported", "unsupported" })
					{
						(MainWindowViewModel vm, MainWindow window) = await OpenPanel(kind, code, width, height);
						string where = $"{width}x{height} {code} {kind}";
						// A pop-up over the page: the backdrop covers the page, the dialog lies fully inside it, the list keeps its
						// normal layout underneath.
						Border sheet = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "GameDialog");
						Border backdrop = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "GameDialogBackdrop");
						Border list = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ListCard");
						SupportedGamesView page = window.GetVisualDescendants().OfType<SupportedGamesView>().Single();
						Rect sheetRect = FitCheck.Bounds(sheet, window);
						Rect pageRect = FitCheck.Bounds(page, window);
						if (!pageRect.Contains(sheetRect) || sheetRect.Height < 100)
						{
							problems.Add($"{where}: dialog {sheetRect} outside the page {pageRect}");
						}
						if (!backdrop.IsEffectivelyVisible || FitCheck.Bounds(backdrop, window) != pageRect)
						{
							problems.Add($"{where}: backdrop {FitCheck.Bounds(backdrop, window)} does not cover the page {pageRect}");
						}
						if (!list.IsEffectivelyVisible)
						{
							problems.Add($"{where}: the list is hidden under the dialog");
						}
						// As big as the page with a margin around it (up to its widest), two columns where there is room, and at the
						// owner's window and 1920x1080 everything in view without scrolling.
						if (page.Bounds.Width - sheet.Bounds.Width > 2 * 28 + 1 && sheet.Bounds.Width < sheet.MaxWidth - 1)
						{
							problems.Add($"{where}: dialog {sheet.Bounds.Width:F0} wide in a {page.Bounds.Width:F0} page");
						}
						GamePanelView panelView = sheet.GetVisualDescendants().OfType<GamePanelView>().Single();
						if (!panelView.IsTwoColumns)
						{
							problems.Add($"{where}: one column in a {panelView.Bounds.Width:F0} wide dialog");
						}
						if ((width, height) is (1152, 700) or (1920, 1080))
						{
							problems.AddRange(FitCheck.Scrolls(panelView.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "PanelScroll"), where));
						}
						problems.AddRange(FitCheck.CutText(sheet).Where(p => !p.Contains(vm.SupportedGames.Panel.Game!.Name)).Select(p => $"{where}: {p}"));
						foreach (Control c in sheet.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.TemplatedParent == null && FitCheck.IsContent(c)))
						{
							Rect r = FitCheck.Bounds(c, window);
							if (r.Width > 0.5 && (r.Left < sheetRect.Left - 0.5 || r.Right > sheetRect.Right + 0.5))
							{
								problems.Add($"{where}: {FitCheck.Describe(c)} {r} outside the dialog {sheetRect}");
							}
						}
						if (outDir != null)
						{
							using var frame = window.CaptureRenderedFrame();
							Directory.CreateDirectory(outDir);
							frame!.Save(Path.Combine(outDir, $"game-panel-fit-{kind}-{width}x{height}-{code}.png"));
						}
						window.Close();
					}
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
		Assert.Empty(problems);
	}

	// The PNGs for the review: the panel of a supported and an unsupported game (with settings of their own) in Turkish.
	[AvaloniaFact]
	public async Task RenderPanels()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_PAGE_RENDER_DIR");
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.Turkish;
			// 1100x700, 1920x1080 and the owner's window (1152x700 in layout units: 2593x1575 at 225 %).
			foreach ((double width, double height) in new[] { (1100.0, 700.0), (1920.0, 1080.0), (1152.0, 700.0) })
			{
				foreach (string kind in new[] { "supported", "unsupported", "supported-default" })
				{
					(_, MainWindow window) = await OpenPanel(kind, "tr", width, height);
					AvaloniaHeadlessPlatform.ForceRenderTimerTick();
					using var frame = window.CaptureRenderedFrame();
					Assert.NotNull(frame);
					if (!string.IsNullOrEmpty(outDir))
					{
						Directory.CreateDirectory(outDir);
						frame!.Save(Path.Combine(outDir, $"game-panel-{kind}-{width}x{height}-tr.png"));
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

	// A start file chosen in the panel: saved per game (and loaded again), it gives the row and the panel "Oyna", which opens
	// it in its folder; Remove takes it away. It wins over the way the app finds itself while it exists.
	[AvaloniaFact]
	public async Task StartFile_ChosenInThePanel_IsSavedAndStartsTheGame()
	{
		string folder = Path.Combine(Path.GetTempPath(), "kopru-startfile-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(folder);
		string file = Path.Combine(folder, "Returnal Shortcut.lnk");
		File.WriteAllText(file, "x");
		try
		{
			var store = new MemorySettingsStore(new AppSettings());
			var vm = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(store));
			GamePanelViewModel panel = vm.SupportedGames.Panel;
			GameItemViewModel returnal = vm.SupportedGames.Games.Single(g => g.Name == "Returnal");
			Assert.False(returnal.CanLaunch);
			vm.SupportedGames.OpenGameCommand.Execute(returnal);
			Assert.True(panel.HasNoStartFile);

			// Cancelled: nothing changes.
			panel.PickStartFile = () => Task.FromResult<string?>(null);
			await panel.ChooseStartFileCommand.ExecuteAsync(null);
			Assert.False(panel.HasStartFile);

			panel.PickStartFile = () => Task.FromResult<string?>(file);
			await panel.ChooseStartFileCommand.ExecuteAsync(null);
			Assert.True(panel.HasStartFile);
			Assert.Equal(Text.StartFileLine("Returnal Shortcut.lnk"), panel.StartFileLine);
			Assert.True(returnal.CanLaunch);
			List<GameLaunch> started = [];
			vm.SupportedGames.StartGame = (launch, _) =>
			{
				started.Add(launch);
				return true;
			};
			panel.PlayCommand.Execute(null);
			Assert.Equal((LaunchVia.File, file, folder), (started[0].Via, started[0].Target, started[0].WorkingDirectory));

			// Saved with the settings and back after a restart.
			vm.Settings.SaveNow();
			Assert.Equal(file, store.Saved!.StartFiles![returnal.Key]);
			var again = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(store));
			Assert.Equal(file, again.Settings.StartFileFor(returnal.Key));
			Assert.True(again.SupportedGames.Games.Single(g => g.Name == "Returnal").CanLaunch);

			panel.RemoveStartFileCommand.Execute(null);
			Assert.False(panel.HasStartFile);
			Assert.False(returnal.CanLaunch);
			vm.Settings.SaveNow();
			Assert.Null(store.Saved!.StartFiles);
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Fact]
	public void StartFile_WinsWhileItExists()
	{
		var catalog = new CatalogGame("Grand Theft Auto V Enhanced", ["GTA5_Enhanced.exe"], 271590);
		var row = new KnownGame(catalog.Name, catalog.Exe[0], true, true, false, catalog, "gta");
		var installed = new InstalledGame(catalog, InstalledSource.Steam, @"C:\Steam\steamapps\common\GTAV", "3240220");
		static bool Exists(string path) => path == @"D:\Shortcuts\GTA.url";

		(GameLaunch? chosen, _) = GameLauncher.Explain(row, null, installed, Exists, @"D:\Shortcuts\GTA.url");
		Assert.Equal((LaunchVia.File, @"D:\Shortcuts\GTA.url", "file"), (chosen!.Via, chosen.Target, chosen.ViaName));
		// Gone: the app's own way again; with none, why.
		Assert.Equal("steam://rungameid/3240220", GameLauncher.Explain(row, null, installed, Exists, @"E:\Gone\GTA.exe").Launch!.Target);
		Assert.Equal("start file missing: GTA.exe", GameLauncher.Explain(row, null, null, Exists, @"E:\Gone\GTA.exe").Reason);
		Assert.Equal("Başlatma Dosyası Seç…", Text.StartFileChoose(Language.Turkish));
		Assert.Equal("Choose Start File…", Text.StartFileChoose(Language.English));
		Assert.Equal("Başlatma Dosyasını Değiştir", Text.StartFileChange(Language.Turkish));
	}

	[Fact]
	public void Texts_PlainAndInBothLanguages()
	{
		Assert.Equal("Bu Oyuna Özel Ayarlar", Text.GameOwnSettings(Language.Turkish));
		Assert.Equal("Settings For This Game", Text.GameOwnSettings(Language.English));
		Assert.Equal("Varsayılana Dön", Text.GameResetToDefault(Language.Turkish));
		Assert.Equal("Sesi Titreşime Çevir", Text.AudioHapticsLabel(Language.Turkish));
		Assert.Equal("Turn Sound Into Haptics", Text.AudioHapticsLabel(Language.English));
		Assert.Equal("Tüm Ses", Text.AudioHapticsBandFull(Language.Turkish));
	}

	// The Games page with the panel open: "supported" (the running Cyberpunk 2077) or "unsupported" (Pixel Racer, found by
	// Steam) with settings of their own, or "supported-default" (Returnal on the default profile).
	internal static async Task<(MainWindowViewModel Vm, MainWindow Window)> OpenPanel(string kind, string code, double width, double height)
	{
		// The sample catalog (Cyberpunk 2077 runs, Returnal is found installed), games found by Steam and Epic, and added ones.
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(new MemorySettingsStore(new AppSettings
		{
			GameListCheckedAt = DateTimeOffset.Now,
			Language = code,
			UserGames = [new UserGame("Hades II", @"C:\Games\Hades II\Hades2.exe"), new UserGame("Stardew Valley", @"D:\Games\Stardew Valley\Stardew Valley.exe")]
		}), GameList: new GameCatalogUpdater(new HttpClient(), Path.Combine(Path.GetTempPath(), "kopru-panel-games.json"), "https://example.invalid/list.json"),
			InstalledGames: FakeFoundGames.Installed(FakeFoundGames.Folders())));
		await vm.SupportedGames.ScanTask;
		vm.SupportedGames.ShowUnsupported = kind == "unsupported";
		Dispatcher.UIThread.RunJobs();
		string name = kind switch { "unsupported" => "Pixel Racer", "supported" => "Cyberpunk 2077", _ => "Returnal" };
		vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == name));
		if (kind != "supported-default")
		{
			vm.SupportedGames.Panel.UseOwnSettingsCommand.Execute(null); vm.SupportedGames.Panel.SaveProfileNameCommand.Execute(null);
		}
		var window = new MainWindow { DataContext = vm, Width = width, Height = height };
		window.Show();
		vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
		FitCheck.Settle(window);
		Thread.Sleep(50);
		FitCheck.Settle(window);
		return (vm, window);
	}

	private static void Click(Window window, Visual target)
	{
		Point center = FitCheck.Bounds(target, window).Center;
		window.MouseDown(center, MouseButton.Left);
		window.MouseUp(center, MouseButton.Left);
		FitCheck.Settle(window);
	}
}
