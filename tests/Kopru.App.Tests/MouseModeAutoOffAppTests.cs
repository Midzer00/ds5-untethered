// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// Mouse mode switching itself off when a game starts using the controller (a trigger effect, haptics, rumble) or a known
// game comes to the front, its toast, the touchpad double press that turns it on, and the log lines for every change.
public class MouseModeAutoOffAppTests
{
	private sealed class Harness
	{
		public long Now = 1_000_000;
		public bool FullScreen;
		public FakeControllerService Fake { get; }
		public MainWindowViewModel Vm { get; }
		public List<AppNotification> Raised { get; } = [];
		public SettingsState Settings => Vm.Settings;

		public Harness()
		{
			Fake = new FakeControllerService(animate: false);
			Vm = new MainWindowViewModel(Fake, new AppEnvironment(null, FullScreen: () => FullScreen, Clock: () => Now));
			Vm.Sticks.StopPreview();
			Vm.Notifications.Raised += Raised.Add;
			// No known game in front (the fake starts with Cyberpunk 2077 in front).
			Fake.Watcher.Exit("Cyberpunk2077.exe");
			Fake.Games.Tick();
		}

		// Mouse mode on, then past the grace time, with `inFront` in front (what the game tick saw; null: this app, the
		// desktop or a desktop app such as the browser).
		public void TurnOn(string source = "settings switch", string? inFront = "UnlistedGame.exe")
		{
			Settings.SetDesktopMode(true, source);
			Now += (long)SettingsState.GameUseGrace.TotalMilliseconds + 1000;
			Fake.SetForeground(inFront);
		}
	}

	private sealed class NoShell : IAppShell
	{
		public void ShowMainWindow(AppPage? page = null) { }

		public void Exit() { }
	}

	// Collects the log lines that contain marker (other tests log in parallel).
	internal static List<string> WatchLog(string marker, Action action)
	{
		var lines = new List<string>();
		void Watch(string line)
		{
			if (line.Contains(marker, StringComparison.Ordinal))
			{
				lock (lines)
				{
					lines.Add(line);
				}
			}
		}
		Kopru.Core.Diagnostics.AppLog.Written += Watch;
		try
		{
			action();
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= Watch;
		}
		lock (lines)
		{
			return lines.ToList();
		}
	}

	[AvaloniaFact]
	public void GameUsingTheController_TurnsMouseModeOff_WithItsToastAndLogLine()
	{
		foreach (GameUse use in new[] { GameUse.TriggerEffect, GameUse.Haptics, GameUse.Rumble })
		{
			var h = new Harness();
			var autoOff = new List<(string?, string)>();
			h.Settings.DesktopAutoOff += (game, exe) => autoOff.Add((game, exe));
			h.TurnOn();
			string what = use.LogName();
			List<string> log = WatchLog("mouse mode", () => h.Fake.RaiseGameUse(use));

			Assert.False(h.Settings.DesktopModeOn);
			Assert.False(h.Fake.Settings.DesktopModeOn);
			Assert.Equal([((string?)null, what)], autoOff);
			Assert.Contains($"mouse mode: switched off automatically, the game started using the controller ({what})", log);
			Assert.Contains($"toast: mouse mode off for a game using the controller ({what}) shown", log);
			AppNotification n = h.Raised.Single(r => r.Type == NotificationType.MouseModeOff);
			Assert.Equal(Text.DesktopAutoOffTitle(), n.Title);
			Assert.Equal(Text.DesktopOffGameStartedBody(), n.Body);
			Assert.Equal("Fare Modu Kapandı", Text.DesktopAutoOffTitle(Language.Turkish));
			Assert.Equal("Kumanda oyuna geçti.", Text.DesktopOffGameStartedBody(Language.Turkish));
			Assert.Equal("Controller is back in the game.", Text.DesktopOffGameStartedBody(Language.English));
			// Only the automatic toast, not the shortcut one as well.
			Assert.DoesNotContain(h.Raised, r => r.Type == NotificationType.Shortcut && r.Title == Text.DesktopOffTitle());

			// It stays off: the next signal does nothing.
			h.Fake.RaiseGameUse(use);
			Assert.False(h.Settings.DesktopModeOn);
			Assert.Single(autoOff);
		}
	}

