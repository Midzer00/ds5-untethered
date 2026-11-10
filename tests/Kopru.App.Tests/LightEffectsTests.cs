// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Kopru.App.Services;
using Kopru.Core.Bridge;
using Kopru.App.ViewModels;
using Kopru.Core.Lighting;

namespace Kopru.App.Tests;

public class LightEffectsTests
{
	[Fact]
	public void Settings_RoundTripThroughJson()
	{
		string dir = Path.Combine(Path.GetTempPath(), "untethered-" + Guid.NewGuid().ToString("N"));
		string path = Path.Combine(dir, "settings.json");
		Directory.CreateDirectory(dir);
		try
		{
			var store = new JsonSettingsStore(path);
			var service = new FakeControllerService(animate: false);
			var state = new SettingsState(service, store)
			{
				LightMode = LightMode.Breathing,
				BreathingPeriod = 6,
				BreathingMin = 30,
				BreathingMax = 90,
				RainbowSpeed = 7,
				PlayerLedPattern = PlayerLedPattern.Three,
				PlayerLedBrightness = LedBrightness.Medium,
				MuteLed = MuteLedMode.On
			};
			state.SaveNow();
			Assert.Contains("\"Breathing\"", File.ReadAllText(path));

			var loaded = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path));
			Assert.Equal(LightMode.Breathing, loaded.LightMode);
			Assert.Equal(6, loaded.BreathingPeriod);
			Assert.Equal(30, loaded.BreathingMin);
			Assert.Equal(90, loaded.BreathingMax);
			Assert.Equal(7, loaded.RainbowSpeed);
			Assert.Equal(PlayerLedPattern.Three, loaded.PlayerLedPattern);
			Assert.True(loaded.PlayerLeds);
			Assert.Equal(LedBrightness.Medium, loaded.PlayerLedBrightness);
			Assert.Equal(MuteLedMode.On, loaded.MuteLed);
			Assert.Equal(new LightOptions(LightMode.Breathing, 6, 0.3, 0.9, 7, PlayerLedPattern.Three, LedBrightness.Medium, MuteLedMode.On),
				loaded.ToSettings().LightOptions);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[Theory]
	[InlineData(true, PlayerLedPattern.One)]
	[InlineData(false, PlayerLedPattern.Off)]
	public void OldFiles_LoadAsStatic_WithThePatternFromPlayerLeds(bool playerLeds, PlayerLedPattern expected)
	{
		string dir = Path.Combine(Path.GetTempPath(), "untethered-" + Guid.NewGuid().ToString("N"));
		string path = Path.Combine(dir, "settings.json");
		Directory.CreateDirectory(dir);
		try
		{
			File.WriteAllText(path, $$"""{ "LightbarColour": 16711680, "Brightness": 50, "PlayerLeds": {{(playerLeds ? "true" : "false")}} }""");
			var state = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path));
			Assert.Equal(LightMode.Static, state.LightMode);
			Assert.Equal(expected, state.PlayerLedPattern);
			Assert.Equal(playerLeds, state.PlayerLeds);
			Assert.Equal(LedBrightness.High, state.PlayerLedBrightness);
			Assert.Equal(MuteLedMode.Off, state.MuteLed);
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[Fact]
	public void PlayerLedsSwitch_AndPattern_StayInStep_AndReachTheService()
	{
		var service = new FakeControllerService(animate: false);
		var state = new SettingsState(service);
		state.PlayerLedPattern = PlayerLedPattern.All;
		Assert.Equal(0x1F, service.Settings.Light.PlayerMask);
		state.PlayerLeds = false;
		Assert.Equal(PlayerLedPattern.Off, state.PlayerLedPattern);
		Assert.Equal(0, service.Settings.Light.PlayerMask);
		state.PlayerLeds = true;
		Assert.Equal(PlayerLedPattern.One, state.PlayerLedPattern);
		state.MuteLed = MuteLedMode.On;
		Assert.Equal(MuteLedMode.On, service.Settings.Light.Options.MuteLed);
	}

	[Fact]
	public void GameControlledMode_AlwaysLetsTheGamesLightThrough()
	{
		var s = new ControllerSettings(LightFromGame: false);
		Assert.False(s.Supported.LightFromGame);
		Assert.True((s with { LightOptions = new LightOptions(LightMode.Game) }).Supported.LightFromGame);
		Assert.True((s with { LightFromGame = true, LightOptions = new LightOptions(LightMode.Rainbow) }).Supported.LightFromGame);
	}

	[AvaloniaFact]
	public void LightPage_ModePicker_ShowsTheModesSettings_AndAnimatesThePreview()
	{
		var service = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(service);
		LightViewModel light = vm.Light;
		Assert.Equal(5, light.Modes.Count);
		Assert.Equal(6, light.Patterns.Count);
		Assert.Equal(3, light.LedLevels.Count);
		// Follow mic mute is offered with the microphone feature.
		Assert.Equal([MuteLedMode.Off, MuteLedMode.On, MuteLedMode.FollowMic], light.MuteChoices.Cast<LightChoice<MuteLedMode>>().Select(c => c.Value));
		Assert.Equal(LightMode.Static, ((LightChoice<LightMode>)light.SelectedMode!).Value);
		Assert.True(light.ShowsColour);
		Assert.False(light.IsPreviewAnimated);

		light.SelectedMode = light.Modes[2];
		Assert.Equal(LightMode.Rainbow, vm.Settings.LightMode);
		Assert.Equal(LightMode.Rainbow, service.Settings.LightOptions.Mode);
		Assert.True(light.ShowsRainbow);
		Assert.False(light.ShowsColour);
		Assert.True(light.IsPreviewAnimated);
		light.UpdatePreview(TimeSpan.Zero);
		Color first = light.PreviewColor;
		light.UpdatePreview(TimeSpan.FromSeconds(3));
		Assert.NotEqual(first, light.PreviewColor);

		light.SelectedMode = light.Modes[1];
		Assert.True(light.ShowsBreathing);
		Assert.True(light.ShowsColour);

		// Battery colour with the fake's 78 %: green, static.
		light.SelectedMode = light.Modes[3];
		Assert.False(light.IsPreviewAnimated);
		Assert.Equal(Color.FromRgb(0x20, 0xE0, 0x40), light.PreviewColor);
		service.BatteryPercent = 5;
		Assert.True(light.IsPreviewAnimated);

		light.SelectedPattern = light.Patterns[5];
		Assert.Equal(0x1F, light.PreviewPlayerMask);
		light.SelectedLedLevel = light.LedLevels[0];
		Assert.Equal(LedBrightness.Low, service.Settings.LightOptions.PlayerBrightness);
		light.SelectedMute = light.MuteChoices[1];
		Assert.Equal(MuteLedMode.On, service.Settings.LightOptions.MuteLed);
	}
}
