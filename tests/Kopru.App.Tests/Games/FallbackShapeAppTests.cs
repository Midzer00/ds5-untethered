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

// The fallback trigger effect's shape on Haptics And Triggers and in a game's pop-up: percent of the trigger's travel
// (a zone is 10 %), the weapon's start and end pushing each other, per profile, saved and loaded, played by Try Trigger
// Effect, and the row shown only while an effect is chosen.
public class FallbackShapeAppTests
{
	[AvaloniaFact]
	public void Defaults_AreTheOldEffect_InPercent()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		var feel = new ProfileFeelViewModel(state);
		Assert.Equal(FallbackShape.Default, state.FallbackShape);
		Assert.Equal((20, 20, 60, 20, 30), (feel.FeedbackStart, feel.WeaponStart, feel.WeaponEnd, feel.VibrationStart, feel.VibrationFrequency));
		Assert.Equal(Text.PercentValue(20), feel.FeedbackStartText);
		Assert.Equal(Text.PercentValue(60), feel.WeaponEndText);
		Assert.Equal("30 Hz", feel.VibrationFrequencyText);
		Assert.Equal(FallbackShape.Default, fake.Settings.FallbackShape);
	}

	[AvaloniaFact]
	public void Sliders_EditTheActiveProfile_PushTheShape_AndTheTryButtonPlaysIt()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		var feel = new ProfileFeelViewModel(state);
		feel.FallbackIndex = 3;
		feel.FallbackStrength = 100;
		feel.VibrationStart = 50;
		feel.VibrationFrequency = 45;
		Assert.Equal(new FallbackShape { VibrationStart = 5, VibrationFrequency = 45 }, state.FallbackShape);
		Assert.Equal(state.FallbackShape, fake.Settings.FallbackShape);
		Assert.Equal(state.FallbackShape, state.Profiles.Default.Values.FallbackShape);
		Assert.Equal("45 Hz", feel.VibrationFrequencyText);
		Assert.Equal(TriggerEffects.Vibration(5, 8, 45), RealControllerService.FallbackTestEffect(fake.Settings));
		Assert.Equal(TriggerEffects.Vibration(5, 8, 45), fake.Settings.Game.TriggerEffect);

		feel.FallbackIndex = 1;
		feel.FeedbackStart = 90;
		Assert.Equal(TriggerEffects.Feedback(9, 8), RealControllerService.FallbackTestEffect(fake.Settings));
		// The controller's output tests stay the fixed presets.
		Assert.Equal(TriggerEffects.Feedback(2, 8), RealControllerService.TriggerEffect(TriggerTestEffect.Feedback, 100));

		feel.FallbackIndex = 2;
		feel.WeaponStart = 40;
		feel.WeaponEnd = 70;
		Assert.Equal(TriggerEffects.Weapon(4, 7, 8), RealControllerService.FallbackTestEffect(fake.Settings));
		// Each effect keeps its own shape.
		Assert.Equal((90, 50), (feel.FeedbackStart, feel.VibrationStart));
	}

	[AvaloniaFact]
	public void WeaponStartAndEnd_PushEachOther()
	{
		var state = new SettingsState(new FakeControllerService(animate: false));
		var feel = new ProfileFeelViewModel(state);
		List<string?> changed = [];
		feel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

		feel.WeaponStart = 60;
		Assert.Equal((60, 70), (feel.WeaponStart, feel.WeaponEnd));
		Assert.Contains(string.Empty, changed);
		feel.WeaponStart = 70;
		Assert.Equal((70, 80), (feel.WeaponStart, feel.WeaponEnd));
		feel.WeaponEnd = 30;
		Assert.Equal((20, 30), (feel.WeaponStart, feel.WeaponEnd));
		feel.WeaponEnd = 50;
		Assert.Equal((20, 50), (feel.WeaponStart, feel.WeaponEnd));
		// Out of the sliders' ranges: clamped.
		feel.WeaponStart = 0;
		Assert.Equal((20, 50), (feel.WeaponStart, feel.WeaponEnd));
		feel.WeaponEnd = 100;
		Assert.Equal((20, 80), (feel.WeaponStart, feel.WeaponEnd));
		feel.VibrationFrequency = 1;
		Assert.Equal(5, feel.VibrationFrequency);
		feel.VibrationFrequency = 90;
		Assert.Equal(60, feel.VibrationFrequency);
	}

	[AvaloniaFact]
	public void AnotherProfile_IsEditedInTheBook_AndCopiedWithTheProfile()
	{
		var fake = new FakeControllerService(animate: false);
		var state = new SettingsState(fake);
		ControllerProfile racing = state.Profiles.Create("Racing", state.CaptureValues())!;
		var other = new ProfileFeelViewModel(state, () => racing.Id);
		other.WeaponEnd = 80;
		other.WeaponStart = 50;
		Assert.Equal(new FallbackShape { WeaponStart = 5, WeaponEnd = 8 }, state.Profiles.Find(racing.Id)!.Values.FallbackShape);
		Assert.Equal(FallbackShape.Default, state.FallbackShape);
		Assert.Equal(FallbackShape.Default, fake.Settings.FallbackShape);

		// A per-controller profile carries it too.
		Assert.Equal(new FallbackShape { WeaponStart = 5, WeaponEnd = 8 }, state.SettingsFor(state.Profiles.Find(racing.Id)!.Values).FallbackShape);

		state.ActivateProfile(racing.Id);
		Assert.Equal(new FallbackShape { WeaponStart = 5, WeaponEnd = 8 }, state.FallbackShape);
		Assert.Equal(new FallbackShape { WeaponStart = 5, WeaponEnd = 8 }, fake.Settings.FallbackShape);
		ControllerProfile copy = state.Profiles.Duplicate(racing.Id, "Copy")!;
		Assert.Equal(new FallbackShape { WeaponStart = 5, WeaponEnd = 8 }, copy.Values.FallbackShape);
	}

	[AvaloniaFact]
	public void SavedAndLoaded_AndOldSettingsLoadTheDefaultShape()
	{
		string path = Path.Combine(Path.GetTempPath(), $"untethered-fallbackshape-{Guid.NewGuid():N}.json");
		try
		{
			var state = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path));
			var feel = new ProfileFeelViewModel(state);
			feel.FeedbackStart = 70;
			feel.WeaponEnd = 40;
			feel.VibrationFrequency = 15;
			state.SaveNow();

			var again = new SettingsState(new FakeControllerService(animate: false), new JsonSettingsStore(path));
			Assert.Equal(new FallbackShape { FeedbackStart = 7, WeaponEnd = 4, VibrationFrequency = 15 }, again.FallbackShape);
		}
		finally
		{
			File.Delete(path);
		}

		// A settings file from before the shape (and one from before profiles) loads the effects as they were.
		var old = new SettingsState(new FakeControllerService(animate: false), new MemorySettingsStore(new AppSettings
		{
			Profiles = [new ControllerProfile(ProfileBook.DefaultId, "Default", new ProfileValues { FallbackTrigger = FallbackTrigger.Weapon })]
		}));
		Assert.Equal(FallbackTrigger.Weapon, old.FallbackTrigger);
		Assert.Equal(FallbackShape.Default, old.FallbackShape);
		var older = new SettingsState(new FakeControllerService(animate: false), new MemorySettingsStore(new AppSettings { FallbackTrigger = FallbackTrigger.Feedback }));
		Assert.Equal(FallbackShape.Default, older.FallbackShape);
		Assert.Equal(TriggerEffects.Feedback(2, 4), older.ToSettings().Game.TriggerEffect);
	}

	// The fallback's shape has no sliders: Haptics And Triggers shows none whatever the effect (the page as tall as with Off),
	// the pop-up's right column is Custom Trigger now (the profile keeps its fallback shape).
	[AvaloniaFact]
	public async Task Shape_IsNotOnTheGeneralPage_NorInThePopUp()
	{
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = "en" })));
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		vm.Navigate(vm.OtherGames);
		vm.OtherGames.SelectedTab = OtherGamesViewModel.RumbleTab;
		FitCheck.Settle(window);
		OtherGamesView page = window.GetVisualDescendants().OfType<OtherGamesView>().Single();
		ScrollViewer scroll = page.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "PageScroll");
		Grid tryRow = page.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TryRow");
		double offHeight = scroll.Extent.Height - tryRow.Bounds.Height;
		foreach (int effect in new[] { 1, 2, 3 })
		{
			vm.OtherGames.Feel.FallbackIndex = effect;
			FitCheck.Settle(window);
			Assert.Equal(2, page.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ClassicRumbleCard").GetVisualDescendants().OfType<Slider>().Count());
			// No rows come or go (the Try row's hint is its label's tooltip, so it keeps one line beside the fourth button).
			Assert.Equal(offHeight, scroll.Extent.Height - tryRow.Bounds.Height, 1);
		}
		window.Close();

		// The game's pop-up has Custom Trigger instead: no fallback rows there any more.
		(MainWindowViewModel games, MainWindow gamesWindow) = await AuditRenderTests.OpenGames("en", 1100, 700);
		games.SupportedGames.OpenGameCommand.Execute(games.SupportedGames.Games.First(g => !g.IsRunning && g.CanLaunch));
		GamePanelViewModel panel = games.SupportedGames.Panel;
		panel.UseOwnSettingsCommand.Execute(null);
		panel.SaveProfileNameCommand.Execute(null);
		panel.SelectedSegment = GamePanelViewModel.TriggersSegment;
		panel.Feel.FallbackIndex = 2;
		FitCheck.Settle(gamesWindow);
		GamePanelView view = gamesWindow.GetVisualDescendants().OfType<GamePanelView>().Single();
		Assert.DoesNotContain(view.GetVisualDescendants().OfType<Control>(), c => c.Name?.StartsWith("PanelFallback", StringComparison.Ordinal) == true
			|| c.Name is "PanelFeedbackStartRow" or "PanelWeaponStartRow" or "PanelVibrationStartRow" or "PanelShapeSpacer");
		Assert.Contains(view.GetVisualDescendants().OfType<Control>(), c => c.Name == "PanelCustomTrigger" && c.IsEffectivelyVisible);
		// The profile's fallback (and its shape) is still kept and played where the game sends nothing.
		panel.Feel.WeaponEnd = 80;
		Assert.Equal(8, games.Settings.Profiles.Find(panel.Feel.ProfileId)!.Values.FallbackShape.WeaponEnd);
		gamesWindow.Close();	}
}