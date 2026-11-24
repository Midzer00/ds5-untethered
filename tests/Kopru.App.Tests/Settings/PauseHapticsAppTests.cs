// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Bridge;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// "Pause Haptics In Silence": off by default, an app setting (saved, not per profile), pushed to the service; Low Power
// turns it on while active without changing the setting; the service applies it to the host live.
public class PauseHapticsAppTests
{
	private readonly TestControllers controllers = new();

	[Fact]
	public void Setting_IsOffByDefault_Saved_Pushed_AndLowPowerTurnsItOn()
	{
		FakeControllerService fake = new();
		SettingsState settings = new(fake);
		Assert.False(settings.PauseHapticsInSilence);
		Assert.False(new AppSettings().PauseHapticsInSilence);
		Assert.Null(fake.Settings.PauseHapticsReason);

		settings.PauseHapticsInSilence = true;
		Assert.Equal("setting", fake.Settings.PauseHapticsReason);
		Assert.True(settings.ToAppSettings().PauseHapticsInSilence);

		settings.PauseHapticsInSilence = false;
		settings.LowPowerOn = true;
		Assert.Equal("low power", fake.Settings.PauseHapticsReason);
		Assert.False(settings.ToAppSettings().PauseHapticsInSilence);

		settings.LowPowerOn = false;
		Assert.Null(fake.Settings.PauseHapticsReason);
	}

	[Fact]
	public void Service_AppliesItToTheHost_Live()
	{
		BridgeHost host = new(controllers, new TestIdentity(true), new TestAttacher());
		using RealControllerService service = new(host, new TestHiding(), controllers, a => a(), _ => null, timers: false) { DsxPort = 0 };
		Assert.False(host.PauseHapticsInSilence);

		service.ApplySettings(new ControllerSettings { PauseHapticsInSilence = true });
		Assert.True(host.PauseHapticsInSilence);

		service.ApplySettings(LowPower.Apply(new ControllerSettings(), true));
		Assert.True(host.PauseHapticsInSilence);

		service.ApplySettings(new ControllerSettings());
		Assert.False(host.PauseHapticsInSilence);
	}

	[Fact]
	public void Texts_InBothLanguages()
	{
		Assert.Equal("Pause Haptics In Silence", Text.PauseHapticsInSilence(Language.English));
		Assert.Equal("Sessizlikte Haptiği Durdur", Text.PauseHapticsInSilence(Language.Turkish));
		Assert.StartsWith("Oyun sessizken kumandaya haptik gönderilmez", Text.PauseHapticsInSilenceHint(Language.Turkish), StringComparison.Ordinal);
		Assert.Equal("Devices & Profiles", Text.SettingsTabDevices(Language.English));
		Assert.Equal("Cihazlar ve Profiller", Text.SettingsTabDevices(Language.Turkish));
	}

	[AvaloniaFact]
	public void Switch_IsOnHapticsAndTriggers_AndGoneFromSettings_InBothLanguages()
	{
		foreach (Language language in new[] { Language.English, Language.Turkish })
		{
			Language? before = Localizer.Chosen;
			try
			{
				Localizer.Chosen = language;
				var fake = new FakeControllerService(animate: false);
				var settings = new SettingsState(fake);

				var hapticsVm = new OtherGamesViewModel(fake, settings);
				var hapticsWindow = new Window { Content = new OtherGamesView { DataContext = hapticsVm }, Width = 1100, Height = 700 };
				hapticsWindow.Show();
				FitCheck.Settle(hapticsWindow);
				ToggleSwitch hapticsSwitch = hapticsWindow.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "PauseHapticsSwitch");
				Assert.True(hapticsSwitch.IsEffectivelyVisible);
				hapticsSwitch.IsChecked = true;
				Assert.True(settings.PauseHapticsInSilence);
				hapticsWindow.Close();

				var settingsVm = new SettingsViewModel(fake, settings) { SelectedTab = SettingsViewModel.ControllersTab };
				Assert.Equal(Text.SettingsTabDevices(language), settingsVm.Tabs[SettingsViewModel.ControllersTab].Name);
				var settingsWindow = new Window { Content = new SettingsView { DataContext = settingsVm }, Width = 1100, Height = 700 };
				settingsWindow.Show();
				FitCheck.Settle(settingsWindow);
				Assert.Empty(settingsWindow.GetVisualDescendants().OfType<Grid>().Where(g => g.Name == "PauseHapticsRow"));
				Assert.Empty(settingsWindow.GetVisualDescendants().OfType<ToggleSwitch>().Where(t => t.Name == "PauseHapticsSwitch"));
				settingsWindow.Close();
			}
			finally
			{
				Localizer.Chosen = before;
			}
		}
	}
}