	[AvaloniaFact]
	public void GameUse_LeavesMouseModeOn_RightAfterItWasTurnedOn_OrWhenTheOptionIsOff()
	{
		// Within the grace time (the game reacting to the neutral input or to the presses that turned it on).
		var h = new Harness();
		h.Settings.SetDesktopMode(true, "touchpad double press");
		h.Fake.SetForeground("UnlistedGame.exe");
		h.Now += 500;
		h.Fake.RaiseGameUse(GameUse.Rumble);
		Assert.True(h.Settings.DesktopModeOn);
		h.Now += (long)SettingsState.GameUseGrace.TotalMilliseconds;
		h.Fake.RaiseGameUse(GameUse.Rumble);
		Assert.False(h.Settings.DesktopModeOn);

		// "Turn Off When a Game Starts" off: stays on, and says so once per mouse mode session, not per rumble start.
		var off = new Harness();
		off.Settings.DesktopPanel.AutoOffInGame = false;
		off.TurnOn();
		List<string> log = WatchLog("but turning off in games is off", () =>
		{
			for (int i = 0; i < 5; i++)
			{
				off.Fake.RaiseGameUse(GameUse.Rumble);
			}
		});
		Assert.True(off.Settings.DesktopModeOn);
		Assert.Single(log);
		Assert.DoesNotContain(off.Raised, r => r.Type == NotificationType.MouseModeOff);

		// Mouse mode already off: nothing happens.
		var idle = new Harness();
		idle.Fake.RaiseGameUse(GameUse.Haptics);
		Assert.False(idle.Settings.DesktopModeOn);
		Assert.DoesNotContain(idle.Raised, r => r.Type == NotificationType.MouseModeOff);
	}

	// A game still running and sending rumble and trigger effects while the player is on the desktop or in the browser:
	// Mouse Mode turned on there stays on (however often the game rumbles, logged once); when the game's window comes back
	// to the front with its trigger effect held, Mouse Mode goes off.
	[AvaloniaFact]
	public void RunningGame_LeavesMouseModeOn_OnTheDesktop_UntilItComesToTheFront()
	{
		var h = new Harness();
		h.Fake.GameUsingControllerNow = GameUse.TriggerEffect;
		h.TurnOn(inFront: null);
		List<string> log = WatchLog("but no game is in front", () =>
		{
			for (int i = 0; i < 4; i++)
			{
				h.Fake.RaiseGameUse(GameUse.Rumble);
				h.Fake.RaiseGameUse(GameUse.Haptics);
			}
		});
		Assert.True(h.Settings.DesktopModeOn);
		Assert.Single(log);

		// The app's own window or a desktop app in front (the tick reports null again): still on.
		h.Fake.SetForeground(null);
		Assert.True(h.Settings.DesktopModeOn);

		List<string> off = WatchLog("mouse mode: switched off automatically", () => h.Fake.SetForeground("UnlistedGame.exe"));
		Assert.False(h.Settings.DesktopModeOn);
		Assert.Contains("mouse mode: switched off automatically, UnlistedGame.exe came to the front while the game uses the controller (trigger effect)", off);
		Assert.Equal(Text.DesktopOffGameStartedBody(), h.Raised.Single(r => r.Type == NotificationType.MouseModeOff).Body);
	}

	// Signals that arrived (or were already on) in the grace time are not lost: the game coming to the front afterwards
	// with the controller in use switches Mouse Mode off. Nothing in use: it stays on.
	[AvaloniaFact]
	public void UseDuringTheGrace_CountsWhenTheGameComesToTheFront()
	{
		var h = new Harness();
		h.Settings.SetDesktopMode(true, "shortcut");
		h.Fake.GameUsingControllerNow = GameUse.Rumble;
		h.Fake.SetForeground("UnlistedGame.exe");
		h.Fake.RaiseGameUse(GameUse.Rumble);
		Assert.True(h.Settings.DesktopModeOn);
		h.Now += (long)SettingsState.GameUseGrace.TotalMilliseconds + 1;
		h.Fake.SetForeground(null);
		h.Fake.SetForeground("UnlistedGame.exe");
		Assert.False(h.Settings.DesktopModeOn);

		var idle = new Harness();
		idle.TurnOn(inFront: null);
		idle.Fake.SetForeground("UnlistedGame.exe");
		Assert.True(idle.Settings.DesktopModeOn);
	}

