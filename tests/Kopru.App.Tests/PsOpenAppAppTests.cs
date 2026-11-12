// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// "PS: Open The App" in the app: the setting (saved, pushed, under the shortcuts switch), the window shown and hidden
// through the shell, where the real service routes a PS tap, and the presenter that puts the window in front.
public class PsOpenAppAppTests
{
	private sealed class FakeShell : IAppShell
	{
		public List<AppPage?> Shown { get; } = [];

		public int Hidden { get; private set; }

		public void ShowMainWindow(AppPage? page = null) => Shown.Add(page);

		public void HideMainWindow() => Hidden++;

		public void Exit()
		{
		}
	}

	private sealed class FakeWindow : IPresentedWindow
	{
		public List<string> Calls { get; } = [];
		public IntPtr Handle { get; set; } = 42;
		public bool IsMinimized { get; set; }

		public void Show() => Calls.Add("show");

		public void Restore()
		{
			Calls.Add("restore");
			IsMinimized = false;
		}

		public void Activate() => Calls.Add("activate");

		public void Hide() => Calls.Add("hide");
	}

	private sealed class FakeForeground : IForegroundWindows
	{
		public IntPtr Foreground { get; set; } = 7;

		// What BringToFront manages: null = Windows keeps the other window in front.
		public string? Result { get; set; } = "attach input";

		public List<IntPtr> Asked { get; } = [];

		// The windows that still exist (besides ours): only these are put back in front on hide.
		public HashSet<IntPtr> Alive { get; } = [];

		public bool Exists(IntPtr window) => Alive.Contains(window);

		public string? BringToFront(IntPtr window)
		{
			Asked.Add(window);
			if (Result != null)
			{
				Foreground = window;
			}
			return Result;
		}
	}

	private sealed class PresenterShell(MainWindowPresenter presenter) : IAppShell
	{
		public void ShowMainWindow(AppPage? page = null) => presenter.Show("open");

		public void HideMainWindow() => presenter.Hide("PS tap");

		public void Exit()
		{
		}
	}

	private static BridgeHost HostOf(RealControllerService service) =>
		(BridgeHost)typeof(RealControllerService).GetField("host", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service)!;

	// A short PS tap through the processor (real time, well under OpenAppMaxHold); true when the game saw PS.
	private static bool Tap(ShortcutProcessor shortcuts)
	{
		static byte[] Body(bool ps)
		{
			var b = new byte[Kopru.Core.Protocol.InputState.BodySize];
			b[ShortcutProcessor.Buttons0] = ShortcutProcessor.HatNone;
			b[ShortcutProcessor.Buttons2] = (byte)(ps ? ShortcutProcessor.PsBit : 0);
			return b;
		}
		byte[] down = Body(true);
		shortcuts.Apply(down);
		byte[] up = Body(false);
		shortcuts.Apply(up);
		return ((down[ShortcutProcessor.Buttons2] | up[ShortcutProcessor.Buttons2]) & ShortcutProcessor.PsBit) != 0;
	}

	[AvaloniaFact]
	public void Setting_IsOnByDefault_PushedAndSaved()
	{
		var store = new MemorySettingsStore();
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake, store);
		Assert.True(state.PsOpensApp);
		Assert.True(fake.Settings.PsOpensApp);
		Assert.True(new AppSettings().PsOpensApp);

