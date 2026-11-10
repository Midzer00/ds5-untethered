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
using Kopru.Core.Lighting;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The controller microphone group on the Settings page: off by default, pushed to the service, saved in settings.json
// and in profiles, mute LED follows the mic when it is turned on, live level meter.
public class MicAppTests
{
	[AvaloniaFact]
	public void Default_IsOff_AndChangesReachTheService_AndTheMuteLedFollowsTheMic()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		Assert.False(state.MicEnabled);
		Assert.Equal(MicSettings.Off, fake.Settings.Mic);
		Assert.Equal(MuteLedMode.Off, state.MuteLed);

		state.MicEnabled = true;
		state.MicGain = 150;
		state.MicNoiseGate = false;
		state.MicMuteWindows = true;

		Assert.Equal(new MicSettings(true, 1.5, false, true), fake.Settings.Mic);
		Assert.Equal(MuteLedMode.FollowMic, state.MuteLed);
		Assert.Equal(MuteLedMode.FollowMic, fake.Settings.LightOptions.MuteLed);
		Assert.Equal(Text.PercentValue(150), state.MicGainText);

		// "Always on" picked by the user is kept.
		var other = new SettingsState(new FakeControllerService(animate: false)) { MuteLed = MuteLedMode.On };
		other.MicEnabled = true;
		Assert.Equal(MuteLedMode.On, other.MuteLed);
	}

	[AvaloniaFact]
	public void SettingsFile_RoundTrips_AndOldFilesLoadWithTheMicOff()
	{
		string dir = Path.Combine(Path.GetTempPath(), "untethered-" + Guid.NewGuid().ToString("N"));
		string path = Path.Combine(dir, "settings.json");
		Directory.CreateDirectory(dir);
		try
		{
			File.WriteAllText(path, """{ "Brightness": 50, "MuteLed": 1 }""");
			var old = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path));
			Assert.False(old.MicEnabled);
			Assert.Equal(100, old.MicGain);
			Assert.True(old.MicNoiseGate);
			Assert.False(old.MicMuteWindows);
			Assert.Equal(MuteLedMode.On, old.MuteLed);

			var state = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path))
			{
				MicEnabled = true,
				MicGain = 250,
				MicNoiseGate = false,
				MicMuteWindows = true,
				MuteLed = MuteLedMode.FollowMic
			};
			state.SaveNow();
			Assert.Contains("\"MicEnabled\": true", File.ReadAllText(path));

			var fake = new FakeControllerService(animate: false);
			var loaded = new SettingsState(fake, new JsonSettingsStore(path));
			Assert.True(loaded.MicEnabled);
			Assert.Equal(250, loaded.MicGain);
			Assert.False(loaded.MicNoiseGate);
			Assert.True(loaded.MicMuteWindows);
			Assert.Equal(MuteLedMode.FollowMic, loaded.MuteLed);
			Assert.Equal(new MicSettings(true, 2.5, false, true), fake.Settings.Mic);
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
		Assert.Equal(MuteLedMode.FollowMic, state.Profiles.Find(chat.Id)!.Values.MuteLed);
		Assert.False(state.Profiles.Default.Values.MicEnabled);

		state.ActivateProfile(Kopru.Core.Profiles.ProfileBook.DefaultId);
		Assert.False(fake.Settings.Mic.Enabled);
		Assert.Equal(MuteLedMode.Off, state.MuteLed);

		state.ActivateProfile(chat.Id);
		Assert.True(fake.Settings.Mic.Enabled);
		Assert.Equal(2.0, fake.Settings.Mic.Gain, 3);
		Assert.Equal(MuteLedMode.FollowMic, state.MuteLed);
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

	// The controller's mic button (MicOn / MicOff from the service): the same switch as the tile and the Settings page,
	// one push per press, the mute LED follows the mic, a toast, a log line; nothing under Low Power.
	[AvaloniaFact]
	public void MicButton_FlipsTheSameSwitch_WithAToast_ButNotUnderLowPower()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(null));
		SettingsState state = vm.Settings;
		QuickSettingsViewModel quick = vm.Overview.Quick;
		List<(string Title, string Body)> toasts = [];
		state.ShortcutFeedback += (title, body) => toasts.Add((title, body));
		ConcurrentQueue<string> lines = new();
		void Watch(string line)
		{
			if (line.StartsWith("settings: mic switch", StringComparison.Ordinal))
			{
				lines.Enqueue(line);
			}
		}
		Kopru.Core.Diagnostics.AppLog.Written += Watch;
		try
		{
			Dispatcher.UIThread.RunJobs();
			int applies = fake.Applies;

			fake.RaiseShortcut(Kopru.Core.Input.ShortcutAction.MicOn);
			Dispatcher.UIThread.RunJobs();
			Assert.True(state.MicEnabled);
			Assert.True(quick.MicOn);
			Assert.True(fake.Settings.Mic.Enabled);
			Assert.Equal(MuteLedMode.FollowMic, fake.Settings.LightOptions.MuteLed);
			Assert.Equal(applies + 1, fake.Applies);
			Assert.Equal((Text.MicButtonToastTitle(true), Text.MicButtonToastBody(true)), toasts.Single());

			fake.RaiseShortcut(Kopru.Core.Input.ShortcutAction.MicOff);
			Dispatcher.UIThread.RunJobs();
			Assert.False(state.MicEnabled);
			Assert.False(quick.MicOn);
			Assert.False(fake.Settings.Mic.Enabled);
			Assert.Equal(applies + 2, fake.Applies);
			Assert.Equal(Text.MicButtonToastTitle(false), toasts.Last().Title);
			Assert.Contains("settings: mic switch on (controller button)", lines);
			Assert.Contains("settings: mic switch off (controller button)", lines);

			// Low Power holds the mic off: a press changes nothing and shows nothing.
			state.LowPowerOn = true;
			Dispatcher.UIThread.RunJobs();
			Assert.False(fake.Settings.Mic.Button);
			int before = fake.Applies;
			fake.RaiseShortcut(Kopru.Core.Input.ShortcutAction.MicOn);
			Dispatcher.UIThread.RunJobs();
			Assert.False(state.MicEnabled);
			Assert.Equal(before, fake.Applies);
			Assert.Equal(2, toasts.Count);
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= Watch;
		}
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
				var window = new Window { Content = new SettingsView { DataContext = vm }, Width = 1100, Height = 900 };
				window.Show();
				Settle();

				SettingsView view = window.GetLogicalDescendants().OfType<SettingsView>().Single();
				List<TextBlock> texts = view.GetLogicalDescendants().OfType<TextBlock>().ToList();
				Assert.Contains(texts, t => t.Text == Text.GroupControllerMic(language));
				Assert.Contains(texts, t => t.Text == Text.MicSendToGame(language));
				Assert.Contains(texts, t => t.Text == Text.MicGain(language));
				Assert.Contains(texts, t => t.Text == Text.MicNoiseGate(language));
				Assert.Contains(texts, t => t.Text == Text.MicMuteWindows(language));

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
					panel.BringIntoView();
					Settle();
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