	private const string WindowsDir = @"C:\Windows";

	private static string? Possible(string exe, string? path, bool app = false, string? known = null) =>
		RealControllerService.PossibleGame(app, known, new ProcessInfo(1, exe, path), WindowsDir);

	[Fact]
	public void PossibleGame_NeverThisApp_TheShell_OrDesktopApps()
	{
		Assert.Null(Possible("Game.exe", @"C:\Games\Game\Game.exe", app: true));
		Assert.Null(RealControllerService.PossibleGame(false, null, null, WindowsDir));
		Assert.Null(Possible("explorer.exe", @"C:\Windows\explorer.exe"));
		Assert.Null(Possible("chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe"));
		Assert.Null(Possible("Discord.exe", @"C:\Users\u\AppData\Local\Discord\app-1.0\Discord.exe"));
		Assert.Equal("Game.exe", Possible("Game.exe", @"C:\Games\Game\Game.exe"));
		Assert.Equal("Cyberpunk2077.exe", Possible("Cyberpunk2077.exe", null, known: "Cyberpunk2077.exe"));
	}

	// Windows' own apps and tools (the Snipping Tool the owner used for a screenshot) never switch Mouse Mode off; nor does a
	// program whose path can't be read. Games in a Steam library or any folder of their own may.
	[Fact]
	public void PossibleGame_NeverWindowsOwnApps_OrAnUnreadablePath()
	{
		Assert.Null(Possible("SnippingTool.exe", @"C:\Windows\System32\SnippingTool.exe"));
		Assert.Null(Possible("SnippingTool.exe",
			@"C:\Program Files\WindowsApps\Microsoft.ScreenSketch_11.2409.25.0_x64__8wekyb3d8bbwe\SnippingTool\SnippingTool.exe"));
		Assert.Null(Possible("Notepad.exe", @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2410.21.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe"));
		Assert.Null(Possible("notepad.exe", @"c:\windows\notepad.exe"));
		Assert.Null(Possible("CalculatorApp.exe",
			@"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.2410.0.0_x64__8wekyb3d8bbwe\CalculatorApp.exe"));
		Assert.Null(Possible("explorer.exe", @"C:\Windows\explorer.exe"));
		Assert.Null(Possible("Unknown.exe", null));
		Assert.Null(Possible("Unknown.exe", "Unknown.exe"));

		Assert.Equal("u4.exe", Possible("u4.exe", @"D:\SteamLibrary\steamapps\common\Uncharted Legacy of Thieves Collection\u4.exe"));
		Assert.Equal("Game.exe", Possible("Game.exe", @"C:\Games\Some Game\bin\Game.exe"));
		// Another publisher's packaged app (a Game Pass game) may be a game; so may a folder only named like Windows.
		Assert.Equal("Game.exe", Possible("Game.exe", @"C:\Program Files\WindowsApps\Publisher.Game_1.0.0.0_x64__abc\Game.exe"));
		Assert.Equal("Game.exe", Possible("Game.exe", @"C:\WindowsGames\Game.exe"));

		Assert.Equal("Windows folder", RealControllerService.WindowsTool(@"C:\Windows\System32\SnippingTool.exe", WindowsDir));
		Assert.Equal("Microsoft app", RealControllerService.WindowsTool(
			@"C:\Program Files\WindowsApps\Microsoft.ScreenSketch_11.2409.25.0_x64__8wekyb3d8bbwe\SnippingTool\SnippingTool.exe", WindowsDir));
		RealControllerService.PossibleGame(false, null, new ProcessInfo(1, "Unknown.exe", null), out string? why, WindowsDir);
		Assert.Equal("path not readable", why);
	}

	// The real service reads the foreground at the game tick only while Mouse Mode is on, and reports changes.
	[Fact]
	public void RealService_ReadsTheForeground_OnlyWhileMouseModeIsOn()
	{
		var controllers = new TestControllers();
		using var service = new RealControllerService(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher()), new TestHiding(),
			controllers, a => a(), _ => "id", timers: false, games: new GameModeController(FakeControllerService.SampleCatalog, new FakeGameWatcher(null), null));
		int reads = 0, changes = 0;
		string? exe = "Unlisted.exe";
		string? path = @"C:\Games\Unlisted\Unlisted.exe";
		service.ForegroundProcess = () =>
		{
			reads++;
			return exe == null ? null : new ProcessInfo(1, exe, path);
		};
		service.ForegroundChanged += (_, _) => changes++;
		service.GameTick();
		Assert.Equal(0, reads);
		Assert.Null(service.PossibleGameInFront);

		service.ApplySettings(new ControllerSettings { DesktopModeOn = true });
		service.GameTick();
		Assert.Equal("Unlisted.exe", service.PossibleGameInFront);
		Assert.Equal(1, changes);
		service.GameTick();
		Assert.Equal(1, changes);
		exe = "msedge.exe";
		service.GameTick();
		Assert.Null(service.PossibleGameInFront);
		Assert.Equal(2, changes);
		Assert.Null(service.GameUsingControllerNow);

		// The Snipping Tool in front (a screenshot while the game holds a trigger effect): not a game, said once.
		exe = "SnippingTool.exe";
		path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "SnippingTool.exe");
		List<string> log = WatchLog("in front does not count as a game", () =>
		{
			service.GameTick();
			service.GameTick();
		});
		Assert.Null(service.PossibleGameInFront);
		Assert.Equal(["mouse mode: SnippingTool.exe in front does not count as a game (Windows folder)"], log);
	}

