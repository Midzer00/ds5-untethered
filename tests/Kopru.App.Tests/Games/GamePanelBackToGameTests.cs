// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Games;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// "Oyuna Dön" in a game's pop-up and row on the Games page: while that game runs it stands in Play's place and runs the
// Overview's way back (GameReturn: Mouse Mode off, the game to the front), closing the pop-up; other games keep Play.
// Writes game-panel-back-to-game-1152x700-tr.png to KOPRU_PAGE_RENDER_DIR when it is set.
public sealed class GamePanelBackToGameTests
{
	private const string MadeUpGame = "Neon Drift";
	private const string MadeUpExe = "NeonDrift.exe";
	private const string MadeUpPath = @"C:\Games\Neon Drift\NeonDrift.exe";

	[AvaloniaFact]
	public void RunningGame_BackToGame_ClosesThePanel_AndRunsTheOverviewsWayBack()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now })));
		List<(string Reason, int Game)> returned = [];
		vm.BackToGame.Return = (reason, game) => returned.Add((reason, game));
		fake.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		Assert.Same(vm.BackToGame, vm.SupportedGames.BackToGame);

		GameItemViewModel cyberpunk = vm.SupportedGames.Games.Single(g => g.Name == "Cyberpunk 2077");
		GameItemViewModel returnal = vm.SupportedGames.Games.Single(g => g.Name == "Returnal");
		Assert.True(cyberpunk.IsRunning);
		Assert.False(cyberpunk.ShowsPlay);
		Assert.False(returnal.IsRunning);
		Assert.Equal(Text.BackToGameTip("Cyberpunk 2077"), cyberpunk.BackToGameTip);

		// The pop-up of the running game: Back To Game, with Mouse Mode on as when the player came with the controller as a mouse.
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		vm.SupportedGames.OpenGameCommand.Execute(cyberpunk);
		vm.Settings.SetDesktopMode(true, "settings switch");
		List<string> log = MouseModeAutoOffAppTests.WatchLog("back to game:", () => panel.BackToGameCommand.Execute(null));
		Assert.False(panel.IsOpen);
		Assert.False(vm.Settings.DesktopModeOn);
		Assert.Equal("back to game button", Assert.Single(returned).Reason);
		Assert.Equal(["back to game: back to game button (Cyberpunk 2077)"], log);

		// Another game's pop-up: nothing to go back to there.
		vm.SupportedGames.OpenGameCommand.Execute(returnal);
		panel.BackToGameCommand.Execute(null);
		Assert.True(panel.IsOpen);
		Assert.Single(returned);

		// The row's button runs the same way back.
		cyberpunk.BackToGameCommand.Execute(null);
		Assert.Equal(2, returned.Count);

		// The game ended: its row and pop-up offer Play again (where we know how).
		List<string?> changed = [];
		cyberpunk.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		Assert.False(cyberpunk.IsRunning);
		Assert.Equal(cyberpunk.CanLaunch, cyberpunk.ShowsPlay);
		Assert.Contains(nameof(GameItemViewModel.IsRunning), changed);
		Assert.Contains(nameof(GameItemViewModel.ShowsPlay), changed);
	}

	// In the window: the running game's pop-up shows Oyuna Dön (primary, with the Overview's icon, whole) and no Oyna, and a click
	// on it goes back to the game; a game that is not running shows Oyna. The running game's row shows Oyuna Dön, the others Oyna.
	[AvaloniaFact]
	public async Task PopUp_ShowsBackToGame_ForTheRunningGame_PlayForTheOthers()
	{
		Language? before = Localizer.Chosen;
		List<string> problems = [];
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height) in new[] { (1100.0, 700.0), (1152.0, 700.0) })
				{
					(MainWindowViewModel vm, FakeControllerService _, MainWindow window, List<string> returned) = await Open(code, width, height, MadeUpGame);
					string at = $"{code} {width}x{height}";
					GamePanelView view = window.GetVisualDescendants().OfType<GamePanelView>().Single();
					Button back = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PanelBackToGameButton");
					Button play = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PanelPlayButton");
					Assert.True(back.IsEffectivelyVisible, at);
					Assert.False(play.IsEffectivelyVisible, at);
					Assert.Contains(back.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Text.BackToGame(language));
					Assert.Single(back.GetVisualDescendants().OfType<Kopru.App.Controls.IconSlot>());
					problems.AddRange(FitCheck.CutText(back).Select(p => $"{at}: {p}"));
					Rect sheet = FitCheck.Bounds(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "GameDialog"), window);
					if (!sheet.Contains(FitCheck.Bounds(back, window)))
					{
						problems.Add($"{at}: back {FitCheck.Bounds(back, window)} outside the dialog {sheet}");
					}

					Click(window, back);
					Assert.False(vm.SupportedGames.Panel.IsOpen);
					Assert.Equal(["back to game button"], returned);

					// The running game's row: Oyuna Dön; another game's (one we can start): Oyna.
					vm.SupportedGames.ShowUnsupported = true;
					FitCheck.Settle(window);
					Button rowBack = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("back") && b.DataContext is GameItemViewModel { Name: MadeUpGame });
					Assert.True(rowBack.IsEffectivelyVisible, at);
					Assert.Equal(Text.BackToGame(language), rowBack.Content);
					Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(),
						b => b.Classes.Contains("play") && b.IsEffectivelyVisible && b.DataContext is GameItemViewModel { Name: MadeUpGame });

					// A game that is not running: Oyna in its pop-up.
					GameItemViewModel other = vm.SupportedGames.Games.First(g => !g.IsRunning && g.CanLaunch);
					vm.SupportedGames.OpenGameCommand.Execute(other);
					FitCheck.Settle(window);
					view = window.GetVisualDescendants().OfType<GamePanelView>().Single();
					Assert.True(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PanelPlayButton").IsEffectivelyVisible, at);
					Assert.False(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PanelBackToGameButton").IsEffectivelyVisible, at);
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

	[Fact]
	public void Texts()
	{
		Assert.Equal("Oyuna Dön", Text.BackToGame(Language.Turkish));
		Assert.Equal("Back To Game", Text.BackToGame(Language.English));
		Assert.Equal("Oyna", Text.PlayGame(Language.Turkish));
		Assert.Equal("Play", Text.PlayGame(Language.English));
		Assert.Equal("Uygulamayı gizler, Neon Drift öne gelir.", Text.BackToGameTip("Neon Drift", Language.Turkish));
		Assert.Equal("Hides the app and brings Neon Drift to the front.", Text.BackToGameTip("Neon Drift", Language.English));
	}

	// The PNG for the review: the pop-up of a running (made-up) game at the owner's window, in Turkish.
	[AvaloniaFact]
	public async Task RenderPopUp()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_PAGE_RENDER_DIR");
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.Turkish;
			(_, _, MainWindow window, _) = await Open("tr", 1152, 700, MadeUpGame);
			Thread.Sleep(320);
			FitCheck.Settle(window);
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			using var frame = window.CaptureRenderedFrame();
			Assert.NotNull(frame);
			if (!string.IsNullOrEmpty(outDir))
			{
				Directory.CreateDirectory(outDir);
				frame!.Save(Path.Combine(outDir, "game-panel-back-to-game-1152x700-tr.png"));
			}
			window.Close();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The Games page with the pop-up of the running game open: a made-up added game in front (Cyberpunk 2077 has ended), with
	// settings of its own; the sample catalog and games found by Steam and Epic around it.
	private static async Task<(MainWindowViewModel Vm, FakeControllerService Fake, MainWindow Window, List<string> Returned)> Open(string code, double width, double height, string name)
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings
		{
			GameListCheckedAt = DateTimeOffset.Now,
			Language = code,
			UserGames = [new UserGame(MadeUpGame, MadeUpPath)]
		}), InstalledGames: FakeFoundGames.Installed(FakeFoundGames.Folders())));
		List<string> returned = [];
		vm.BackToGame.Return = (reason, _) => returned.Add(reason);
		await vm.SupportedGames.ScanTask;
		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Watcher.Source.Show(MadeUpExe, MadeUpPath);
		fake.Games.Tick();
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(name, fake.Games.ActiveGame?.Name);
		vm.SupportedGames.ShowUnsupported = true;
		Dispatcher.UIThread.RunJobs();
		vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == name));
		vm.SupportedGames.Panel.UseOwnSettingsCommand.Execute(null); vm.SupportedGames.Panel.SaveProfileNameCommand.Execute(null);
		var window = new MainWindow { DataContext = vm, Width = width, Height = height };
		window.Show();
		vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
		FitCheck.Settle(window);
		Thread.Sleep(50);
		FitCheck.Settle(window);
		return (vm, fake, window, returned);
	}

	private static void Click(Window window, Visual target)
	{
		Point center = FitCheck.Bounds(target, window).Center;
		window.MouseDown(center, MouseButton.Left);
		window.MouseUp(center, MouseButton.Left);
		FitCheck.Settle(window);
	}
}
