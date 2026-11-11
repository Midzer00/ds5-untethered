// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Audio;
using Kopru.Core.Bridge;
using Kopru.Core.Lighting;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The sound device pickers (Quick Settings and Settings) and the mic button modes: lists that follow Windows, a choice set
// for every role and read back, the guard learning it, the disabled state, and what a press of the button (or the tile)
// does for each mode and selected microphone, with the LED and the toast.
public class AudioPickersAppTests
{
	private static (MainWindowViewModel Vm, FakeControllerService Fake) Create(ISettingsStore? store = null, Action<FakeControllerService>? setup = null)
	{
		var fake = new FakeControllerService(animate: false);
		setup?.Invoke(fake);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(store));
		Settle();
		return (vm, fake);
	}

	private static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Dispatcher.UIThread.RunJobs();
	}

	[AvaloniaFact]
	public void Lists_ShowTheControllerWithPlainNames_AndSelectTheDefaults()
	{
		(MainWindowViewModel vm, _) = Create();
		AudioDevicesViewModel a = vm.Overview.Quick.Audio;
		Assert.Same(a, vm.Settings.Audio);
		Assert.Equal([Text.ControllerSpeakerDevice(), "Hoparlör (Realtek Audio)", "Kulaklık (HyperX Cloud II Wireless Gaming Headset)"], a.Outputs.Select(i => i.Name));
		Assert.Equal("Hoparlör (Realtek(R) Audio)", a.Outputs[1].FullName);
		Assert.Equal("speaker", a.SelectedOutput!.Id);
		Assert.Equal(Text.ControllerMicDevice(), a.SelectedInput!.Name);
		Assert.True(a.CanChoose);
		Assert.False(a.ShowsUnavailable);
	}

	[AvaloniaFact]
	public void SeveralPads_AreNamedByTheirSlot()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create(setup: f =>
		{
			f.AudioEndpoints.Add("pad2-speaker", AudioFlow.Render, "Hoparlör (2- DualSense Wireless Controller)", pad: true, port: 2);
			f.AudioPortSlots[1] = 1;
			f.AudioPortSlots[2] = 3;
		});
		Assert.Equal([Text.ControllerSpeakerDevice(1), Text.ControllerSpeakerDevice(3)], vm.Settings.Audio.Outputs.Where(i => i.IsPad).Select(i => i.Name));
	}

	[AvaloniaFact]
	public void Lists_FollowArrivalsRemovalsAndDefaultChangesMadeElsewhere()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		AudioDevicesViewModel a = vm.Settings.Audio;
		fake.AudioEndpoints.Add("dac", AudioFlow.Render, "Speakers (USB DAC)");
		Settle();
		Assert.Contains(a.Outputs, i => i.Id == "dac");
		fake.AudioEndpoints.SetAll(AudioFlow.Render, "dac");
		Settle();
		Assert.Equal("dac", a.SelectedOutput!.Id);
		fake.AudioEndpoints.Remove("headset");
		Settle();
		Assert.DoesNotContain(a.Outputs, i => i.Id == "headset");
		// The guard putting the PC speaker back shows at once too.
		fake.AudioEndpoints.SetAll(AudioFlow.Render, "speaker");
		Settle();
		Assert.Equal("speaker", a.SelectedOutput!.Id);
	}

	[AvaloniaFact]
	public void Choosing_SetsEveryRole_ReadsItBack_AndTellsTheGuard()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		var guard = new DefaultAudioGuard(fake.AudioEndpoints, post: FakeControllerService.Serial(), log: _ => { });
		fake.AudioDevices!.Guard = guard;
		AudioDevicesViewModel a = vm.Settings.Audio;

		a.SelectedOutput = a.Outputs.Single(i => i.IsPad);
		Settle();
		Assert.Equal(3, fake.AudioEndpoints.Sets.Count(s => s.Id == "pad-speaker"));
		Assert.Equal([AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications], fake.AudioEndpoints.Sets.Select(s => s.Role));
		Assert.Equal("pad-speaker", vm.Overview.Quick.Audio.SelectedOutput!.Id);
		Assert.True(guard.Preferences.ControllerSpeakerChosen);
		Assert.False(a.HasError);

		a.SelectedOutput = a.Outputs.Single(i => i.Id == "headset");
		Settle();
		Assert.False(guard.Preferences.ControllerSpeakerChosen);
	}

	[AvaloniaFact]
	public void AChoiceWindowsDidNotTake_ShowsAShortError_AndTheSelectionStaysWhereWindowsHasIt()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		fake.AudioEndpoints.IgnoreSet = true;
		AudioDevicesViewModel a = vm.Settings.Audio;
		a.SelectedOutput = a.Outputs.Single(i => i.Id == "headset");
		Settle();
		Assert.Equal("speaker", a.SelectedOutput!.Id);
		Assert.Equal(Text.AudioDeviceNotChanged(), a.ErrorText);
		Assert.True(a.CanChoose);
	}

	[AvaloniaFact]
	public void SetDefaultUnavailable_DisablesThePickers_WithAReason_EverythingElseWorks()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		fake.AudioEndpoints.ThrowOnSet = true;
		AudioDevicesViewModel a = vm.Settings.Audio;
		a.SelectedOutput = a.Outputs.Single(i => i.Id == "headset");
		Settle();
		Assert.False(a.CanChoose);
		Assert.True(a.ShowsUnavailable);
		Assert.Equal(Text.AudioDevicesUnavailable(), a.UnavailableText);
		Assert.Equal("speaker", a.SelectedOutput!.Id);
		// Nothing more reaches Windows.
		int sets = fake.AudioEndpoints.Sets.Count;
		a.SelectedInput = a.Inputs.Single(i => i.Id == "webcam");
		Settle();
		Assert.Equal(sets, fake.AudioEndpoints.Sets.Count);
		// The mic button still works.
		fake.RaiseShortcut(Kopru.Core.Input.ShortcutAction.MicButton);
		Settle();
		Assert.True(vm.Settings.MicEnabled);

		(MainWindowViewModel none, _) = Create(setup: f => f.NoAudioDevices = true);
		Assert.False(none.Settings.Audio.CanChoose);
		Assert.Equal(Text.AudioDevicesUnavailable(), none.Settings.Audio.UnavailableText);
	}

	// Controller microphone selected, "Turn Off" (the default): the press switches the feature; the tile, the LED (FollowMic)
	// and the toast follow; Low Power: nothing, no toast.
	[AvaloniaFact]
	public void ControllerMic_TurnOff_SwitchesTheFeature()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		SettingsState s = vm.Settings;
		QuickSettingsViewModel q = vm.Overview.Quick;
		List<string> toasts = [];
		s.ShortcutFeedback += (title, _) => toasts.Add(title);
		Assert.Equal(MicButtonMode.TurnOff, s.MicButtonMode);
		int applies = fake.Applies;

		fake.RaiseShortcut(Kopru.Core.Input.ShortcutAction.MicButton);
		Settle();
		Assert.True(s.MicEnabled);
		Assert.True(q.MicTile);
		Assert.Equal(Text.QuickStateOn(), q.MicStateText);
		Assert.True(fake.Settings.Mic.IsLive);
		Assert.Equal(MuteLedMode.FollowMic, fake.Settings.LightOptions.MuteLed);
		Assert.Equal(applies + 1, fake.Applies);
		Assert.Equal([Text.MicToastTitle(MicState.On)], toasts);

		fake.RaiseShortcut(Kopru.Core.Input.ShortcutAction.MicButton);
		Settle();
		Assert.False(s.MicEnabled);
		Assert.False(q.MicTile);
		Assert.Equal(Text.QuickStateOff(), q.MicStateText);
		Assert.False(fake.Settings.Mic.IsLive);
		Assert.Equal(Text.MicToastTitle(MicState.Off), toasts.Last());

		s.LowPowerOn = true;
		Settle();
		Assert.True(fake.Settings.Mic.Button);
		Assert.False(q.CanPressMic);
		int before = fake.Applies;
		fake.RaiseShortcut(Kopru.Core.Input.ShortcutAction.MicButton);
		Settle();
		Assert.False(s.MicEnabled);
		Assert.Equal(before, fake.Applies);
		Assert.Equal(2, toasts.Count);
	}

	// Controller microphone selected, "Mute": the stream stays on, the press mutes what games get; a first press while off
	// turns it on unmuted. The tile does the same as the button (without a toast).
	[AvaloniaFact]
	public void ControllerMic_Mute_KeepsTheStream()
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		SettingsState s = vm.Settings;
		QuickSettingsViewModel q = vm.Overview.Quick;
		List<string> toasts = [];
		s.ShortcutFeedback += (title, _) => toasts.Add(title);
		q.MicButtonModeIndex = 0;
		Assert.Equal(MicButtonMode.Mute, s.MicButtonMode);

		q.MicTile = true;
		Settle();
		Assert.True(s.MicEnabled);
		Assert.False(s.MicMuted);
		Assert.Empty(toasts);

		fake.RaiseShortcut(Kopru.Core.Input.ShortcutAction.MicButton);
		Settle();
		Assert.True(s.MicEnabled);
		Assert.True(fake.Settings.Mic.Enabled);
		Assert.True(fake.Settings.Mic.Muted);
		Assert.False(fake.Settings.Mic.IsLive);
		Assert.False(q.MicTile);
		Assert.Equal(Text.MicStateMuted(), q.MicStateText);
		Assert.Equal([Text.MicToastTitle(MicState.Muted)], toasts);

		q.MicTile = true;
		Settle();
		Assert.False(fake.Settings.Mic.Muted);
		Assert.True(q.MicTile);

		// Turning the feature off in Settings ends the mute.
		fake.RaiseShortcut(Kopru.Core.Input.ShortcutAction.MicButton);
		s.MicEnabled = false;
		Settle();
		Assert.False(s.MicMuted);
	}

	// Another PC microphone selected: the press mutes that endpoint (either mode, also under Low Power); the controller mic
	// stream is not started; the LED follows that microphone, also when it is muted elsewhere (polled).
	[AvaloniaTheory]
	[InlineData(MicButtonMode.TurnOff)]
	[InlineData(MicButtonMode.Mute)]
	public void PcMic_IsMutedAndUnmuted(MicButtonMode mode)
	{
		(MainWindowViewModel vm, FakeControllerService fake) = Create();
		SettingsState s = vm.Settings;
		QuickSettingsViewModel q = vm.Overview.Quick;
		List<(string Title, string Body)> toasts = [];
		s.ShortcutFeedback += (title, body) => toasts.Add((title, body));
		s.MicButtonMode = mode;
		s.MuteLed = MuteLedMode.FollowMic;
		s.Audio.SelectedInput = s.Audio.Inputs.Single(i => i.Id == "webcam");
		Settle();
		Assert.False(s.ControllerMicSelected);
		Assert.True(q.MicTile);
		Assert.True(fake.Settings.Mic.IsLive);

		fake.RaiseShortcut(Kopru.Core.Input.ShortcutAction.MicButton);
		Settle();
		Assert.True(fake.AudioEndpoints.GetMute("webcam"));
		Assert.False(s.MicEnabled);
		Assert.False(fake.Settings.Mic.Enabled);
		Assert.False(fake.Settings.Mic.IsLive);
		Assert.False(q.MicTile);
		Assert.Equal(Text.MicStateMuted(), q.MicStateText);
		Assert.Equal((Text.MicToastTitle(MicState.Muted), Text.MicToastBody(false)), toasts.Single());

		// Low Power: a PC microphone can still be switched.
		s.LowPowerOn = true;
		Settle();
		Assert.True(q.CanPressMic);
		q.MicTile = true;
		Settle();
		Assert.False(fake.AudioEndpoints.GetMute("webcam"));
		Assert.True(fake.Settings.Mic.IsLive);

		// Muted in Windows' own settings: the tile and the LED follow on the next poll.
		fake.AudioEndpoints.MuteElsewhere("webcam", true);
		fake.AudioDevices!.RefreshMute();
		Settle();
		Assert.False(q.MicTile);
		Assert.False(fake.Settings.Mic.IsLive);
	}

	[AvaloniaFact]
	public void MicButtonMode_IsGlobal_Saved_AndTheOldWindowsMuteOptionIsDropped()
	{
		string dir = Path.Combine(Path.GetTempPath(), "untethered-" + Guid.NewGuid().ToString("N"));
		string path = Path.Combine(dir, "settings.json");
		Directory.CreateDirectory(dir);
		try
		{
			File.WriteAllText(path, """{ "MicEnabled": true, "MicMuteWindows": true, "Profiles": [ { "Id": "default", "Name": "", "Values": { "MicEnabled": true, "MicMuteWindows": true } } ] }""");
			var state = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path));
			Assert.True(state.MicEnabled);
			Assert.Equal(MicButtonMode.TurnOff, state.MicButtonMode);
			var chat = state.Profiles.Create("Chat", state.CaptureValues())!;
			state.ActivateProfile(chat.Id);
			state.MicButtonMode = MicButtonMode.Mute;
			state.ActivateProfile(Kopru.Core.Profiles.ProfileBook.DefaultId);
			Assert.Equal(MicButtonMode.Mute, state.MicButtonMode);
			state.SaveNow();
			string json = File.ReadAllText(path);
			Assert.DoesNotContain("MicMuteWindows", json);
			Assert.Contains("\"MicButtonMode\": \"Mute\"", json);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	private static string RepoRoot()
	{
		DirectoryInfo? dir = new(AppContext.BaseDirectory);
		while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "screens")))
		{
			dir = dir.Parent;
		}
		return dir?.FullName ?? AppContext.BaseDirectory;
	}

	[Fact]
	public void ShortName_DropsNumberingAndMarks()
	{
		Assert.Equal("Hoparlör (Realtek Audio)", AudioDevicesViewModel.ShortName("Hoparlör (Realtek(R) Audio)"));
		Assert.Equal("Speakers (HyperX)", AudioDevicesViewModel.ShortName("Speakers (2- HyperX)"));
		Assert.Equal("Mic", AudioDevicesViewModel.ShortName("Mic"));
	}

	// The Overview at the default size and large, and the Settings sound panels; PNGs to docs/screens only with
	// KOPRU_RENDER_SCREENS=1. The quick settings card must hold every row without clipping.
	[AvaloniaFact]
	public void RenderQuickSettingsAndSoundPanels()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string outDir = Path.Combine(RepoRoot(), "docs", "screens");
		foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
		{
			Language? before = Localizer.Chosen;
			try
			{
				Localizer.Chosen = language;
				(MainWindowViewModel vm, FakeControllerService fake) = Create();
				var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
				window.Show();
				vm.SelectedNav = vm.NavItems[0];
				vm.Overview.ToggleBridgeCommand.Execute(null);
				fake.RaiseShortcut(Kopru.Core.Input.ShortcutAction.MicButton);
				Settle();
				OverviewView overview = window.GetLogicalDescendants().OfType<OverviewView>().Single();
				ToggleButton micTile = overview.GetLogicalDescendants().OfType<ToggleButton>().Single(t => t.Name == "MicTile");
				ComboBox picker = overview.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "QuickInputPicker");
				Border card = overview.GetLogicalDescendants().OfType<Border>().First(b => b.GetLogicalDescendants().Contains(micTile) && b.Classes.Contains("panel"));
				foreach ((double w, double h, string size) in new[] { (1100.0, 700.0, "default"), (1680.0, 1050.0, "large") })
				{
					window.Width = w;
					window.Height = h;
					Settle();
					// Every row ends inside the card.
					Avalonia.Point? bottom = picker.TranslatePoint(new Avalonia.Point(0, picker.Bounds.Height), card);
					Assert.NotNull(bottom);
					Assert.True(bottom!.Value.Y <= card.Bounds.Height, $"{code} {size}: picker ends below the card");
					ComboBox profile = overview.GetLogicalDescendants().OfType<ComboBox>().Last();
					Avalonia.Point? last = profile.TranslatePoint(new Avalonia.Point(0, profile.Bounds.Height), card);
					Assert.True(last!.Value.Y <= card.Bounds.Height - 8, $"{code} {size}: the profile row is clipped");
					using var frame = window.CaptureRenderedFrame();
					Assert.NotNull(frame);
					if (writeFiles)
					{
						frame!.Save(Path.Combine(outDir, $"overview-audio-{size}-{code}.png"));
					}
				}

				// Settings: the speaker and microphone groups.
				vm.SelectedNav = vm.NavItems.Last();
				window.Height = 1400;
				Settle();
				SettingsView settings = window.GetLogicalDescendants().OfType<SettingsView>().Single();
				Border speaker = settings.GetLogicalDescendants().OfType<Border>().Single(b => b.Name == "SpeakerPanel");
				speaker.BringIntoView();
				Settle();
				using (var page = window.CaptureRenderedFrame())
				{
					if (writeFiles)
					{
						page!.Save(Path.Combine(outDir, $"settings-audio-speaker-{code}.png"));
					}
				}
				Border mic = settings.GetLogicalDescendants().OfType<Border>().Single(b => b.Name == "MicPanel");
				mic.BringIntoView();
				Settle();
				using (var page = window.CaptureRenderedFrame())
				{
					if (writeFiles)
					{
						page!.Save(Path.Combine(outDir, $"settings-audio-mic-{code}.png"));
					}
				}
				// Pickers unavailable: one line why, inside the card (the error of the refused choice is not shown then).
				window.Width = 1100;
				window.Height = 700;
				vm.SelectedNav = vm.NavItems[0];
				fake.AudioEndpoints.ThrowOnSet = true;
				vm.Settings.Audio.SelectedOutput = vm.Settings.Audio.Outputs.Single(i => i.Id == "headset");
				Settle();
				ComboBox profileRow = overview.GetLogicalDescendants().OfType<ComboBox>().Last();
				Assert.True(profileRow.TranslatePoint(new Avalonia.Point(0, profileRow.Bounds.Height), card)!.Value.Y <= card.Bounds.Height);
				using (var unavailable = window.CaptureRenderedFrame())
				{
					if (writeFiles)
					{
						unavailable!.Save(Path.Combine(outDir, $"overview-audio-unavailable-{code}.png"));
					}
				}

				window.Close();
			}
			finally
			{
				Localizer.Chosen = before;
			}
		}
	}
}
