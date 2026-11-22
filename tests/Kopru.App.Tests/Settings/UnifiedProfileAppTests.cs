// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Games;
using Kopru.Core.Lighting;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;
using Kopru.Core.Virtual;

namespace Kopru.App.Tests;

// The settings model without DualSense Mode: one profile, one light switch, one speaker switch, the controller type with a
// pending change while a game runs. And the one-time migration of older settings files.
public class UnifiedProfileAppTests
{
	private static AppSettings Old(bool? modeOn, bool? migrated, bool? modeSpeaker, LightMode light = LightMode.Static, bool lightFromGame = true,
		bool speaker = false) => new()
	{
		SupportedModeOn = modeOn,
		DualSenseModeMigrated = migrated,
		DualSenseModeSpeaker = modeSpeaker,
		LightMode = light,
		LightFromGame = lightFromGame,
		SpeakerEnabled = speaker,
		Profiles =
		[
			new ControllerProfile(ProfileBook.DefaultId, "Default", new ProfileValues { LightMode = light, LightFromGame = lightFromGame, SpeakerEnabled = speaker }),
			new ControllerProfile("racing", "Racing", new ProfileValues { LightMode = LightMode.Game, LightFromGame = false, SpeakerEnabled = speaker }),
			new ControllerProfile("chill", "Chill", new ProfileValues { LightMode = LightMode.Rainbow, LightFromGame = true, SpeakerEnabled = !speaker })
		]
	};

	// Mode on: every profile's speaker switch becomes the mode's speaker switch; lights stay (Game becomes Static + game light).
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Migration_ModeOn_SpeakerIsTheModesSpeakerInEveryProfile(bool modeSpeaker)
	{
		AppSettings s = Old(true, true, modeSpeaker);
		Assert.True(s.MigrateUnifiedProfile());
		Assert.Equal(modeSpeaker, s.SpeakerEnabled);
		Assert.All(s.Profiles!, p => Assert.Equal(modeSpeaker, p.Values.SpeakerEnabled));
		Assert.Equal((LightMode.Static, true), (s.Profiles![1].Values.LightMode, s.Profiles[1].Values.LightFromGame));
		Assert.Equal((LightMode.Rainbow, true), (s.Profiles[2].Values.LightMode, s.Profiles[2].Values.LightFromGame));
		Assert.Null(s.SupportedModeOn);
		Assert.Null(s.DualSenseModeMigrated);
		Assert.Null(s.DualSenseModeSpeaker);
		Assert.True(s.UnifiedProfile);
		Assert.False(s.MigrateUnifiedProfile());
	}

	// Mode off: the game's light was blocked, so it goes off where the light mode was not Game; the speaker switches stay.
	[Fact]
	public void Migration_ModeOff_KeepsTheUsersLight_AndTheSpeakerSwitches()
	{
		AppSettings s = Old(false, true, modeSpeaker: true, speaker: false);
		Assert.True(s.MigrateUnifiedProfile());
		Assert.False(s.LightFromGame);
		Assert.False(s.SpeakerEnabled);
		Assert.False(s.Profiles![0].Values.LightFromGame);
		// The Game light mode let the game's light through even with the mode off.
		Assert.Equal((LightMode.Static, true), (s.Profiles[1].Values.LightMode, s.Profiles[1].Values.LightFromGame));
		Assert.False(s.Profiles[2].Values.LightFromGame);
		Assert.Equal([false, false, true], s.Profiles.Select(p => p.Values.SpeakerEnabled));
	}

	// A file that never had the mode's one-time step counts as mode on (that step turned it on); missing fields read as on.
	[Theory]
	[InlineData(false, false)]
	[InlineData(null, null)]
	public void Migration_NotYetMigratedFile_CountsAsModeOn(bool? modeOn, bool? migrated)
	{
		AppSettings s = Old(modeOn, migrated, null, lightFromGame: true, speaker: false);
		s.MigrateUnifiedProfile();
		Assert.True(s.LightFromGame);
		Assert.True(s.SpeakerEnabled);
	}

	// The settings-level Game light mode too.
	[Fact]
	public void Migration_GameLightMode_InTheSettings_BecomesStaticWithTheGamesLight()
	{
		AppSettings s = Old(false, true, true, LightMode.Game, lightFromGame: false);
		s.MigrateUnifiedProfile();
		Assert.Equal((LightMode.Static, true), (s.LightMode, s.LightFromGame));
	}

