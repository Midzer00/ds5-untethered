// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// Desktop mode (Settings group, PS + Options, tray, auto-off on a known game, toasts) and the gyro mouse target.
public class DesktopModeAppTests
{
	private static (MainWindowViewModel Vm, FakeControllerService Fake) NewViewModel(ISettingsStore? store = null)
	{
		var fake = new FakeControllerService(ControllerKind.DualSense, animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(store));
		vm.Sticks.StopPreview();
		return (vm, fake);
	}

	private sealed class NoShell : IAppShell
	{
		public void ShowMainWindow(AppPage? page = null) { }

		public void Exit() { }
	}

	[AvaloniaFact]
	public void Switch_Shortcut_AndTray_ReachTheService_WithToasts()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		var toasts = new List<string>();
		vm.Settings.ShortcutFeedback += (title, _) => toasts.Add(title);
		Assert.False(fake.Settings.DesktopModeOn);

		vm.Settings.DesktopPanel.Settings.DesktopModeOn = true;
		Assert.True(fake.Settings.DesktopModeOn);
		Assert.Equal([Text.DesktopOnTitle()], toasts);

		vm.Settings.HandleShortcut(ShortcutAction.ToggleDesktopMode);
		Assert.False(fake.Settings.DesktopModeOn);
		Assert.Equal(Text.DesktopOffTitle(), toasts[^1]);

