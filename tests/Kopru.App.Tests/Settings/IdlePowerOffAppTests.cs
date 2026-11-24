// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// Settings → General → Power: Idle Time slider and the Turn Off The Controller sub-switch form one group,
// the switch is disabled while the slider is 0, and Notifications uses the matching label.
public sealed class IdlePowerOffAppTests
{
	[AvaloniaFact]
	public void PowerOffSwitch_IsGroupedWithIdleTime_AndDisabledWhenZero_InBothLanguages()
	{
		foreach (Language language in new[] { Language.English, Language.Turkish })
		{
			Language? before = Localizer.Chosen;
			try
			{
				Localizer.Chosen = language;
				var fake = new FakeControllerService(animate: false);
				var settings = new SettingsState(fake);
				var vm = new SettingsViewModel(fake, settings) { SelectedTab = SettingsViewModel.GeneralTab };
				var window = new Window { Content = new SettingsView { DataContext = vm }, Width = 1100, Height = 700 };
				window.Show();
				FitCheck.Settle(window);

				StackPanel group = window.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == "IdlePowerGroup");
				Slider slider = group.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "IdleTimeSlider");
				Grid switchRow = group.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "PowerOffWhenIdleRow");
				ToggleSwitch toggle = switchRow.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "PowerOffWhenIdleSwitch");

				Assert.True(slider.IsEffectivelyVisible);
				Assert.True(toggle.IsEffectivelyVisible);
				Assert.True(settings.IdleTimerActive);
				Assert.True(switchRow.IsEnabled);
				Assert.True(toggle.IsEffectivelyEnabled);

				settings.IdlePowerOffMinutes = 0;
				FitCheck.Settle(window);
				Assert.False(settings.IdleTimerActive);
				Assert.False(switchRow.IsEnabled);
				Assert.False(toggle.IsEffectivelyEnabled);

				settings.IdlePowerOffMinutes = 10;
				FitCheck.Settle(window);
				Assert.True(settings.IdleTimerActive);
				Assert.True(switchRow.IsEnabled);
				Assert.True(toggle.IsEffectivelyEnabled);

				// Notifications → Controller label names the idle power-off notification clearly.
				Assert.Equal(language == Language.English ? "Powered Off When Idle" : "Boşta Kapanınca", Text.NotifyPowerOffSetting(language));

				window.Close();
			}
			finally
			{
				Localizer.Chosen = before;
			}
		}
	}
}
