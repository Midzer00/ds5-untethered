// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Bridge;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The controller page's remap tabs (Buttons, Gyro & Sticks, Edge): settings reach the service and the store, live gyro
// offset, calibration.
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

	// The former Remap & Gyro tab is split: Buttons holds only the remap card, Gyro & Sticks the gyro, its calibration and the
	// stick swap and invert, Edge the paddles. Each control is on its new tab and on no other, in both languages.
	[AvaloniaFact]
	public void RemapTabs_HoldTheirControls_AndOnlyThose_InBothLanguages()
	{
		foreach (Language language in new[] { Language.Turkish, Language.English })
		{
			WithLanguage(language, () =>
			{
				var fake = new FakeControllerService(ControllerKind.DualSenseEdge, animate: false);
				var vm = new MainWindowViewModel(fake, new AppEnvironment(null));
				vm.Sticks.StopPreview();
				vm.Navigate(vm.Controller);
				var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
				window.Show();
				string[] buttons = [Text.ButtonRemapping(language)];
				string[] gyro = [Text.GyroAiming(language), Text.GyroModeLabel(language), Text.GyroTargetLabel(language), Text.GyroSensitivityX(language),
					Text.GyroSmoothing(language), Text.GyroDeadzone(language), Text.GyroInvert(language), Text.GyroCalibration(language),
					Text.StickSwapInvert(language), Text.SwapSticks(language), Text.InvertLeftStick(language), Text.InvertRightStick(language)];
				string[] edge = [Text.EdgePaddlesTitle(language), Text.PresentAsEdge(language)];
				Assert.Equal(["Test", Text.ControllerTabDeviceTests(language), Text.ControllerTabButtons(language), Text.ControllerTabGyroSticks(language), "Edge"],
					vm.Controller.Tabs.Select(t => t.Name));
				Assert.Equal(Text.RemapIntro(language), vm.Controller.Tabs[ControllerPageViewModel.ButtonsTab].Tip);
				foreach ((int tab, string[] own) in new[] { (ControllerPageViewModel.ButtonsTab, buttons), (ControllerPageViewModel.GyroTab, gyro),
					(ControllerPageViewModel.EdgeTab, edge), (ControllerPageViewModel.TestTab, Array.Empty<string>()), (ControllerPageViewModel.OutputsTab, Array.Empty<string>()) })
				{
					vm.Controller.SelectedTab = tab;
					Settle();
					ControllerView page = window.GetLogicalDescendants().OfType<ControllerView>().Single();
					HashSet<string> shown = page.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToHashSet();
					foreach (string label in own)
					{
						Assert.True(shown.Contains(label), $"{language.Code} tab {tab}: '{label}' missing");
					}
					foreach (string label in buttons.Concat(gyro).Concat(edge).Except(own))
					{
						Assert.False(shown.Contains(label), $"{language.Code} tab {tab}: '{label}' shown");
					}
					// The intro line and the hints are tooltips now, not lines on the tab.
					Assert.DoesNotContain(Text.RemapIntro(language), shown);
					Assert.DoesNotContain(Text.ButtonRemappingHint(language), shown);
					Assert.DoesNotContain(Text.GyroAimingHint(language), shown);
					Assert.DoesNotContain(Text.GyroTargetHint(language), shown);
				}
				// The Buttons tab holds only the remap card; each part of the old tab is a view of its own.
				Assert.Equal(1, window.GetLogicalDescendants().OfType<RemapView>().Single().GetLogicalDescendants().OfType<Border>()
					.Count(b => b.Classes.Contains("panel")));
				Assert.Single(window.GetLogicalDescendants().OfType<GyroSticksView>());
				Assert.Single(window.GetLogicalDescendants().OfType<EdgeRemapView>());
				window.Close();
			});
		}
	}

	// The Edge tab is there only while an Edge is connected; asked for without an Edge, the page shows Buttons.
	[AvaloniaFact]
	public void EdgeTab_ComesAndGoes_WithTheEdge()
	{
		var plain = new MainWindowViewModel(new FakeControllerService(ControllerKind.DualSense, animate: false), new AppEnvironment(null));
		plain.Sticks.StopPreview();
		Assert.False(plain.Controller.HasEdgeTab);
		Assert.False(plain.Controller.Tabs[ControllerPageViewModel.EdgeTab].IsVisible);
		plain.OpenController(ControllerPageViewModel.EdgeTab);
		Assert.True(plain.Controller.IsButtonsTab);
		var window = new MainWindow { DataContext = plain, Width = 1100, Height = 700 };
		window.Show();
		Settle();
		ListBox tabs = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ControllerTabs");
		Assert.Equal(4, tabs.GetVisualDescendants().OfType<ListBoxItem>().Count(i => i.IsVisible));
		window.Close();

		var fake = new FakeControllerService(ControllerKind.DualSenseEdge, animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(null));
		vm.Sticks.StopPreview();
		Assert.True(vm.Controller.HasEdgeTab);
		vm.OpenController(ControllerPageViewModel.EdgeTab);
		Assert.True(vm.Controller.IsEdgeTab);
		window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		Settle();
		tabs = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ControllerTabs");
		Assert.Equal(5, tabs.GetVisualDescendants().OfType<ListBoxItem>().Count(i => i.IsVisible));
		Assert.Equal(ControllerPageViewModel.EdgeTab, tabs.SelectedIndex);
		window.Close();
	}

	// The invert strips (gyro Horizontal / Vertical, each stick's X / Y) write and show the same settings as the switches
	// they replace (InvertX ... InvertRightY), both ways.
	[AvaloniaFact]
	public void InvertStrips_AreBoundToTheInvertSettings()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = NewViewModel();
		RemapViewModel page = vm.Sticks.Remap;
		vm.Settings.Gyro = vm.Settings.Gyro with { Mode = GyroMode.Always };
		vm.OpenController(ControllerPageViewModel.GyroTab);
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		Settle();
		ListBox Strip(string name) => window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == name);
		var strips = new (string Name, ToggleChoices Choices, Func<bool>[] Settings)[]
		{
			("GyroInvertChoice", page.GyroInvert, [() => fake.Settings.Gyro.InvertX, () => fake.Settings.Gyro.InvertY]),
			("LeftStickInvertChoice", page.LeftStickInvert, [() => fake.Settings.Remap.InvertLeftX, () => fake.Settings.Remap.InvertLeftY]),
			("RightStickInvertChoice", page.RightStickInvert, [() => fake.Settings.Remap.InvertRightX, () => fake.Settings.Remap.InvertRightY]),
		};
		foreach ((string name, ToggleChoices choices, Func<bool>[] settings) in strips)
		{
			ListBox strip = Strip(name);
			Assert.True(strip.IsEffectivelyVisible, name);
			Assert.Same(choices.Selection, strip.Selection);
			Assert.Equal(SelectionMode.Multiple | SelectionMode.Toggle, strip.SelectionMode);
			for (int i = 0; i < 2; i++)
			{
				// A segment clicked: its setting turns on and the other stays off; clicked again: off.
				ListBoxItem item = strip.GetVisualDescendants().OfType<ListBoxItem>().ElementAt(i);
				Click(window, item);
				Assert.True(settings[i](), $"{name} {i} on");
				Assert.False(settings[1 - i](), $"{name} {1 - i} stays off");
				Assert.True(item.IsSelected);
				Click(window, item);
				Assert.False(settings[i](), $"{name} {i} off");
				Assert.False(item.IsSelected);
			}
			// Both on at once.
			Click(window, strip.GetVisualDescendants().OfType<ListBoxItem>().ElementAt(0));
			Click(window, strip.GetVisualDescendants().OfType<ListBoxItem>().ElementAt(1));
			Assert.True(settings[0]() && settings[1](), $"{name} both on");
		}
		Assert.True(page.InvertX && page.InvertY && page.InvertLeftX && page.InvertLeftY && page.InvertRightX && page.InvertRightY);
		// The settings changed elsewhere (Reset, a profile): the strips follow.
		page.ResetRemapCommand.Execute(null);
		page.ResetGyroCommand.Execute(null);
		Settle();
		foreach ((string name, _, _) in strips)
		{
			Assert.DoesNotContain(Strip(name).GetVisualDescendants().OfType<ListBoxItem>(), i => i.IsSelected);
		}
		vm.Settings.Remap = vm.Settings.Remap with { InvertLeftY = true };
		Settle();
		Assert.Equal([false, true], Strip("LeftStickInvertChoice").GetVisualDescendants().OfType<ListBoxItem>().Select(i => i.IsSelected));
		// Their accessible names and tooltips are the whole settings.
		Assert.Equal([Text.InvertLeftX(), Text.InvertLeftY()], page.LeftStickInvert.Choices.Select(c => c.AccessibleName));
		ListBoxItem horizontal = Strip("GyroInvertChoice").GetVisualDescendants().OfType<ListBoxItem>().First();
		Assert.Equal(Text.GyroInvertX(), Avalonia.Automation.AutomationProperties.GetName(horizontal));
		Assert.Equal(Text.GyroInvertX(), ToolTip.GetTip(horizontal));
		window.Close();
	}

	private static void Click(Window window, Avalonia.Visual target)
	{
		Avalonia.Point center = FitCheck.Bounds(target, window).Center;
		window.MouseDown(center, Avalonia.Input.MouseButton.Left);
		window.MouseUp(center, Avalonia.Input.MouseButton.Left);
		FitCheck.Settle(window);
	}

	// Renders the Gyro & Sticks and the Buttons tabs to docs/screens/remap-{gyro-sticks,buttons}-{tr,en}.png when KOPRU_RENDER_SCREENS=1.
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
				vm.OpenController(ControllerPageViewModel.GyroTab);
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
					frame!.Save(Path.Combine(outDir, $"remap-gyro-sticks-{code}.png"));
				}
				vm.Controller.SelectedTab = ControllerPageViewModel.ButtonsTab;
				Settle();
				using var buttons = window.CaptureRenderedFrame();
				Assert.NotNull(buttons);
				if (writeFiles)
				{
					buttons!.Save(Path.Combine(outDir, $"remap-buttons-{code}.png"));
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