	[AvaloniaFact]
	public void KnownGame_AutoOff_ShowsTheGameToast()
	{
		var h = new Harness();
		h.TurnOn();
		List<string> log = WatchLog("Returnal-Win64-Shipping.exe", () =>
		{
			h.Fake.Watcher.Exe = "Returnal-Win64-Shipping.exe";
			h.Fake.Games.Tick();
		});
		Assert.False(h.Settings.DesktopModeOn);
		Assert.Contains("mouse mode: switched off automatically, Returnal came to the front (Returnal-Win64-Shipping.exe)", log);
		AppNotification n = h.Raised.Single(r => r.Type == NotificationType.MouseModeOff);
		Assert.Equal(Text.DesktopOffGameBody(), n.Body);
		Assert.Equal("Fare Modu kapandı, kumanda oyunda.", Text.DesktopOffGameBody(Language.Turkish));
		Assert.Equal("Mouse Mode off, controller in game.", Text.DesktopOffGameBody(Language.English));
		// "Game detected" for the same game came in the same moment: one toast under the game's name, in its place.
		if (h.Raised.Any(r => r.Type == NotificationType.GameDetected))
		{
			Assert.Equal(h.Raised.Single(r => r.Type == NotificationType.GameDetected).Title, n.Title);
			Assert.Equal(NotificationType.GameDetected, n.Replaces);
			Assert.Contains(log, l => l.StartsWith("toast: mouse mode off for Returnal-Win64-Shipping.exe shown with game detected for ", StringComparison.Ordinal));
		}
		else
		{
			Assert.Equal(ForegroundGame.DisplayName("Returnal-Win64-Shipping.exe"), n.Title);
			Assert.Contains("toast: mouse mode off for Returnal-Win64-Shipping.exe shown", log);
		}
	}

