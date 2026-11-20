// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
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

// The controller microphone group on the Settings page: off by default, pushed to the service, saved in settings.json
// and in profiles, live level meter. The mute LED is no setting (it always shows the microphone, see Core MicTests).
public class MicAppTests
{
	[AvaloniaFact]
	public void Default_IsOff_AndChangesReachTheService()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		Assert.False(state.MicEnabled);
		Assert.Equal(MicSettings.Off, fake.Settings.Mic);

		state.MicEnabled = true;
		state.MicGain = 150;
		state.MicNoiseGate = false;

		Assert.Equal(new MicSettings(true, 1.5, false), fake.Settings.Mic);
		Assert.True(fake.Settings.Mic.IsLive);
		Assert.Equal(Text.PercentValue(150), state.MicGainText);
	}

	[AvaloniaFact]
	public void SettingsFile_RoundTrips_AndOldFilesLoadWithTheMicOff()
	{
		string dir = Path.Combine(Path.GetTempPath(), "untethered-" + Guid.NewGuid().ToString("N"));
		string path = Path.Combine(dir, "settings.json");
		Directory.CreateDirectory(dir);
		try
		{
			// The removed "MuteLed" light setting of older files is ignored.
			File.WriteAllText(path, """{ "Brightness": 50, "MuteLed": 1 }""");
			var old = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path));
			Assert.False(old.MicEnabled);
			Assert.Equal(100, old.MicGain);
			Assert.True(old.MicNoiseGate);
			Assert.Equal(Kopru.Core.Audio.MicButtonMode.TurnOff, old.MicButtonMode);

			var state = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path))
			{
				MicEnabled = true,
				MicGain = 250,
				MicNoiseGate = false,
				MicButtonMode = Kopru.Core.Audio.MicButtonMode.Mute
			};
			state.SaveNow();
			Assert.Contains("\"MicEnabled\": true", File.ReadAllText(path));
			Assert.DoesNotContain("MuteLed", File.ReadAllText(path));

			var fake = new FakeControllerService(animate: false);
			var loaded = new SettingsState(fake, new JsonSettingsStore(path));
			Assert.True(loaded.MicEnabled);
			Assert.Equal(250, loaded.MicGain);
			Assert.False(loaded.MicNoiseGate);
			Assert.Equal(Kopru.Core.Audio.MicButtonMode.Mute, loaded.MicButtonMode);
			Assert.Equal(new MicSettings(true, 2.5, false), fake.Settings.Mic);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[AvaloniaFact]
	public void Profiles_KeepTheirOwnMicSettings()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		var chat = state.Profiles.Create("Chat", state.CaptureValues())!;
		state.ActivateProfile(chat.Id);
		state.MicEnabled = true;
		state.MicGain = 200;

		Assert.True(state.Profiles.Find(chat.Id)!.Values.MicEnabled);
		Assert.Equal(200, state.Profiles.Find(chat.Id)!.Values.MicGain);
		Assert.False(state.Profiles.Default.Values.MicEnabled);

		state.ActivateProfile(Kopru.Core.Profiles.ProfileBook.DefaultId);
		Assert.False(fake.Settings.Mic.Enabled);

		state.ActivateProfile(chat.Id);
		Assert.True(fake.Settings.Mic.Enabled);
		Assert.Equal(2.0, fake.Settings.Mic.Gain, 3);
	}

	[AvaloniaFact]
	public async Task LevelMeter_ShowsOnlyWhileOnAndRunning()
	{
		var fake = new FakeControllerService(animate: false) { MicLevel = 0.42 };
		var state = new SettingsState(fake);
		var vm = new SettingsViewModel(fake, state);
		vm.RefreshMic();
		Assert.False(vm.MicActive);
		Assert.Equal(0, vm.MicLevel);

		state.MicEnabled = true;
		await fake.StartBridgeAsync();
		vm.RefreshMic();
		Assert.True(vm.MicActive);
		Assert.Equal(42, vm.MicLevel);
	}

	[AvaloniaFact]
	public async Task SettingsPage_ShowsTheGroup_InBothLanguages()
	{
		foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
		{
			Language? before = Localizer.Chosen;
			try
			{
				Localizer.Chosen = language;
				var fake = new FakeControllerService(animate: false) { MicLevel = 0.6 };
				var state = new SettingsState(fake);
				var vm = new SettingsViewModel(fake, state);
				vm.SelectedTab = SettingsViewModel.AudioTab;
				var window = new Window { Content = new SettingsView { DataContext = vm }, Width = 1100, Height = 900 };
				window.Show();
				Settle();

				SettingsView view = window.GetLogicalDescendants().OfType<SettingsView>().Single();
				List<TextBlock> texts = view.GetLogicalDescendants().OfType<TextBlock>().ToList();
				Assert.Contains(texts, t => t.Text == Text.GroupControllerMic(language));
				Assert.Contains(texts, t => t.Text == Text.MicSendToGame(language));
				Assert.Contains(texts, t => t.Text == Text.MicGain(language));
				Assert.Contains(texts, t => t.Text == Text.MicNoiseGate(language));
				Assert.Contains(texts, t => t.Text == Text.MicButtonModeLabel(language));
				Assert.Contains(texts, t => t.Text == Text.AudioInput(language));
				Assert.DoesNotContain(texts, t => t.Text?.Contains("Windows Mikrofonunu", StringComparison.Ordinal) == true);

				Border panel = view.GetLogicalDescendants().OfType<Border>().Single(b => b.Name == "MicPanel");
				ProgressBar meter = panel.GetLogicalDescendants().OfType<ProgressBar>().Single();
				Assert.False(meter.IsEffectivelyVisible);
				Assert.All(panel.GetLogicalDescendants().OfType<Slider>(), s => Assert.False(s.IsEffectivelyEnabled));
				ToggleSwitch toggle = panel.GetLogicalDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "MicSwitch");
				toggle.IsChecked = true;
				await fake.StartBridgeAsync();
				fake.RaiseStateChanged();
				Settle();
				vm.RefreshMic();
				Settle();
				Assert.True(state.MicEnabled);
				Assert.True(fake.Settings.Mic.Enabled);
				Assert.True(meter.IsEffectivelyVisible);
				Assert.Equal(60, meter.Value);
				Assert.All(panel.GetLogicalDescendants().OfType<Slider>(), s => Assert.True(s.IsEffectivelyEnabled));

				// KOPRU_MIC_RENDER=<dir> saves a picture of the group for review.
				string? outDir = Environment.GetEnvironmentVariable("KOPRU_MIC_RENDER");
				if (!string.IsNullOrEmpty(outDir))
				{
					using var frame = window.CaptureRenderedFrame();
					Directory.CreateDirectory(outDir);
					frame!.Save(Path.Combine(outDir, $"settings-mic-{code}.png"));
				}
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