		state.PsOpensApp = false;
		Assert.False(fake.Settings.PsOpensApp);
		state.SaveNow();
		Assert.False(store.Saved!.PsOpensApp);
		Assert.False(new SettingsState(new FakeControllerService(animate: false), store).PsOpensApp);
	}

	[AvaloniaFact]
	public void OpenAppShortcut_ShowsTheWindow_OnItsCurrentPage_AndHideAppHidesIt()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		var shell = new FakeShell();
		_ = new TrayViewModel(fake, vm.Settings, shell);
		fake.RaiseShortcut(ShortcutAction.NextProfile);
		Assert.Empty(shell.Shown);
		fake.RaiseShortcut(ShortcutAction.OpenApp);
		Assert.Equal([null], shell.Shown);
		Assert.Equal(0, shell.Hidden);
		fake.RaiseShortcut(ShortcutAction.HideApp);
		Assert.Equal(1, shell.Hidden);
		Assert.Single(shell.Shown);
	}

	[Fact]
	public void RealService_RoutesTheTap_ByTheSettingTheWindowAndTheGame()
	{
		var controllers = new TestControllers();
		using var service = new RealControllerService(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher()), new TestHiding(),
			controllers, a => a(), _ => "id", timers: false);
		bool appInFront = false;
		string? fullScreen = null;
		int fullScreenAsked = 0;
		service.IsAppInFront = () => appInFront;
		service.FullScreenForeground = () =>
		{
			fullScreenAsked++;
			return fullScreen;
		};
		service.ApplySettings(new ControllerSettings());

		// The bridge's shortcut processor asks the service.
		Assert.Equal(new PsTapDecision(PsTapRoute.OpenApp, "no game in front"), HostOf(service).Shortcuts.TapRoute!());

		// Our window in front: hidden again; the full-screen check is not asked about our own window.
		appInFront = true;
		fullScreenAsked = 0;
		Assert.Equal(new PsTapDecision(PsTapRoute.HideApp, "app in front"), service.RoutePsTap());
		Assert.Equal(0, fullScreenAsked);
		appInFront = false;
		fullScreen = "player.exe";
		Assert.Equal(new PsTapDecision(PsTapRoute.OpenApp, "game in front, opens anyway: player.exe"), service.RoutePsTap());
		service.ApplySettings(new ControllerSettings { PsOpensAppInGames = false });
		Assert.Equal(new PsTapDecision(PsTapRoute.Game, "game in front: player.exe"), service.RoutePsTap());
		fullScreen = null;
		service.ApplySettings(new ControllerSettings { PsOpensApp = false });
		Assert.Equal(new PsTapDecision(PsTapRoute.Game, "setting off"), service.RoutePsTap());
	}

	// Shown from the background: restored from minimised, activated, then pushed past the foreground lock; hidden like
	// closing the window. "In front" is the real foreground window, not a remembered Activated flag.
	[Fact]
	public void Presenter_ShowsInFront_AndHides()
	{
		var window = new FakeWindow { IsMinimized = true };
		var foreground = new FakeForeground();
		List<string> log = [];
		var presenter = new MainWindowPresenter(window, foreground, log.Add);
		Assert.False(presenter.IsInFront);

		presenter.Show("open");
		Assert.Equal(["show", "restore", "activate"], window.Calls);
		Assert.Equal([(IntPtr)42], foreground.Asked);
		Assert.True(presenter.IsInFront);
		Assert.Equal("app window: shown (open), in front (attach input)", log.Last());

		// Another window comes to the front (alt-tab): no longer in front.
		foreground.Foreground = 9;
		Assert.False(presenter.IsInFront);

		presenter.Hide("PS tap");
		Assert.Equal("hide", window.Calls.Last());
		Assert.Equal("app window: hidden (PS tap)", log.Last());

		// Windows refuses: logged, so a hardware try shows it.
		foreground.Result = null;
		presenter.Show("open");
		Assert.False(presenter.IsInFront);
		Assert.Equal("app window: shown (open), not in front: Windows kept another window in front", log.Last());
	}

	// The whole path on the real service: a PS tap with no game shows the window through the shell; with the window in
	// front the next tap hides it; with a game in front neither happens and the tap reaches the game.
	[AvaloniaFact]
	public void PsTaps_ShowAndHide_ThroughTheRealService()
	{
		var controllers = new TestControllers();
		using var service = new RealControllerService(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher()), new TestHiding(),
			controllers, a => a(), _ => "id", timers: false);
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
		var window = new FakeWindow();
		var foreground = new FakeForeground();
		var presenter = new MainWindowPresenter(window, foreground, _ => { });
		_ = new TrayViewModel(service, vm.Settings, new PresenterShell(presenter));
		string? game = null;
		service.IsAppInFront = () => presenter.IsInFront;
		service.FullScreenForeground = () => game;
		service.ApplySettings(new ControllerSettings { PsOpensAppInGames = false });
		List<string> log = [];
		ShortcutProcessor shortcuts = HostOf(service).Shortcuts;
		shortcuts.Log = log.Add;

		Assert.False(Tap(shortcuts));
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		Assert.Equal(["show", "activate"], window.Calls);
		Assert.True(presenter.IsInFront);

		Assert.False(Tap(shortcuts));
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		Assert.Equal("hide", window.Calls.Last());

		foreground.Foreground = 9;
		game = "game.exe";
		int calls = window.Calls.Count;
		Assert.True(Tap(shortcuts));
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		Assert.Equal(calls, window.Calls.Count);

		Assert.Equal(3, log.Count);
		Assert.EndsWith("-> OpenApp (no game in front)", log[0]);
		Assert.EndsWith("-> HideApp (app in front)", log[1]);
		Assert.EndsWith("-> Game (game in front: game.exe)", log[2]);
	}

	// "Also In Games" on (the default): a tap over a full-screen game opens the app and does not reach the game; the next
	// tap hides it and puts the game window back in front, and that tap does not reach the game either.
	[AvaloniaFact]
	public void PsTaps_AlsoInGames_OpenOverTheGame_AndHideBackToIt()
	{
		var controllers = new TestControllers();
		using var service = new RealControllerService(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher()), new TestHiding(),
			controllers, a => a(), _ => "id", timers: false);
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
		var window = new FakeWindow();
		var foreground = new FakeForeground { Foreground = 9 };
		foreground.Alive.Add(9);
		var presenter = new MainWindowPresenter(window, foreground, _ => { });
		_ = new TrayViewModel(service, vm.Settings, new PresenterShell(presenter));
		service.IsAppInFront = () => presenter.IsInFront;
		service.FullScreenForeground = () => foreground.Foreground == 9 ? "game.exe" : null;
		service.ApplySettings(new ControllerSettings());
		List<string> log = [];
		ShortcutProcessor shortcuts = HostOf(service).Shortcuts;
		shortcuts.Log = log.Add;

		Assert.False(Tap(shortcuts));
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		Assert.True(presenter.IsInFront);
		Assert.False(Tap(shortcuts));
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		Assert.Equal("hide", window.Calls.Last());
		Assert.Equal((IntPtr)9, foreground.Foreground);
		Assert.Equal([(IntPtr)42, (IntPtr)9], foreground.Asked);
		Assert.EndsWith("-> OpenApp (game in front, opens anyway: game.exe)", log[0]);
		Assert.EndsWith("-> HideApp (app in front)", log[1]);
	}

	// Hide puts back the window that was in front when the app was shown, once, and only while it still exists.
	[Fact]
	public void Presenter_Hide_PutsThePreviousWindowBack()
	{
		var window = new FakeWindow();
		var foreground = new FakeForeground { Foreground = 9 };
		foreground.Alive.Add(9);
		List<string> log = [];
		var presenter = new MainWindowPresenter(window, foreground, log.Add);

		presenter.Show("open");
		presenter.Show("open");
		presenter.Hide("PS tap");
		Assert.Equal((IntPtr)9, foreground.Foreground);
		Assert.Equal("app window: hidden (PS tap), previous window back in front (attach input)", log.Last());

		// Remembered once: a second hide does not bring it up again.
		foreground.Asked.Clear();
		presenter.Hide("PS tap");
		Assert.Empty(foreground.Asked);

		// The game closed meanwhile: nothing to put back.
		presenter.Show("open");
		foreground.Alive.Clear();
		foreground.Asked.Clear();
		presenter.Hide("PS tap");
		Assert.Empty(foreground.Asked);
		Assert.Equal("app window: hidden (PS tap)", log.Last());
	}

	[AvaloniaFact]
	public void AlsoInGames_IsOnByDefault_SavedPushed_AndNeedsBothSwitches()
	{
		var store = new MemorySettingsStore();
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake, store);
		Assert.True(state.PsOpensAppInGames);
		Assert.True(fake.Settings.PsOpensAppInGames);
		Assert.True(new AppSettings().PsOpensAppInGames);
		Assert.True(state.PsOpensAppInGamesAvailable);

		state.PsOpensAppInGames = false;
		Assert.False(fake.Settings.PsOpensAppInGames);
		state.SaveNow();
		Assert.False(store.Saved!.PsOpensAppInGames);
		Assert.False(new SettingsState(new FakeControllerService(animate: false), store).PsOpensAppInGames);

		state.PsOpensApp = false;
		Assert.False(state.PsOpensAppInGamesAvailable);
		state.PsOpensApp = true;
		state.ShortcutsEnabled = false;
		Assert.False(state.PsOpensAppInGamesAvailable);

		// An older settings file without the field loads with it on.
		AppSettings old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"PsOpensApp\": true}")!;
		Assert.True(old.PsOpensAppInGames);
	}

	[Fact]
	public void Texts_AreTranslated()
	{
		Assert.Equal("Oyunda Da Aç", Text.PsOpenAppInGames(Language.Turkish));
		Assert.Equal("Also In Games", Text.PsOpenAppInGames(Language.English));
		Assert.NotEqual(Text.PsOpenAppInGamesHint(Language.Turkish), Text.PsOpenAppInGamesHint(Language.English));
		Assert.NotEqual(Text.PsOpenApp(Language.Turkish), Text.PsOpenApp(Language.English));
		Assert.Equal("PS: Uygulamayı Aç", Text.PsOpenApp(Language.Turkish));
		Assert.Contains("DS5 Untethered", Text.PsOpenAppHint(Language.Turkish));
	}
}
