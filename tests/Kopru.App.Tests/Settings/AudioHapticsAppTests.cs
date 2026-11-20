// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Audio;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;

namespace Kopru.App.Tests;

// "Turn sound into haptics" in the settings: part of the profile (so per game), pushed to the service, saved and loaded,
// and a status line from the service.
public class AudioHapticsAppTests
{
	[AvaloniaFact]
	public void Defaults_AreOff_100Percent_Bass()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		Assert.False(state.AudioHapticsOn);
		Assert.Equal(100, state.AudioHapticsStrength);
		Assert.Equal(AudioHapticsBand.Bass, state.AudioHapticsBand);
		Assert.True(state.IsAudioHapticsBass);
		Assert.True(state.AudioHapticsAvailable);
		Assert.Equal(Text.AudioHapticsStatusOff(), state.AudioHapticsStatus);
		Assert.False(fake.Settings.Game.AudioHaptics.On);
	}

	[AvaloniaFact]
	public void Changes_ArePushed_AndGoIntoTheActiveProfileOnly()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		ControllerProfile racing = state.Profiles.Create("Racing", state.CaptureValues())!;
		state.ActivateProfile(racing.Id);

		state.AudioHapticsOn = true;
		state.AudioHapticsStrength = 160;
		state.IsAudioHapticsFull = true;
		Assert.Equal(new AudioHapticsSettings(true, 1.6, AudioHapticsBand.Full), fake.Settings.Game.AudioHaptics);
		Assert.True(state.Profiles.Find(racing.Id)!.Values.AudioHaptics);
		Assert.Equal(160, state.Profiles.Find(racing.Id)!.Values.AudioHapticsStrength);
		Assert.False(state.Profiles.Default.Values.AudioHaptics);

		state.ActivateProfile(ProfileBook.DefaultId);
		Assert.False(state.AudioHapticsOn);
		Assert.False(fake.Settings.Game.AudioHaptics.On);
		state.ActivateProfile(racing.Id);
		Assert.True(fake.Settings.Game.AudioHaptics.On);
		Assert.Equal(AudioHapticsBand.Full, state.AudioHapticsBand);

		// 0-200 %.
		state.AudioHapticsStrength = 500;
		Assert.Equal(200, state.AudioHapticsStrength);
	}

	[AvaloniaFact]
	public void SavedAndLoaded_ThroughTheSettingsFile()
	{
		string path = Path.Combine(Path.GetTempPath(), $"untethered-audiohaptics-{Guid.NewGuid():N}.json");
		try
		{
			var store = new JsonSettingsStore(path);
			var state = new SettingsState(new FakeControllerService(animate: false), store);
			state.AudioHapticsOn = true;
			state.AudioHapticsStrength = 40;
			state.AudioHapticsBand = AudioHapticsBand.Full;
			state.SaveNow();

			var again = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path));
			Assert.True(again.AudioHapticsOn);
			Assert.Equal(40, again.AudioHapticsStrength);
			Assert.Equal(AudioHapticsBand.Full, again.AudioHapticsBand);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[AvaloniaFact]
	public void OldSettings_WithoutTheFields_LoadAsOff()
	{
		var state = new SettingsState(new FakeControllerService(animate: false), new MemorySettingsStore(new AppSettings
		{
			Profiles = [new ControllerProfile(ProfileBook.DefaultId, "Default", new ProfileValues { RumbleLevel = 70 })]
		}));
		Assert.Equal(70, state.RumbleLevel);
		Assert.False(state.AudioHapticsOn);
		Assert.Equal(100, state.AudioHapticsStrength);
		Assert.Equal(AudioHapticsBand.Bass, state.AudioHapticsBand);
	}

	[AvaloniaFact]
	public void Status_FollowsTheService()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			var fake = new FakeControllerService(animate: false);
			var state = new SettingsState(fake);
			List<string?> changed = [];
			state.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

			fake.AudioHapticsStatus = new AudioHapticsStatus(AudioHapticsStatusKind.Listening, "Forza", AudioCaptureMode.ProcessLoopback);
			Assert.Equal("Listening To Forza", state.AudioHapticsStatus);
			Assert.Contains(nameof(SettingsState.AudioHapticsStatus), changed);
			fake.AudioHapticsStatus = new AudioHapticsStatus(AudioHapticsStatusKind.Listening, "Forza", AudioCaptureMode.SystemLoopback);
			Assert.Equal("Listening To Forza (All System Sound)", state.AudioHapticsStatus);
			fake.AudioHapticsStatus = new AudioHapticsStatus(AudioHapticsStatusKind.WaitingForGame);
			Assert.Equal("Waiting For The Game", state.AudioHapticsStatus);
			fake.AudioHapticsStatus = new AudioHapticsStatus(AudioHapticsStatusKind.NativeHaptics, "Forza");
			Assert.Equal("The Game's Own Haptics In Use", state.AudioHapticsStatus);
			fake.AudioHapticsStatus = new AudioHapticsStatus(AudioHapticsStatusKind.Unavailable, "Forza");
			Assert.Equal("Not Available On This Windows", state.AudioHapticsStatus);
			Assert.False(state.AudioHapticsAvailable);

			Localizer.Chosen = Language.Turkish;
			Assert.Equal("Bu Windows'ta Kullanılamıyor", state.AudioHapticsStatus);
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}
}
