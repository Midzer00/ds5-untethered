// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Bridge;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The experimental controller speaker group on the Settings page: off by default, pushed to the service, saved in
// settings.json and in profiles.
public class SpeakerAppTests
{
	[AvaloniaFact]
	public void Default_IsOff_AndChangesReachTheService()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		Assert.False(state.SpeakerEnabled);
		Assert.Equal(SpeakerSettings.Off, fake.Settings.Speaker);

		state.SpeakerEnabled = true;
		state.SpeakerVolume = 50;
		state.HeadphoneVolume = 25;

		Assert.Equal(new SpeakerSettings(true, 0.5, 0.25), fake.Settings.Speaker);
		Assert.Equal(Text.PercentValue(50), state.SpeakerVolumeText);
	}

	[AvaloniaFact]
	public void SettingsFile_RoundTrips_AndOldFilesLoadWithTheSpeakerOff()
	{
		string dir = Path.Combine(Path.GetTempPath(), "untethered-" + Guid.NewGuid().ToString("N"));
		string path = Path.Combine(dir, "settings.json");
		Directory.CreateDirectory(dir);
		try
		{
			File.WriteAllText(path, """{ "Brightness": 50 }""");
			var old = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path));
			Assert.False(old.SpeakerEnabled);
			Assert.Equal(80, old.SpeakerVolume);
			Assert.Equal(60, old.HeadphoneVolume);

			var state = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path))
			{
				SpeakerEnabled = true,
				SpeakerVolume = 35,
				HeadphoneVolume = 45
			};
			state.SaveNow();
			Assert.Contains("\"SpeakerEnabled\": true", File.ReadAllText(path));

			var fake = new FakeControllerService(animate: false);
			var loaded = new SettingsState(fake, new JsonSettingsStore(path));
			Assert.True(loaded.SpeakerEnabled);
			Assert.Equal(35, loaded.SpeakerVolume);
			Assert.Equal(45, loaded.HeadphoneVolume);
			Assert.Equal(new SpeakerSettings(true, 0.35, 0.45), fake.Settings.Speaker);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[AvaloniaFact]
	public void Profiles_KeepTheirOwnSpeakerSettings()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		var music = state.Profiles.Create("Music", state.CaptureValues())!;
		state.ActivateProfile(music.Id);
		state.SpeakerEnabled = true;
		state.SpeakerVolume = 90;

		Assert.True(state.Profiles.Find(music.Id)!.Values.SpeakerEnabled);
		Assert.Equal(90, state.Profiles.Find(music.Id)!.Values.SpeakerVolume);
		Assert.False(state.Profiles.Default.Values.SpeakerEnabled);

		state.ActivateProfile(Kopru.Core.Profiles.ProfileBook.DefaultId);
		Assert.False(state.SpeakerEnabled);
		Assert.False(fake.Settings.Speaker.Enabled);

		state.ActivateProfile(music.Id);
		Assert.True(fake.Settings.Speaker.Enabled);
		Assert.Equal(0.9, fake.Settings.Speaker.SpeakerVolume, 3);
		Assert.True(state.ToAppSettings().Profiles!.Single(p => p.Id == music.Id).Values.SpeakerEnabled);
	}

	[AvaloniaFact]
	public void SettingsPage_ShowsTheGroup_InBothLanguages_AndTheHeadphoneLine()
	{
		foreach (Language language in new[] { Language.Turkish, Language.English })
		{
			Language? before = Localizer.Chosen;
			try
			{
				Localizer.Chosen = language;
				var fake = new FakeControllerService(animate: false);
				var state = new SettingsState(fake);
				var vm = new SettingsViewModel(fake, state);
				var window = new Window { Content = new SettingsView { DataContext = vm }, Width = 1100, Height = 900 };
				window.Show();
				Settle();

				SettingsView view = window.GetLogicalDescendants().OfType<SettingsView>().Single();
				List<TextBlock> texts = view.GetLogicalDescendants().OfType<TextBlock>().ToList();
				Assert.Contains(texts, t => t.Text == Text.GroupControllerSpeaker(language));
				Assert.Contains(texts, t => t.Text == Text.SpeakerSendGameAudio(language));
				Assert.Contains(texts, t => t.Text == Text.SpeakerSendGameAudioHint(language));
				Assert.Contains(texts, t => t.Text == Text.SpeakerVolume(language));
				Assert.Contains(texts, t => t.Text == Text.HeadphoneVolume(language));
				TextBlock plugged = texts.Single(t => t.Text == Text.HeadphonesPlugged(language));
				Assert.False(plugged.IsVisible);

				fake.HeadphonesPlugged = true;
				fake.RaiseStateChanged();
				Settle();
				Assert.True(vm.HeadphonesPlugged);
				Assert.True(plugged.IsVisible);

				Border panel = view.GetLogicalDescendants().OfType<Border>().Single(b => b.Name == "SpeakerPanel");
				ToggleSwitch toggle = panel.GetLogicalDescendants().OfType<ToggleSwitch>().Single();
				Assert.False(toggle.IsChecked);
				Assert.All(panel.GetLogicalDescendants().OfType<Slider>(), s => Assert.False(s.IsEffectivelyEnabled));
				toggle.IsChecked = true;
				Settle();
				Assert.True(state.SpeakerEnabled);
				Assert.True(fake.Settings.Speaker.Enabled);
				Assert.All(panel.GetLogicalDescendants().OfType<Slider>(), s => Assert.True(s.IsEffectivelyEnabled));
				window.Close();
			}
			finally
			{
				Localizer.Chosen = before;
			}
		}
	}

	private static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Dispatcher.UIThread.RunJobs();
	}
}