	// Mouse Mode going off for a game that uses the controller, then "game detected" for it a moment later: one toast under
	// the game's name that takes the first one's place. Later than MergeWindow they stay two.
	[AvaloniaFact]
	public void GameDetected_RightAfterAutoOff_BecomesOneToast()
	{
		var h = new Harness();
		h.Settings.NotifyGame = true;
		h.TurnOn();
		h.Fake.RaiseGameUse(GameUse.TriggerEffect);
		Assert.Single(h.Raised, r => r.Type == NotificationType.MouseModeOff);
		h.Now += 500;
		List<string> log = WatchLog("toast: game detected for", () =>
		{
			h.Fake.Watcher.Exe = "Cyberpunk2077.exe";
			h.Fake.Games.Tick();
		});
		Assert.DoesNotContain(h.Raised, r => r.Type == NotificationType.GameDetected);
		AppNotification one = h.Raised.Last();
		Assert.Equal(NotificationType.MouseModeOff, one.Type);
		Assert.Equal("Cyberpunk 2077", one.Title);
		Assert.Equal(Text.DesktopOffGameBody(), one.Body);
		Assert.Equal(NotificationType.MouseModeOff, one.Replaces);
		Assert.Contains("toast: game detected for Cyberpunk 2077 shown with mouse mode off (one toast)", log);

		// Long after: two toasts.
		var later = new Harness();
		later.TurnOn();
		later.Fake.RaiseGameUse(GameUse.TriggerEffect);
		later.Now += (long)NotificationCenter.MergeWindow.TotalMilliseconds + 1;
		later.Fake.Watcher.Exe = "Cyberpunk2077.exe";
		later.Fake.Games.Tick();
		Assert.Single(later.Raised, r => r.Type == NotificationType.GameDetected);
		Assert.Single(later.Raised, r => r.Type == NotificationType.MouseModeOff);
	}

	// The toast follows the notification switches and limits, and says why it was not shown.
	[AvaloniaFact]
	public void AutoOffToast_IsHeldBack_ByTheSwitchesAndLimits()
	{
		var h = new Harness();
		h.Settings.NotificationsEnabled = false;
		h.TurnOn();
		h.Fake.RaiseGameUse(GameUse.Rumble);
		Assert.False(h.Settings.DesktopModeOn);
		Assert.Empty(h.Raised.Where(r => r.Type == NotificationType.MouseModeOff));

		h.Settings.NotificationsEnabled = true;
		h.Settings.QuietWhilePlaying = true;
		h.FullScreen = true;
		h.TurnOn();
		h.Fake.RaiseGameUse(GameUse.Rumble);
		Assert.Empty(h.Raised.Where(r => r.Type == NotificationType.MouseModeOff));

		h.Settings.QuietWhilePlaying = false;
		h.TurnOn();
		h.Fake.RaiseGameUse(GameUse.Rumble);
		Assert.Single(h.Raised, r => r.Type == NotificationType.MouseModeOff);
		// Within the toast cooldown: held back.
		h.TurnOn();
		h.Fake.RaiseGameUse(GameUse.Rumble);
		Assert.False(h.Settings.DesktopModeOn);
		Assert.Single(h.Raised, r => r.Type == NotificationType.MouseModeOff);
	}

	// The touchpad double press only ever turns mouse mode on, and says so in the log.
	[AvaloniaFact]
	public void TouchpadDoublePress_TurnsMouseModeOn_NeverOff()
	{
		var h = new Harness();
		List<string> log = WatchLog("(touchpad double press)", () =>
		{
			h.Fake.RaiseShortcut(ShortcutAction.TouchpadDoublePress);
			Assert.True(h.Settings.DesktopModeOn);
			h.Fake.RaiseShortcut(ShortcutAction.TouchpadDoublePress);
			Assert.True(h.Settings.DesktopModeOn);
		});
		Assert.Contains("mouse mode: on (touchpad double press)", log);
		Assert.DoesNotContain("mouse mode: off (touchpad double press)", log);
	}

