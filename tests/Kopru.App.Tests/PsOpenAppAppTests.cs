// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// "PS: Open The App" in the app: the setting (saved, pushed, under the shortcuts switch), the window shown through the
// shell, and where the real service routes a PS tap.
public class PsOpenAppAppTests
{
	private sealed class FakeShell : IAppShell
	{
		public List<AppPage?> Shown { get; } = [];

		public void ShowMainWindow(AppPage? page = null) => Shown.Add(page);

		public void Exit()
		{
		}
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
	public void OpenAppShortcut_ShowsTheWindow_OnItsCurrentPage()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		var shell = new FakeShell();
		_ = new TrayViewModel(fake, vm.Settings, shell);
		fake.RaiseShortcut(ShortcutAction.NextProfile);
		Assert.Empty(shell.Shown);
		fake.RaiseShortcut(ShortcutAction.OpenApp);
		Assert.Equal([null], shell.Shown);
	}

	[Fact]
	public void RealService_RoutesTheTap_ByTheSettingTheWindowAndTheGame()
	{
		var controllers = new TestControllers();
		using var service = new RealControllerService(new BridgeHost(controllers, new TestIdentity(true), new TestAttacher()), new TestHiding(),
			controllers, a => a(), _ => "id", timers: false);
		bool appInFront = false, fullScreen = false;
		service.IsAppInFront = () => appInFront;
		service.IsForegroundFullScreen = () => fullScreen;
		service.ApplySettings(new ControllerSettings());

		// The bridge's shortcut processor asks the service.
		var host = (BridgeHost)typeof(RealControllerService).GetField("host", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(service)!;
		Assert.Equal(PsTapRoute.OpenApp, host.Shortcuts.TapRoute!());

		appInFront = true;
		Assert.Equal(PsTapRoute.Nothing, service.RoutePsTap());
		appInFront = false;
		fullScreen = true;
		Assert.Equal(PsTapRoute.Game, service.RoutePsTap());
		fullScreen = false;
		service.ApplySettings(new ControllerSettings { PsOpensApp = false });
		Assert.Equal(PsTapRoute.Game, service.RoutePsTap());
	}

	[Fact]
	public void Texts_AreTranslated()
	{
		Assert.NotEqual(Text.PsOpenApp(Language.Turkish), Text.PsOpenApp(Language.English));
		Assert.Equal("PS: Uygulamayı Aç", Text.PsOpenApp(Language.Turkish));
		Assert.Contains("DS5 Untethered", Text.PsOpenAppHint(Language.Turkish));
	}
}
