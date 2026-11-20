// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Bridge;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The remap & gyro tab of the sticks page: settings reach the service and the store, live gyro offset, calibration.
public class RemapAppTests
{
	private static (MainWindowViewModel Vm, FakeControllerService Fake) NewViewModel(ISettingsStore? store = null)
	{
		var fake = new FakeControllerService(ControllerKind.DualSense, animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(store));
		vm.Sticks.StopPreview();
		return (vm, fake);
	}

	private static byte[] GyroBody(short pitch, short yaw, byte buttons1 = 0)
	{
		var b = new byte[63];
		b[0] = b[1] = b[2] = b[3] = 128;
		b[7] = 8;
		b[8] = buttons1;
		BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(GyroAim.GyroX), pitch);
		BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(GyroAim.GyroY), yaw);
		return b;
	}

	[AvaloniaFact]
	public void RemapRows_And_StickToggles_ReachTheService()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		RemapViewModel page = vm.Sticks.Remap;
		Assert.Equal(ButtonRemap.Physical.Count, page.Rows.Count);
		Assert.DoesNotContain(page.Rows, r => r.Targets.Any(t => t.Name == "PS"));

		RemapRow cross = page.Rows.Single(r => r.Physical == DsButton.Cross);
		Assert.Equal(DsButton.Cross, cross.Selected!.Button);
		cross.Selected = cross.Targets.Single(t => t.Button == DsButton.None);
		page.Rows.Single(r => r.Physical == DsButton.L1).Selected = cross.Targets.Single(t => t.Button == DsButton.R1);
		page.SwapSticks = true;
		page.InvertRightY = true;

		RemapSettings r = fake.Settings.Remap;
		Assert.Equal(DsButton.None, r.TargetOf(DsButton.Cross));
		Assert.Equal(DsButton.R1, r.TargetOf(DsButton.L1));
		Assert.True(r.SwapSticks && r.InvertRightY && !r.InvertLeftX);
		Assert.True(cross.IsChanged);

		page.ResetRemapCommand.Execute(null);
		Assert.True(fake.Settings.Remap.IsIdentity);
		Assert.Equal(DsButton.Cross, cross.Selected!.Button);
		Assert.False(page.SwapSticks);
	}

	[AvaloniaFact]
	public void GyroSettings_ReachTheService_AndResetKeepsTheCalibration()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		RemapViewModel page = vm.Sticks.Remap;
		Assert.False(page.IsGyroOn);
		Assert.False(page.HasActivationButton);

		page.SelectedMode = page.Modes.Single(m => m.Mode == GyroMode.WhileL2);
		page.Toggle = true;
		page.SensitivityX = 2.46;
		page.SensitivityY = 0.5;
		page.InvertX = true;
		page.Smoothing = 40;
		page.Deadzone = 2.5;
		Assert.True(page.IsGyroOn && page.HasActivationButton);

		GyroSettings g = fake.Settings.Gyro;
		Assert.Equal(GyroMode.WhileL2, g.Mode);
		Assert.True(g.Toggle && g.InvertX && !g.InvertY);
		Assert.Equal(2.5, g.SensitivityX);
		Assert.Equal(0.5, g.SensitivityY);
		Assert.Equal(40, g.Smoothing);
		Assert.Equal(2.5, g.Deadzone);
		Assert.Equal(Text.MultiplierValue(2.5), page.SensitivityXText);
		Assert.Equal(Text.DegreesPerSecond(2.5), page.DeadzoneText);

		vm.Settings.Gyro = vm.Settings.Gyro with { BiasX = 7 };
		page.ResetGyroCommand.Execute(null);
		Assert.Equal(GyroSettings.Default with { BiasX = 7 }, fake.Settings.Gyro);
	}

	[AvaloniaFact]
	public void LiveIndicator_ShowsTheGyroOffset()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		RemapViewModel page = vm.Sticks.Remap;
		fake.RawInput = [128, 128, 128, 128, 0, 0, 0, 8, 0];
		vm.Sticks.RefreshInput();
		Assert.False(page.IsLive);
		Assert.Equal(Text.LivePreviewNeedsBridge(), page.OffsetHint);

		fake.RawInput = GyroBody(0, 1000);
		vm.Sticks.RefreshInput();
		Assert.True(page.IsLive);
		Assert.Equal(0, page.OffsetX);

		page.SelectedMode = page.Modes.Single(m => m.Mode == GyroMode.WhileR1);
		vm.Sticks.RefreshInput();
		Assert.Equal(0, page.OffsetX);
		fake.RawInput = GyroBody(0, 1000, buttons1: 0x02);
		vm.Sticks.RefreshInput();
		Assert.True(page.OffsetX < -0.3);
		Assert.Equal(Text.GyroOffsetHint(), page.OffsetHint);
	}

	[AvaloniaFact]
	public async Task GyroCalibration_StoresTheBias_OrReportsWhyNot()
	{
		var fake = new FakeControllerService(ControllerKind.DualSense, animate: false);
		var settings = new SettingsState(fake);
		var page = new RemapViewModel(fake, settings, TimeSpan.FromMilliseconds(700));

		fake.RawInput = GyroBody(12, -30);
		await page.CalibrateCommand.ExecuteAsync(null);
		Assert.Equal(Text.GyroCalibrationResultText(GyroCalibrationStatus.Success), page.CalibrationText);
		Assert.Equal(12, settings.Gyro.BiasX);
		Assert.Equal(-30, settings.Gyro.BiasY);
		Assert.Equal(100, page.CalibrationProgress);

		fake.RawInput = null;
		await page.CalibrateCommand.ExecuteAsync(null);
		Assert.Equal(Text.GyroCalibrationResultText(GyroCalibrationStatus.NotEnoughSamples), page.CalibrationText);
		Assert.Equal(12, settings.Gyro.BiasX);
	}

	[AvaloniaFact]
	public void Settings_AreSaved_AndLoaded()
	{
		var store = new MemorySettingsStore(new AppSettings());
		(MainWindowViewModel vm, _) = NewViewModel(store);
		vm.Settings.Remap = RemapSettings.Default.With(DsButton.Square, DsButton.Triangle) with { InvertLeftY = true };
		vm.Settings.Gyro = new GyroSettings { Mode = GyroMode.Always, SensitivityY = 3 };
		AppSettings saved = vm.Settings.ToAppSettings();

		// Through the real JSON store.
		string path = Path.Combine(Path.GetTempPath(), $"kopru-remap-{Guid.NewGuid():N}.json");
		try
		{
			var file = new JsonSettingsStore(path);
			file.Save(saved);
			(MainWindowViewModel loaded, FakeControllerService fake) = NewViewModel(file);
			Assert.Equal(vm.Settings.Remap, loaded.Settings.Remap);
			Assert.Equal(vm.Settings.Gyro, loaded.Settings.Gyro);
			Assert.Equal(vm.Settings.Remap, fake.Settings.Remap);

			// Older files without the new keys load as "no change".
			File.WriteAllText(path, "{ \"GameHapticsLevel\": 120 }");
			(MainWindowViewModel old, _) = NewViewModel(new JsonSettingsStore(path));
			Assert.True(old.Settings.Remap.IsIdentity);
			Assert.Equal(GyroSettings.Default, old.Settings.Gyro);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[AvaloniaFact]
	public void Tabs_SwitchBetweenTuningAndRemap_InBothLanguages()
	{
		foreach (Language language in new[] { Language.Turkish, Language.English })
		{
			WithLanguage(language, () =>
			{
				(MainWindowViewModel vm, _) = NewViewModel();
				vm.Navigate(vm.Controller);
				var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
				window.Show();
				vm.Controller.SelectedTab = ControllerPageViewModel.RemapTab;
				Settle();
				Assert.True(vm.Controller.IsRemapTab);
				RemapView view = window.GetLogicalDescendants().OfType<RemapView>().Single();
				Assert.True(view.IsEffectivelyVisible);
				List<string> texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
				Assert.Contains(Text.ButtonRemapping(language), texts);
				Assert.Contains(Text.GyroAiming(language), texts);
				Assert.Contains(Text.GyroCalibration(language), texts);
				window.Close();
			});
		}
	}

	// Renders the remap & gyro tab to docs/screens/remap-{tr,en}.png when KOPRU_RENDER_SCREENS=1.
	[AvaloniaFact]
	public void RenderRemapScreens()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string outDir = Path.Combine(RepoRoot(), "docs", "screens");
		foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
		{
			WithLanguage(language, () =>
			{
				(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
				vm.Settings.Remap = RemapSettings.Default.With(DsButton.Cross, DsButton.Circle).With(DsButton.Circle, DsButton.Cross)
					.With(DsButton.Mute, DsButton.None);
				vm.Settings.Gyro = new GyroSettings { Mode = GyroMode.WhileL2, SensitivityX = 1.5, Smoothing = 20, Target = GyroTarget.Mouse };
				fake.RawInput = GyroBody(-500, 900, buttons1: 0x04);
				vm.OpenController(ControllerPageViewModel.RemapTab);
				for (int i = 0; i < 20; i++)
				{
					vm.Sticks.RefreshInput();
					fake.RawInput = (byte[])fake.RawInput.Clone();
				}
				var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
				window.Show();
				Settle();
				using var frame = window.CaptureRenderedFrame();
				Assert.NotNull(frame);
				if (writeFiles)
				{
					frame!.Save(Path.Combine(outDir, $"remap-{code}.png"));
					// The whole tab, scrolled to the end, for checking the lower panels.
					window.GetLogicalDescendants().OfType<ScrollViewer>().First(s => s.Classes.Contains("page")).ScrollToEnd();
					Settle();
					using var end = window.CaptureRenderedFrame();
					end!.Save(Path.Combine(outDir, $"remap-end-{code}.png"));
				}
				window.Close();
			});
		}
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

	private static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Thread.Sleep(320);
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
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
