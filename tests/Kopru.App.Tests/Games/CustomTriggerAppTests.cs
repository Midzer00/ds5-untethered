// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;
using Kopru.Core.Protocol;

namespace Kopru.App.Tests;

// Custom Trigger in a game's pop-up: per profile and per trigger (L2 and R2 apart), Game's Effect by default, each effect's
// sliders, Try playing both triggers, saved and loaded (older settings: Game's Effect); Haptics And Triggers keeps its
// fallback effect as it was.
public class CustomTriggerAppTests
{
	[AvaloniaFact]
	public void Defaults_AreGamesEffect_OnBothTriggers()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		var feel = new ProfileFeelViewModel(state);
		Assert.Equal(CustomTrigger.Default, state.LeftCustomTrigger);
		Assert.Equal(CustomTrigger.Default, state.RightCustomTrigger);
		Assert.Equal(0, feel.LeftTrigger.EffectIndex);
		Assert.False(feel.LeftTrigger.IsSet);
		Assert.Equal([Text.CustomTriggerGameEffect(), Text.CustomTriggerResistance(), Text.EffectWeapon(), Text.EffectVibration()], feel.LeftTrigger.Choices.Select(c => c.Name));
		Assert.Equal((Text.CustomTriggerLeft(), Text.CustomTriggerRight()), (feel.LeftTrigger.Label, feel.RightTrigger.Label));
		Assert.Equal((20, 20, 60, 20, 30, 50), (feel.LeftTrigger.ResistanceStart, feel.LeftTrigger.WeaponStart, feel.LeftTrigger.WeaponEnd,
			feel.LeftTrigger.VibrationStart, feel.LeftTrigger.Frequency, feel.LeftTrigger.Force));
		Assert.Null(fake.Settings.Game.LeftCustom.Bytes());
	}

	[AvaloniaFact]
	public void EachTrigger_HasItsOwnEffect_AndSliders_PushedLive()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		var feel = new ProfileFeelViewModel(state);
		feel.LeftTrigger.EffectIndex = 1;
		feel.LeftTrigger.ResistanceStart = 40;
		feel.LeftTrigger.Force = 100;
		feel.RightTrigger.EffectIndex = 3;
		feel.RightTrigger.VibrationStart = 60;
		feel.RightTrigger.Frequency = 45;
		feel.RightTrigger.Force = 25;
		Assert.True(feel.LeftTrigger.IsResistance);
		Assert.True(feel.RightTrigger.IsVibration);
		Assert.Equal(TriggerEffects.Feedback(4, 8), fake.Settings.Game.LeftCustom.Bytes());
		Assert.Equal(TriggerEffects.Vibration(6, 2, 45), fake.Settings.Game.RightCustom.Bytes());
		Assert.Equal(TriggerEffects.Vibration(6, 2, 45), GameProfiles.For(fake.Settings.Game).CustomRightTrigger);
		Assert.Equal(Text.PercentValue(25), feel.RightTrigger.ForceText);
		Assert.Equal("45 Hz", feel.RightTrigger.FrequencyText);
		// Into the active profile.
		Assert.Equal(state.LeftCustomTrigger, state.Profiles.Default.Values.LeftCustomTrigger);

		// The weapon's start and end push each other; the left trigger keeps its own values.
		feel.RightTrigger.EffectIndex = 2;
		feel.RightTrigger.WeaponStart = 70;
		Assert.Equal((70, 80), (feel.RightTrigger.WeaponStart, feel.RightTrigger.WeaponEnd));
		feel.RightTrigger.WeaponEnd = 30;
		Assert.Equal((20, 30), (feel.RightTrigger.WeaponStart, feel.RightTrigger.WeaponEnd));
		Assert.Equal(TriggerEffects.Feedback(4, 8), fake.Settings.Game.LeftCustom.Bytes());
		// The fallback is a different setting and stays as it was.
		Assert.Equal(FallbackTrigger.Off, state.FallbackTrigger);

		feel.LeftTrigger.EffectIndex = 0;
		Assert.Null(fake.Settings.Game.LeftCustom.Bytes());
		Assert.Null(GameProfiles.For(fake.Settings.Game).CustomLeftTrigger);
	}

	[AvaloniaFact]
	public void AGamesPopUp_EditsItsProfile_AndTryPlaysBothTriggers()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake);
		fake.Watcher.Exit("Cyberpunk2077.exe");
		fake.Games.Tick();
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == "Returnal"));
		panel.UseOwnSettingsCommand.Execute(null);
		panel.SaveProfileNameCommand.Execute(null);
		Assert.False(panel.Feel.IsLive);
		Assert.False(panel.CanTryCustomTriggers);
		panel.TryCustomTriggersCommand.Execute(null);
		Assert.Null(fake.TestedTriggers);

		panel.Feel.RightTrigger.EffectIndex = 2;
		Assert.True(panel.CanTryCustomTriggers);
		ProfileValues own = vm.Settings.Profiles.Find(panel.Feel.ProfileId)!.Values;
		Assert.Equal(FallbackTrigger.Weapon, own.RightCustomTrigger.Effect);
		Assert.Equal(CustomTrigger.Default, own.LeftCustomTrigger);
		// The active (default) profile is untouched.
		Assert.Equal(CustomTrigger.Default, vm.Settings.RightCustomTrigger);

		panel.TryCustomTriggersCommand.Execute(null);
		Assert.Equal(TriggerEffects.Weapon(2, 6, 4), fake.TestedTriggers!.Value.Right);
		Assert.Equal(TriggerEffects.Off(), fake.TestedTriggers!.Value.Left);
	}

	[AvaloniaFact]
	public void SavedAndLoaded_AndOldSettingsLoadGamesEffect()
	{
		string path = Path.Combine(Path.GetTempPath(), $"untethered-customtrigger-{Guid.NewGuid():N}.json");
		try
		{
			var state = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path));
			var feel = new ProfileFeelViewModel(state);
			feel.LeftTrigger.EffectIndex = 3;
			feel.LeftTrigger.Force = 90;
			feel.RightTrigger.EffectIndex = 1;
			state.SaveNow();

			var again = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path));
			Assert.Equal(FallbackTrigger.Vibration, again.LeftCustomTrigger.Effect);
			Assert.Equal(90, again.LeftCustomTrigger.Shape.Force);
			Assert.Equal(FallbackTrigger.Feedback, again.RightCustomTrigger.Effect);
		}
		finally
		{
			File.Delete(path);
		}

		var old = new SettingsState(new FakeControllerService(animate: false), new MemorySettingsStore(new AppSettings
		{
			Profiles = [new ControllerProfile(ProfileBook.DefaultId, "Default", new ProfileValues { FallbackTrigger = FallbackTrigger.Weapon, FallbackStrength = 75 })]
		}));
		Assert.Equal(CustomTrigger.Default, old.LeftCustomTrigger);
		Assert.Equal(CustomTrigger.Default, old.RightCustomTrigger);
		Assert.Equal(FallbackTrigger.Weapon, old.FallbackTrigger);
		Assert.Equal(TriggerEffects.Weapon(2, 6, 6), old.ToSettings().Game.TriggerEffect);
	}

	// The pop-up: one header row per trigger and only its effect's rows; Haptics And Triggers keeps its fallback rows and no
	// Custom Trigger.
	[AvaloniaFact]
	public async Task PopUp_ShowsEachEffectsRows_TheGeneralPageKeepsTheFallback()
	{
		(MainWindowViewModel vm, MainWindow window) = await GamePanelTests.OpenPanel("supported", "en", 1100, 700);
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		panel.SelectedSegment = GamePanelViewModel.TriggersSegment;
		FitCheck.Settle(window);
		GamePanelView view = window.GetVisualDescendants().OfType<GamePanelView>().Single();
		StackPanel Block(string side) => view.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == $"Panel{side}TriggerBlock");
		List<string> Labels(string side) => Block(side).GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("panelRow") && g.IsEffectivelyVisible)
			.Select(g => g.Children.OfType<TextBlock>().First().Text ?? string.Empty).ToList();
		string start = Text.CustomTriggerStart(), end = Text.CustomTriggerEnd(), force = Text.CustomTriggerForce(), frequency = Text.FallbackFrequency();
		string effect = Text.CustomTriggerEffectLabel();
		Assert.Equal([effect], Labels("Left"));
		Assert.Equal([effect], Labels("Right"));
		// A card per trigger, side by side, each showing only its rows; the cards keep the room of the tallest state, so Try
		// (in the footer under them) never moves.
		Avalonia.Rect RectOf(string name) => FitCheck.Bounds(view.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name), window);
		double tryTop = RectOf("TryCustomTriggerRow").Top;
		void NoHoles(string at)
		{
			FitCheck.Settle(window);
			Avalonia.Rect cards = RectOf("PanelCustomTrigger"), tryRow = RectOf("TryCustomTriggerRow"), right = RectOf("PanelRightTriggerBlock");
			Assert.True(right.Bottom <= cards.Bottom + 0.5, $"{at}: the right block {right} outside the cards {cards}");
			Assert.True(tryRow.Top >= cards.Bottom, $"{at}: Try {tryRow} not under the cards {cards}");
			Assert.Equal(tryTop, tryRow.Top, 0.5);
		}
		NoHoles("game's effect");
		Assert.Equal(RectOf("PanelLeftTriggerHeader").Top, RectOf("PanelRightTriggerHeader").Top, 0.5);
		panel.Feel.LeftTrigger.EffectIndex = 1;
		panel.Feel.RightTrigger.EffectIndex = 2;
		FitCheck.Settle(window);
		NoHoles("resistance and weapon");
		Assert.Equal([effect, start, force], Labels("Left"));
		Assert.Equal([effect, start, end, force], Labels("Right"));
		panel.Feel.LeftTrigger.EffectIndex = 3;
		FitCheck.Settle(window);
		Assert.Equal([effect, start, force, frequency], Labels("Left"));
		NoHoles("vibration and weapon");
		// The tallest state fills the room the column keeps.
		panel.Feel.RightTrigger.EffectIndex = 3;
		FitCheck.Settle(window);
		NoHoles("vibration and vibration");
		Assert.True(RectOf("PanelCustomTrigger").Bottom - RectOf("PanelRightTriggerBlock").Bottom < 16, "the tallest state fills the kept room");
		Assert.True(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TryCustomTriggerButton").IsEffectivelyEnabled);
		window.Close();

		var main = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = "en" })));
		var page = new MainWindow { DataContext = main, Width = 1100, Height = 700 };
		page.Show();
		try
		{
			main.Navigate(main.OtherGames);
			main.OtherGames.SelectedTab = OtherGamesViewModel.RumbleTab;
			FitCheck.Settle(page);
			OtherGamesView haptics = page.GetVisualDescendants().OfType<OtherGamesView>().Single();
			Assert.Contains(haptics.GetVisualDescendants().OfType<Control>(), c => c.Name == "FallbackPicker" && c.IsEffectivelyVisible);
			Assert.Contains(haptics.GetVisualDescendants().OfType<Control>(), c => c.Name == "FallbackStrengthSlider");
			Assert.DoesNotContain(haptics.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Text.CustomTriggerSection());
		}
		finally
		{
			page.Close();
		}
	}
}
