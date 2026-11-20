// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The way back to a running game (GameReturn): the "Back To Game" button in the Overview's header while a game runs, Mouse Mode turned off with PS + Options over a game, and Mouse Mode going off on every way back.
public class GameReturnAppTests
{
	private sealed class Run
	{
		public FakeControllerService Fake { get; } = new(animate: false);
		public MainWindowViewModel Vm { get; }
		public List<(string Reason, int Game)> Returned { get; } = [];

		public Run(MemorySettingsStore? store = null)
		{
			Vm = new MainWindowViewModel(Fake, new AppEnvironment(store ?? new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now })));
			Vm.BackToGame.Return = (reason, game) => Returned.Add((reason, game));
		}

		// PS brings the window up over the game (the fake starts with Cyberpunk 2077 in front).
		public void OpenWithPs()
		{
			Fake.RaiseShortcut(ShortcutAction.OpenApp);
			Vm.BackToGame.SetWindowActive(true);
		}
	}

	[AvaloniaFact]
	public void Button_ShowsWhileAGameRuns()
	{
		var run = new Run();
		GameReturn back = run.Vm.BackToGame;
		Assert.Same(back, run.Vm.Overview.BackToGame);
		Assert.True(back.IsGameRunning);
		Assert.Equal(Text.BackToGame(), back.Label);
		Assert.Contains("Cyberpunk 2077", back.ToolTip);

		List<string> log = MouseModeAutoOffAppTests.WatchLog("back to game:", () => back.BackCommand.Execute(null));
		Assert.Equal("back to game button", Assert.Single(run.Returned).Reason);
		Assert.Equal(["back to game: back to game button (Cyberpunk 2077)"], log);

		// The game gone: hidden.
		run.Fake.Watcher.Exit("Cyberpunk2077.exe");
		run.Fake.Games.Tick();
		Assert.False(back.IsGameRunning);
	}

	// PS + Options turning Mouse Mode off while the window PS brought up over the game is in front: back to the game, like the
	// PS tap. Not from the Settings switch, nor when the window was not brought up over a game.
	[AvaloniaFact]
	public void MouseModeOff_WithPsOptions_OverAGame_GoesBackToTheGame()
	{
		var run = new Run();
		run.OpenWithPs();
		run.Vm.Settings.SetDesktopMode(true, SettingsState.TouchpadDoublePressSource);
		run.Fake.RaiseShortcut(ShortcutAction.ToggleDesktopMode);
		Assert.False(run.Vm.Settings.DesktopModeOn);
		Assert.Equal("mouse mode turned off with PS + Options", Assert.Single(run.Returned).Reason);

		// The Settings switch: stays in the app.
		var settings = new Run();
		settings.OpenWithPs();
		settings.Vm.Settings.SetDesktopMode(true, "settings switch");
		settings.Vm.Settings.SetDesktopMode(false, "settings switch");
		Assert.Empty(settings.Returned);

		// Opened from the tray (no PS over a game): stays in the app.
		var tray = new Run();
		tray.Vm.BackToGame.SetWindowActive(true);
		tray.Vm.Settings.SetDesktopMode(true, "tray menu");
		tray.Fake.RaiseShortcut(ShortcutAction.ToggleDesktopMode);
		Assert.Empty(tray.Returned);

		// The window left the front meanwhile (the player alt-tabbed): nothing to return from.
		var away = new Run();
		away.OpenWithPs();
		away.Vm.BackToGame.SetWindowActive(false);
		away.Vm.Settings.SetDesktopMode(true, "tray menu");
		away.Fake.RaiseShortcut(ShortcutAction.ToggleDesktopMode);
		Assert.Empty(away.Returned);
	}

	// Every way back to the game turns Mouse Mode off (the player who pressed the button with the controller as a mouse must
	// get the controller back in the game), logged, with no toast of its own.
	[AvaloniaFact]
	public void EveryWayBack_TurnsMouseModeOff_WithoutAToast()
	{
		var button = new Run();
		var toasts = new List<string>();
		button.Vm.Settings.ShortcutFeedback += (title, _) => toasts.Add(title);
		button.OpenWithPs();
		button.Vm.Settings.SetDesktopMode(true, "settings switch");
		List<string> log = MouseModeAutoOffAppTests.WatchLog("mouse mode: off", () => button.Vm.BackToGame.BackCommand.Execute(null));
		Assert.False(button.Vm.Settings.DesktopModeOn);
		Assert.False(button.Fake.Settings.DesktopModeOn);
		Assert.Contains("mouse mode: off (back to game)", log);
		Assert.Empty(toasts);
		Assert.Single(button.Returned);

		// The PS tap from the app while a game runs.
		var tap = new Run();
		tap.OpenWithPs();
		tap.Vm.Settings.SetDesktopMode(true, "settings switch");
		Assert.Contains("mouse mode: off (back to game)",
			MouseModeAutoOffAppTests.WatchLog("mouse mode: off", () => tap.Fake.RaiseShortcut(ShortcutAction.HideApp)));
		Assert.False(tap.Vm.Settings.DesktopModeOn);

		// PS + Options over the game: off (by the shortcut) and back in the game.
		var options = new Run();
		options.OpenWithPs();
		options.Vm.Settings.SetDesktopMode(true, "settings switch");
		options.Fake.RaiseShortcut(ShortcutAction.ToggleDesktopMode);
		Assert.False(options.Vm.Settings.DesktopModeOn);
		Assert.Single(options.Returned);

		// The PS tap hiding the app with no game running leaves Mouse Mode alone (the player stays on the desktop).
		var desktop = new Run();
		desktop.Fake.Watcher.Exit("Cyberpunk2077.exe");
		desktop.Fake.Games.Tick();
		desktop.Vm.BackToGame.SetWindowActive(true);
		desktop.Vm.Settings.SetDesktopMode(true, "settings switch");
		desktop.Fake.RaiseShortcut(ShortcutAction.HideApp);
		Assert.True(desktop.Vm.Settings.DesktopModeOn);
	}

	[Fact]
	public void Texts()
	{
		Assert.Equal("Oyuna Dön", Text.BackToGame(Language.Turkish));
		Assert.Equal("Back To Game", Text.BackToGame(Language.English));
	}

	// "Back To Game" beside "Turn Off Controller" in the Overview's header, from the smallest to the largest window in both
	// languages: as tall as that button, on its left on the same line, whole, inside the page column, clear of the title and
	// status line; the sidebar logo stays. Saves main-back-to-game-overview-{size}-{lang}.png to KOPRU_FIT_RENDER_DIR.
	[AvaloniaFact]
	public void Button_SitsBesideTurnOff_AtEverySize()
	{
		string? dir = Environment.GetEnvironmentVariable("KOPRU_FIT_RENDER_DIR") is { Length: > 0 } d ? d : null;
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height) in FitCheck.Matrix)
				{
					(MainWindowViewModel vm, FakeControllerService _) = SettingsFitTests.FullCase(code);
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.Navigate(vm.Overview);
					FitCheck.Settle(window);
					string at = $"{code} {width}x{height} (scale {window.ContentScale:F2})";
					Visual root = window.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "Root");
					OverviewView view = window.GetVisualDescendants().OfType<OverviewView>().Single();
					Button back = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BackToGame");
					Button off = view.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.Overview.PowerOffCommand);
					Grid header = view.GetVisualDescendants().OfType<Grid>().First(g => g.Classes.Contains("pageHeader"));
					Rect r = FitCheck.Bounds(back, root);
					Rect o = FitCheck.Bounds(off, root);
					Rect title = FitCheck.Bounds(header.Children[0], root);
					if (!back.IsEffectivelyVisible || !off.IsEffectivelyVisible)
					{
						problems.Add($"{at}: back shown {back.IsEffectivelyVisible}, turn off shown {off.IsEffectivelyVisible}");
					}
					if (Math.Abs(back.Bounds.Height - off.Bounds.Height) > 0.5 || Math.Abs(r.Top - o.Top) > 0.5 || r.Right > o.Left - 4)
					{
						problems.Add($"{at}: back {r} not beside turn off {o}");
					}
					if (r.Left < title.Right - 0.5 && r.Intersects(title))
					{
						problems.Add($"{at}: back {r} over the title {title}");
					}
					problems.AddRange(FitCheck.CutText(back).Select(p => $"{at}: {p}"));
					problems.AddRange(FitCheck.CutText(header).Select(p => $"{at}: {p}"));
					if (!window.GetVisualDescendants().OfType<Image>().First().IsEffectivelyVisible)
					{
						problems.Add($"{at}: the sidebar logo is hidden");
					}
					if (dir != null && code == "tr" && (width, height) is (1100, 700) or (1920, 1080))
					{
						Thread.Sleep(320);
						FitCheck.Settle(window);
						using var frame = window.CaptureRenderedFrame();
						Directory.CreateDirectory(dir);
						frame!.Save(Path.Combine(dir, $"main-back-to-game-overview-{width}x{height}-{code}.png"));
					}
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}
}
