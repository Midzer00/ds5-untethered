// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// A PS tap that brings the app's window to the front turns Mouse Mode on with one "Mouse Mode On" toast (SettingsState.OpenWithPs);
// already on: no second toast; every way back to the game turns it off again; Windows keeping another window in front or
// the controller's Test / Remap tab keeps it off; the touchpad double press works as before.
public class PsMouseModeAppTests
{
	private sealed class FakeWindow(Action? activated = null) : IPresentedWindow
	{
		public IntPtr Handle => 42;
		public bool IsMinimized => false;
		public bool Hidden { get; private set; }

		public void Show() => Hidden = false;

		public void Restore()
		{
		}

		// The real window raises Activated synchronously inside Activate.
		public void Activate() => activated?.Invoke();

		public void Hide() => Hidden = true;
	}

	private sealed class FakeForeground : IForegroundWindows
	{
		public IntPtr Foreground { get; set; } = 9;

		// null: Windows keeps the other window in front.
		public string? Result { get; set; } = "attach input";

		public bool Exists(IntPtr window) => window == 9;

		public string? BringToFront(IntPtr window)
		{
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

		public bool IsMainWindowInFront => presenter.IsInFront;

		public void Exit()
		{
		}
	}

	private sealed class Run
	{
		public long Now = 1_000_000;
		public FakeControllerService Fake { get; } = new(animate: false);
		public MainWindowViewModel Vm { get; }
		public FakeForeground Foreground { get; } = new();
		public MainWindowPresenter Presenter { get; }
		public FakeWindow Window { get; }
		public IAppShell Shell { get; }
		public List<AppNotification> Raised { get; } = [];
		public SettingsState Settings => Vm.Settings;

