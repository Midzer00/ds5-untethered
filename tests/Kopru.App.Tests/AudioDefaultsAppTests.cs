// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Audio;

namespace Kopru.App.Tests;

// "Keep My Default Audio Device": on by default, global (saved with the settings, not in profiles), drives the guard, and
// the guard's learned "the user chose the controller" state is saved and given back at the next start.
public class AudioDefaultsAppTests
{
	private static (MainWindowViewModel Vm, DefaultAudioGuard Guard, OneSpeaker Audio) Create(ISettingsStore store)
	{
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(store));
		var audio = new OneSpeaker();
		var guard = new DefaultAudioGuard(audio, audio.Time, a => a(), _ => { });
		vm.Settings.AttachAudioGuard(guard);
		return (vm, guard, audio);
	}

	[AvaloniaFact]
	public void OnByDefault_TheSwitchDrivesTheGuard_AndIsSaved()
	{
		var store = new MemorySettingsStore();
		(MainWindowViewModel vm, DefaultAudioGuard guard, _) = Create(store);
		Assert.True(vm.Settings.ProtectDefaultAudio);
		Assert.True(guard.Enabled);

		vm.Settings.ProtectDefaultAudio = false;
		Assert.False(guard.Enabled);
		vm.Settings.SaveNow();
		Assert.False(store.Saved!.ProtectDefaultAudio);
	}

	[AvaloniaFact]
	public void AnOldFile_WithoutTheKey_ProtectsTheDefaults()
	{
		var store = new MemorySettingsStore(new AppSettings { GameHapticsLevel = 120 });
		(MainWindowViewModel vm, DefaultAudioGuard guard, _) = Create(store);
		Assert.True(vm.Settings.ProtectDefaultAudio);
		Assert.True(guard.Enabled);
		Assert.Equal(new AudioGuardPreferences(), guard.Preferences);
	}

	[AvaloniaFact]
	public void TheLearnedChoice_IsSaved_AndGivenBackAtTheNextStart()
	{
		var store = new MemorySettingsStore(new AppSettings());
		(MainWindowViewModel vm, DefaultAudioGuard guard, OneSpeaker audio) = Create(store);
		audio.UserPicksTheController(guard);
		Dispatcher.UIThread.RunJobs();
		Assert.True(vm.Settings.AudioChoices.ControllerSpeakerChosen);
		vm.Settings.SaveNow();
		Assert.True(store.Saved!.ControllerSpeakerChosen);
		Assert.False(store.Saved.ControllerMicChosen);

		(MainWindowViewModel again, DefaultAudioGuard next, _) = Create(store);
		Assert.True(again.Settings.AudioChoices.ControllerSpeakerChosen);
		Assert.True(next.Preferences.ControllerSpeakerChosen);
	}

	// A PC speaker and the virtual pad's speaker.
	private sealed class OneSpeaker : IAudioDefaultsSystem
	{
		private IAudioDefaultsListener? listener;
		private string current = "pc";

		public StepTime Time { get; } = new();

		// Long after the pad's arrival window the user makes the controller speaker the default.
		public void UserPicksTheController(DefaultAudioGuard guard)
		{
			guard.PadAttaching(1, true);
			guard.PadAttached(1, 1);
			Time.Advance(TimeSpan.FromMinutes(2));
			current = "pad";
			listener!.DefaultChanged(AudioFlow.Render, AudioRole.Console, "pad");
		}

		public void Watch(IAudioDefaultsListener l) => listener = l;

		public string? GetDefault(AudioFlow flow, AudioRole role) => flow == AudioFlow.Render ? current : null;

		public AudioEndpointInfo? Describe(string id) => new(id, AudioFlow.Render, true, id,
			id == "pad" ? new PadAudioMatch(true, 1, PadMatchMethod.DeviceTree, "test") : PadAudioMatch.None);

		public IReadOnlyList<AudioEndpointInfo> Endpoints(AudioFlow flow) => [Describe("pc")!, Describe("pad")!];

		public void SetDefault(string id, AudioRole role) => current = id;
	}
}