	// The app runs the migration once on load, saves the marker at once and drops the old fields.
	[AvaloniaFact]
	public void App_MigratesOnLoad_AndSavesTheMarker()
	{
		AppSettings saved = Old(true, true, modeSpeaker: false, speaker: true);
		saved.ActiveProfile = "racing";
		var store = new MemorySettingsStore(saved);
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake, store);
		Assert.False(state.SpeakerEnabled);
		Assert.Equal((LightMode.Static, true), (state.LightMode, state.LightFromGame));
		Assert.True(SpinWait.SpinUntil(() => store.Saves > 0, 3000));
		Assert.True(store.Saved!.UnifiedProfile);
		Assert.Null(store.Saved.SupportedModeOn);
		Assert.Null(store.Saved.DualSenseModeSpeaker);
		Assert.All(store.Saved.Profiles!, p => Assert.False(p.Values.SpeakerEnabled));
	}

	// An exported profile from before: imports with the Game light mode turned into Static with the game's light.
	[AvaloniaFact]
	public void App_ImportsAnOldExport()
	{
		var state = new SettingsState(new FakeControllerService(animate: false));
		const string json = """
			{ "format": "untethered-profile", "version": 1, "name": "Old", "values": { "lightMode": "Game", "lightFromGame": false } }
			""";
		ControllerProfile p = state.Profiles.Import(json, "x")!;
		Assert.Equal((LightMode.Static, true), (p.Values.LightMode, p.Values.LightFromGame));
	}

	// The Light page no longer offers "Game Controlled".
	[AvaloniaFact]
	public void LightPage_HasNoGameMode()
	{
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
		Assert.DoesNotContain(vm.Light.Modes, m => m.Value == LightMode.Game);
	}

	[AvaloniaFact]
	public void PadType_PendingWhileTheGameRuns_ApplyNow_AndTheSwapEvent()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		fake.Games.Tick();
		string key = fake.Games.ActiveGame!.Key;
		Assert.Equal(VirtualPadType.DualSense, state.CurrentPadType);
		Assert.Equal(Text.GameSees("DualSense"), state.GameSeesText);

		var changed = new List<string?>();
		state.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
		fake.Games.SetPadType(key, VirtualPadType.Xbox360);
		Assert.True(state.HasPendingPadType);
		Assert.Equal(VirtualPadType.Xbox360, state.PendingPadType);
		Assert.Equal(VirtualPadType.DualSense, state.CurrentPadType);
		Assert.Contains(nameof(SettingsState.HasPendingPadType), changed);
		Assert.Equal(Text.PadTypeDeferred(), state.PendingPadTypeText);

		List<PadSwap> swaps = [];
		state.PadSwapped += swaps.Add;
		state.ApplyPadTypeNowCommand.Execute(null);
		Assert.False(state.HasPendingPadType);
		Assert.Equal(VirtualPadType.Xbox360, state.CurrentPadType);
		Assert.Equal(Text.GameSees("Xbox 360"), state.GameSeesText);
		PadSwap swap = Assert.Single(swaps);
		Assert.Equal((VirtualPadType.DualSense, VirtualPadType.Xbox360, key), (swap.From, swap.To, swap.GameKey));

		// The game exits: back to the default type.
		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Games.Tick();
		Assert.Equal(VirtualPadType.DualSense, state.CurrentPadType);
		Assert.Equal(2, swaps.Count);
	}

	// The game's light and the speaker: what really plays, given Low Power and the controller type.
	[AvaloniaFact]
	public void LightAndSpeaker_Work_OnlyWhereTheyCan()
	{
		var fake = new FakeControllerService(animate: false);
		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Games.Tick();
		var state = new SettingsState(fake);
		Assert.True(state.SpeakerWorks);
		Assert.True(state.GameLightWorks);

		state.LowPowerOn = true;
		Assert.False(state.SpeakerWorks);
		Assert.False(state.GameLightWorks);
		Assert.False(fake.Settings.Speaker.Enabled);
		Assert.False(fake.Settings.LightFromGame);
		state.LowPowerOn = false;

		fake.Games.SetForcedPadType(VirtualPadType.DualShock4);
		Assert.False(state.SpeakerWorks);
		Assert.True(state.GameLightWorks);
		fake.Games.SetForcedPadType(VirtualPadType.Xbox360);
		Assert.False(state.SpeakerWorks);
		Assert.False(state.GameLightWorks);
		// The switch itself stays as the user set it.
		Assert.True(state.SpeakerEnabled);
		Assert.True(state.LightFromGame);
	}

	[Fact]
	public void NewTexts_ReadAsDecided()
	{
		Assert.Equal("Varsayılan Kumanda Türü", Text.PadTypeDefaultLabel(Language.Turkish));
		Assert.Equal("Otomatik (Oyuna Göre)", Text.PadChoiceAutoPerGame(Language.Turkish));
		Assert.Equal("The Game Sees: Xbox 360", Text.GameSees("Xbox 360", Language.English));
		Assert.Equal("Oyunu Yeniden Başlatınca Geçerli Olur", Text.PadTypeDeferred(Language.Turkish));
		Assert.Equal("Şimdi Uygula", Text.PadTypeApplyNow(Language.Turkish));
		Assert.Equal("Oyun kumandayı görmüyorsa oyunu yeniden başlat.", Text.PadSwapRestartHint(Language.Turkish));
		Assert.Equal("Use The Game's Light", Text.LightFromGame(Language.English));
		Assert.Equal("Kumanda Hoparlörü", Text.GroupControllerSpeaker(Language.Turkish));
		Assert.Equal("Titreşim ve Tetikler", Text.PageHapticsTriggers(Language.Turkish));
	}
}