	// The real service gives every host's shortcuts the facts the double press needs: the app's window in front and mouse
	// mode on (the host's own desktop mode).
	[Fact]
	public void RealService_PassesTheDoublePressFacts()
	{
		var controllers = new TestControllers();
		var host = new BridgeHost(controllers, new TestIdentity(true), new TestAttacher());
		using var service = new RealControllerService(host, new TestHiding(), controllers, a => a(), _ => "id", timers: false,
			games: new GameModeController(FakeControllerService.SampleCatalog, new FakeGameWatcher(null), null));
		bool app = false;
		service.IsAppInFront = () => app;
		Assert.False(host.Shortcuts.AppInFront!());
		app = true;
		Assert.True(host.Shortcuts.AppInFront!());
		// The controller page's Test or Remap tab: the user is trying buttons, the double press does nothing.
		service.ButtonPageShown = true;
		Assert.False(host.Shortcuts.AppInFront!());
		var fired = new List<ShortcutAction>();
		host.Shortcuts.Triggered += fired.Add;
		host.Shortcuts.Log = _ => { };
		void DoublePress()
		{
			foreach (bool down in new[] { true, false, true, false })
			{
				byte[] body = new byte[Kopru.Core.Protocol.InputState.BodySize];
				body[ShortcutProcessor.Buttons0] = 0x08;
				body[ShortcutProcessor.Buttons2] = down ? ShortcutProcessor.TouchpadBit : (byte)0;
				host.Shortcuts.Apply(body);
			}
		}
		DoublePress();
		Assert.DoesNotContain(ShortcutAction.TouchpadDoublePress, fired);
		service.ButtonPageShown = false;
		DoublePress();
		Assert.Contains(ShortcutAction.TouchpadDoublePress, fired);
		Assert.False(host.Shortcuts.MouseModeOn!());
		service.ApplySettings(new ControllerSettings { DesktopModeOn = true });
		Assert.True(host.Shortcuts.MouseModeOn!());
	}

	// The window tells the service when the controller page's Test or Remap tab is in view (the double press is off there).
	[AvaloniaFact]
	public void ButtonPage_FollowsTheControllerPagesTestAndRemapTabs()
	{
		var h = new Harness();
		Assert.False(h.Fake.ButtonPageShown);
		h.Vm.OpenController(ControllerPageViewModel.TestTab);
		Assert.True(h.Fake.ButtonPageShown);
		h.Vm.Controller.SelectedTab = ControllerPageViewModel.OutputsTab;
		Assert.False(h.Fake.ButtonPageShown);
		h.Vm.Controller.SelectedTab = ControllerPageViewModel.RemapTab;
		Assert.True(h.Fake.ButtonPageShown);
		h.Vm.Navigate(h.Vm.Overview);
		Assert.False(h.Fake.ButtonPageShown);
	}

	// Every change by the user is logged with where it came from; a bridge start with mouse mode on is logged too.
	[AvaloniaFact]
	public void ManualChanges_AreLogged_WithTheirSource()
	{
		var h = new Harness();
		var tray = new TrayViewModel(h.Fake, h.Settings, new NoShell());
		List<string> log = WatchLog("mouse mode: ", () =>
		{
			h.Settings.DesktopPanel.DesktopModeOn = true;
			h.Settings.HandleShortcut(ShortcutAction.ToggleDesktopMode);
			h.Vm.Overview.Quick.MouseModeOn = true;
			h.Vm.Overview.Quick.MouseModeOn = false;
			tray.ToggleDesktopModeCommand.Execute(null);
			_ = h.Fake.StartBridgeAsync();
			tray.ToggleDesktopModeCommand.Execute(null);
		});
		// Other tests log mouse mode lines in parallel: check order among ours.
		string[] expected =
		[
			"mouse mode: on (settings switch)",
			"mouse mode: off (shortcut)",
			"mouse mode: on (quick settings)",
			"mouse mode: off (quick settings)",
			"mouse mode: on (tray menu)",
			"mouse mode: on at start",
			"mouse mode: off (tray menu)",
		];
		int at = 0;
		foreach (string line in log)
		{
			if (at < expected.Length && line == expected[at])
			{
				at++;
			}
		}
		Assert.Equal(expected.Length, at);
		Assert.True(h.Settings.DesktopPanel.DesktopModeOn == h.Settings.DesktopModeOn);
	}
}