		var tray = new TrayViewModel(fake, vm.Settings, new NoShell());
		Assert.False(tray.IsDesktopModeOn);
		Assert.Equal(Text.TrayDesktopMode(), tray.DesktopModeText);
		tray.ToggleDesktopModeCommand.Execute(null);
		Assert.True(tray.IsDesktopModeOn);
		Assert.True(fake.Settings.DesktopModeOn);
		Assert.Equal(3, toasts.Count);
	}

	[AvaloniaFact]
	public void KnownGame_TurnsDesktopModeOff_WhenTheOptionIsOn()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		// Turned on with the game already in front: stays on.
		vm.Settings.DesktopModeOn = true;
		fake.Games.Tick();
		vm.Settings.CheckDesktopAutoOff();
		Assert.True(vm.Settings.DesktopModeOn);

		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Games.Tick();
		Assert.True(vm.Settings.DesktopModeOn);

		var bodies = new List<string>();
		vm.Settings.ShortcutFeedback += (_, body) => bodies.Add(body);
		var lines = new List<string>();
		void Watch(string line)
		{
			if (line.StartsWith("mouse mode: switched off automatically", StringComparison.Ordinal))
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
			fake.Watcher.Exe = "Cyberpunk2077.exe";
			fake.Games.Tick();
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= Watch;
		}
		lock (lines)
		{
			Assert.Contains("mouse mode: switched off automatically, Cyberpunk 2077 came to the front (Cyberpunk2077.exe)", lines);
		}
		Assert.False(vm.Settings.DesktopModeOn);
		Assert.False(fake.Settings.DesktopModeOn);
		Assert.Single(bodies);
		Assert.Contains("Cyberpunk2077", bodies[0]);

		// Option off: the game coming back leaves it on.
		vm.Settings.DesktopPanel.AutoOffInGame = false;
		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Games.Tick();
		vm.Settings.DesktopModeOn = true;
		fake.Watcher.Exe = "Cyberpunk2077.exe";
		fake.Games.Tick();
		Assert.True(vm.Settings.DesktopModeOn);
	}

	[AvaloniaFact]
	public void Panel_SlidersAndLegend()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		DesktopModeViewModel panel = vm.Settings.DesktopPanel;
		Assert.True(panel.AutoOffInGame);
		panel.PointerSpeed = 1.84;
		panel.PointerAcceleration = 72.4;
		panel.TouchpadSpeed = 0.5;
		panel.ScrollSpeed = 2;
		DesktopModeSettings d = fake.Settings.Desktop;
		Assert.Equal(1.8, d.PointerSpeed);
		Assert.Equal(72, d.PointerAcceleration);
		Assert.Equal(0.5, d.TouchpadSpeed);
		Assert.Equal(2, d.ScrollSpeed);
		Assert.Equal(Text.MultiplierValue(1.8), panel.PointerSpeedText);
		Assert.Equal(Text.PercentValue(72), panel.PointerAccelerationText);
		Assert.Equal(DesktopButtonMap.Buttons.Count, panel.Rows.Count);
		Assert.Contains(vm.Settings.ShortcutsPanel.Legend, l => l.Keys == Text.ShortcutKeysDesktop());
	}

	[AvaloniaFact]
	public void PointerStick_AndButtonMapping_ReachTheService()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		DesktopModeViewModel panel = vm.Settings.DesktopPanel;
		Assert.Equal([Text.DesktopStickLeft(), Text.DesktopStickRight()], panel.PointerSticks);
		IReadOnlyList<string> sticks = panel.PointerSticks;
		Assert.Equal(1, panel.PointerStickIndex);
		panel.PointerStickIndex = 0;
		Assert.Equal(PointerStick.Left, fake.Settings.Desktop.PointerStick);
		Assert.Equal(0, panel.PointerStickIndex);
		panel.PointerStickIndex = 5;
		Assert.Equal(PointerStick.Left, vm.Settings.DesktopMode.PointerStick);
		Assert.Same(sticks, panel.PointerSticks);

		// Rows: every remappable button, today's actions selected, every action offered.
		Assert.Equal(DesktopButtonMap.Buttons, panel.Rows.Select(r => r.Button));
		Assert.All(panel.Rows, r => Assert.Equal(DesktopButtonMap.DefaultActionOf(r.Button), r.Selected!.Action));
		Assert.Equal(Text.ButtonName(DsButton.Cross), panel.Rows[0].Name);
		Assert.Equal(Enum.GetValues<MouseAction>().Length, panel.Actions.Count);
		Assert.True(panel.IsMappingDefault);

		DesktopButtonRow cross = panel.Rows.Single(r => r.Button == DsButton.Cross);
		var changes = new List<string?>();
		cross.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
		cross.Selected = panel.Actions.Single(a => a.Action == MouseAction.Copy);
		Assert.Equal(MouseAction.Copy, fake.Settings.Desktop.Buttons.ActionOf(DsButton.Cross));
		Assert.Equal(MouseAction.Copy, cross.Selected!.Action);
		Assert.Equal(Text.DesktopActionName(MouseAction.Copy), cross.Selected.Name);
		Assert.NotEmpty(changes);
		Assert.False(panel.IsMappingDefault);
		cross.Selected = null;
		Assert.Equal(MouseAction.Copy, fake.Settings.Desktop.Buttons.ActionOf(DsButton.Cross));

		// Reset: the mapping only (the stick stays).
		panel.ResetMappingCommand.Execute(null);
		Assert.Equal(DesktopButtonMap.Default, fake.Settings.Desktop.Buttons);
		Assert.Equal(MouseAction.LeftClick, cross.Selected!.Action);
		Assert.Equal(PointerStick.Left, fake.Settings.Desktop.PointerStick);
		Assert.True(panel.IsMappingDefault);
	}

	[AvaloniaFact]
	public void GyroTarget_ReachesTheService()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		RemapViewModel page = vm.Sticks.Remap;
		Assert.Equal(GyroTarget.RightStick, page.SelectedGyroTarget!.Target);
		page.SelectedGyroTarget = page.GyroTargets.Single(t => t.Target == GyroTarget.Mouse);
		Assert.Equal(GyroTarget.Mouse, fake.Settings.Gyro.Target);
		Assert.Equal(GyroTarget.Mouse, vm.Settings.CaptureValues().Gyro!.Target);
		Assert.Equal(Text.GyroTargetName(GyroTarget.Mouse), page.SelectedGyroTarget!.Name);
	}

	[AvaloniaFact]
	public void Settings_AndProfile_RoundTrip()
	{
		var store = new MemorySettingsStore();
		(MainWindowViewModel vm, _) = NewViewModel(store);
		var desktop = new DesktopModeSettings
		{
			PointerSpeed = 2.2, PointerAcceleration = 10, TouchpadSpeed = 1.5, ScrollSpeed = 0.6, AutoOffInGame = false, PointerStick = PointerStick.Left,
			Buttons = DesktopButtonMap.Default.With(DsButton.Square, MouseAction.Space).With(DsButton.L2, MouseAction.None)
		};
		vm.Settings.DesktopMode = desktop;
		vm.Settings.DesktopModeOn = true;
		vm.Settings.Gyro = vm.Settings.Gyro with { Mode = GyroMode.Always, Target = GyroTarget.Mouse };
		vm.Settings.SaveNow();
		Assert.Equal(desktop, store.Saved!.DesktopMode);

		// Through JSON, like settings.json.
		string dir = Path.Combine(Path.GetTempPath(), "kopru-desktop-" + Guid.NewGuid().ToString("N"));
		try
		{
			var file = new JsonSettingsStore(Path.Combine(dir, "settings.json"));
			file.Save(store.Saved);
			(MainWindowViewModel again, FakeControllerService fake) = NewViewModel(file);
			Assert.Equal(desktop, again.Settings.DesktopMode);
			Assert.False(again.Settings.DesktopModeOn);
			Assert.Equal(GyroTarget.Mouse, again.Settings.Gyro.Target);
			Assert.Equal(GyroTarget.Mouse, again.Settings.Profiles.Active.Values.Gyro!.Target);
			Assert.Equal(desktop, fake.Settings.Desktop);

			// An older file without the new fields: the defaults.
			File.WriteAllText(file.Path, "{\"Gyro\":{\"Mode\":\"Always\"}}");
			(MainWindowViewModel old, _) = NewViewModel(file);
			Assert.Equal(DesktopModeSettings.Default, old.Settings.DesktopMode);
			Assert.Equal(GyroTarget.RightStick, old.Settings.Gyro.Target);

			// A file from before the stick and the mapping: their defaults, the rest kept; a bad action falls back alone.
			File.WriteAllText(file.Path, "{\"DesktopMode\":{\"PointerSpeed\":2.5,\"PointerStick\":\"Diagonal\",\"Buttons\":{\"Cross\":\"Teleport\",\"Circle\":\"Paste\"}},\"Brightness\":40}");
			(MainWindowViewModel older, _) = NewViewModel(file);
			Assert.Equal(DesktopModeSettings.Default with
			{
				PointerSpeed = 2.5, Buttons = DesktopButtonMap.Default.With(DsButton.Circle, MouseAction.Paste)
			}, older.Settings.DesktopMode);
			Assert.Equal(40, older.Settings.Brightness);
		}
		finally
		{
			if (Directory.Exists(dir))
			{
				Directory.Delete(dir, true);
			}
		}
	}

	// Renders the Settings page's Mouse Mode tab with mouse mode on; PNGs go to docs/screens only with KOPRU_RENDER_SCREENS=1.
	[AvaloniaFact]
	public void RenderDesktopModeGroup()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string outDir = Path.Combine(RepoRoot(), "docs", "screens");
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				(MainWindowViewModel vm, _) = NewViewModel();
				vm.Settings.DesktopModeOn = true;
				vm.OpenSettingsAt(SettingsViewModel.MouseTab);
				var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
				window.Show();
				Dispatcher.UIThread.RunJobs();
				AvaloniaHeadlessPlatform.ForceRenderTimerTick();
				Border panel = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DesktopModeGroup");
				Assert.True(panel.IsEffectivelyVisible);
				Thread.Sleep(320);
				Dispatcher.UIThread.RunJobs();
				AvaloniaHeadlessPlatform.ForceRenderTimerTick();
				var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
				Assert.Contains(Text.DesktopModeSection(language), texts);
				Assert.Contains(Text.DesktopPointerSpeed(language), texts);
				using var frame = window.CaptureRenderedFrame();
				Assert.NotNull(frame);
				if (writeFiles)
				{
					frame!.Save(Path.Combine(outDir, $"desktop-mode-{code}.png"));
				}
				window.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static string RepoRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		return dir ?? throw new InvalidOperationException("Kopru.sln not found");
	}
}