		// The fake starts with Cyberpunk 2077 running and in front; the Virtual Controller runs with the controller.
		public Run()
		{
			Vm = new MainWindowViewModel(Fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now }),
				Clock: () => Now));
			Vm.Sticks.StopPreview();
			_ = Fake.StartBridgeAsync();
			Fake.Tick();
			Window = new FakeWindow(() => Vm.BackToGame.SetWindowActive(true));
			Presenter = new MainWindowPresenter(Window, Foreground, _ => { });
			Shell = new PresenterShell(Presenter);
			_ = new TrayViewModel(Fake, Vm.Settings, Shell);
			Vm.BackToGame.Return = Presenter.ReturnTo;
			Vm.Notifications.Raised += Raised.Add;
		}

		public void Shortcut(ShortcutAction action)
		{
			Fake.RaiseShortcut(action);
			Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		}

		// The Mouse Mode toasts raised so far (on and off, from the controller).
		public List<AppNotification> MouseToasts() =>
			Raised.Where(r => r.Title == Text.DesktopOnTitle() || r.Title == Text.DesktopOffTitle() || r.Type == NotificationType.MouseModeOff).ToList();
	}

	[AvaloniaFact]
	public void PsOpensTheApp_MouseModeOn_WithOneToast()
	{
		var run = new Run();
		List<string> log = MouseModeAutoOffAppTests.WatchLog("mouse mode: on (ps opened the app)", () => run.Shortcut(ShortcutAction.OpenApp));

		Assert.True(run.Presenter.IsInFront);
		Assert.True(run.Settings.DesktopModeOn);
		Assert.True(run.Fake.Settings.DesktopModeOn);
		Assert.Single(log);
		AppNotification toast = Assert.Single(run.MouseToasts());
		Assert.Equal((NotificationType.Shortcut, Text.DesktopOnTitle(), Text.DesktopOnBody()), (toast.Type, toast.Title, toast.Body));
		Assert.Equal("Fare Modu Açık", Text.DesktopOnTitle(Language.Turkish));
		Assert.Equal("Mouse Mode On", Text.DesktopOnTitle(Language.English));
	}

	[AvaloniaFact]
	public void AlreadyOn_NoSecondToast()
	{
		// On from the app: PS opening the app neither toasts nor switches.
		var run = new Run();
		run.Settings.SetDesktopMode(true, "settings switch");
		run.Shortcut(ShortcutAction.OpenApp);
		Assert.True(run.Settings.DesktopModeOn);
		Assert.Empty(run.MouseToasts());

		// On from a PS tap: another PS open (the window had gone behind) and a touchpad double press add nothing.
		var again = new Run();
		again.Shortcut(ShortcutAction.OpenApp);
		again.Foreground.Foreground = 9;
		again.Shortcut(ShortcutAction.OpenApp);
		again.Shortcut(ShortcutAction.TouchpadDoublePress);
		Assert.True(again.Settings.DesktopModeOn);
		Assert.Single(again.MouseToasts());
	}

	// Every way back to the game turns it off (no toast of its own), and the next PS open turns it on again with one toast:
	// no on / off loop with the automatic switch-off.
	[AvaloniaFact]
	public void BackToTheGame_TurnsItOff_AndTheNextPsOpenOn()
	{
		var run = new Run();
		run.Shortcut(ShortcutAction.OpenApp);
		Assert.True(run.Settings.DesktopModeOn);

		// The PS tap from the app: hidden, game window back, Mouse Mode off.
		run.Shortcut(ShortcutAction.HideApp);
		Assert.True(run.Window.Hidden);
		Assert.Equal((IntPtr)9, run.Foreground.Foreground);
		Assert.False(run.Settings.DesktopModeOn);
		Assert.False(run.Fake.Settings.DesktopModeOn);
		Assert.Single(run.MouseToasts());

		// Again with PS, then the Back To Game button.
		run.Vm.BackToGame.SetWindowActive(false);
		run.Shortcut(ShortcutAction.OpenApp);
		Assert.True(run.Settings.DesktopModeOn);
		Assert.Equal(2, run.MouseToasts().Count);
		run.Vm.BackToGame.BackCommand.Execute(null);
		Assert.True(run.Window.Hidden);
		Assert.False(run.Settings.DesktopModeOn);
		Assert.Equal(2, run.MouseToasts().Count);

		// While the app is in front, the game using the controller (past the grace time) does not fight it: the app in
		// front is never a possible game.
		run.Vm.BackToGame.SetWindowActive(false);
		run.Shortcut(ShortcutAction.OpenApp);
		run.Now += (long)SettingsState.GameUseGrace.TotalMilliseconds + 1000;
		run.Fake.SetForeground(null);
		run.Fake.RaiseGameUse(GameUse.TriggerEffect);
		Avalonia.Threading.Dispatcher.UIThread.RunJobs();
		Assert.True(run.Settings.DesktopModeOn);
		Assert.DoesNotContain(run.Raised, r => r.Type == NotificationType.MouseModeOff);
		Assert.Equal(3, run.MouseToasts().Count);
	}

	// Windows kept the game in front (the window only flashed on the taskbar): the game keeps the controller. The controller
	// page's Test or Remap tab: the user is trying the buttons there.
	[AvaloniaFact]
	public void StaysOff_WhenNotInFront_OrOnTheButtonPage()
	{
		var refused = new Run();
		refused.Foreground.Result = null;
		List<string> log = MouseModeAutoOffAppTests.WatchLog("mouse mode: stays off, PS opened the app but Windows kept another window in front",
			() => refused.Shortcut(ShortcutAction.OpenApp));
		Assert.Single(log);
		Assert.False(refused.Settings.DesktopModeOn);
		Assert.Empty(refused.MouseToasts());

		var buttons = new Run();
		buttons.Fake.ButtonPageShown = true;
		buttons.Shortcut(ShortcutAction.OpenApp);
		Assert.True(buttons.Presenter.IsInFront);
		Assert.False(buttons.Settings.DesktopModeOn);
		Assert.Empty(buttons.MouseToasts());
	}

	// Opened another way (the tray icon): Mouse Mode stays off and the touchpad double press turns it on as before, with
	// its one toast.
	[AvaloniaFact]
	public void OpenedFromTheTray_TouchpadDoublePressStillWorks()
	{
		var run = new Run();
		run.Shell.ShowMainWindow();
		Assert.True(run.Presenter.IsInFront);
		Assert.False(run.Settings.DesktopModeOn);
		run.Shortcut(ShortcutAction.TouchpadDoublePress);
		Assert.True(run.Settings.DesktopModeOn);
		Assert.Single(run.MouseToasts());
	}

	[Fact]
	public void Texts_MentionThePsOpen()
	{
		Assert.Contains("PS ile uygulamayı açınca", Text.DesktopModeHint(Language.Turkish));
		Assert.Contains("opening the app with PS", Text.DesktopModeHint(Language.English));
		Assert.Contains("Fare Modu açık", Text.PsOpenAppHint(Language.Turkish));
		Assert.Contains("with Mouse Mode on", Text.PsOpenAppHint(Language.English));
	}
}
