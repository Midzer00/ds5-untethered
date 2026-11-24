// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;

namespace Kopru.App.Tests;

// The game pop-up's three segments (Game, Rumble And Triggers, Sound And Light): the app's tab switch picks one by mouse or
// Ctrl+Tab / Ctrl+Shift+Tab (the keyboard stays on the switch), the pop-up keeps one height whatever the segment, the
// segment is remembered while the app runs, and on the default profile segments 2 and 3 offer "Settings For This Game".
public sealed class GamePanelSegmentsTests
{
	private static (GamePanelView View, ListBox Tabs) Find(Window window)
	{
		GamePanelView view = window.GetVisualDescendants().OfType<GamePanelView>().Single();
		return (view, view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "PanelTabs"));
	}

	private static Control Named(Visual root, string name) => root.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name);

	private static void Click(Window window, Visual target)
	{
		Point centre = FitCheck.Bounds(target, window).Center;
		window.MouseDown(centre, MouseButton.Left);
		window.MouseUp(centre, MouseButton.Left);
		FitCheck.Settle(window);
	}

	[AvaloniaFact]
	public async Task Segments_SwitchByMouse_AndByCtrlTab_TheKeyboardStaysOnTheSwitch()
	{
		(MainWindowViewModel vm, MainWindow window) = await GamePanelTests.OpenPanel("supported", "en", 1152, 700);
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		(GamePanelView view, ListBox tabs) = Find(window);
		Assert.Equal(Text.GamePanelTabsLabel(), Avalonia.Automation.AutomationProperties.GetName(tabs));
		Assert.Equal([Text.GamePanelTabGame(), Text.GamePanelTabVibration(), Text.GamePanelTabTriggers()], panel.Tabs.Select(t => t.Name));
		Assert.Equal(GamePanelViewModel.GameSegment, panel.SelectedSegment);
		Assert.True(Named(view, "GameSegment").IsEffectivelyVisible);
		Assert.False(Named(view, "VibrationSegment").IsEffectivelyVisible);

		// A click on a segment shows it alone.
		Click(window, tabs.ContainerFromIndex(GamePanelViewModel.VibrationSegment)!);
		Assert.Equal(GamePanelViewModel.VibrationSegment, panel.SelectedSegment);
		Assert.True(Named(view, "VibrationSegment").IsEffectivelyVisible);
		Assert.False(Named(view, "GameSegment").IsEffectivelyVisible);
		Assert.False(Named(view, "TriggersSegment").IsEffectivelyVisible);

		// Ctrl+Tab anywhere in the pop-up: the next segment, wrapping; Ctrl+Shift+Tab the previous one.
		view.FocusClose();
		FitCheck.Settle(window);
		window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
		FitCheck.Settle(window);
		Assert.Equal(GamePanelViewModel.TriggersSegment, panel.SelectedSegment);
		Assert.True(Named(view, "TriggersSegment").IsEffectivelyVisible);
		Assert.Contains(tabs, (window.FocusManager?.GetFocusedElement() as Visual)?.GetVisualAncestors().Prepend((Visual)window.FocusManager!.GetFocusedElement()!) ?? []);
		window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control);
		FitCheck.Settle(window);
		Assert.Equal(GamePanelViewModel.GameSegment, panel.SelectedSegment);
		window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
		FitCheck.Settle(window);
		Assert.Equal(GamePanelViewModel.TriggersSegment, panel.SelectedSegment);
		Assert.Contains(tabs, (window.FocusManager?.GetFocusedElement() as Visual)?.GetVisualAncestors().Prepend((Visual)window.FocusManager!.GetFocusedElement()!) ?? []);
		Assert.True(panel.IsOpen);
		window.Close();
	}

	// The tallest case (a supported game with Trigger Rumble shown, in Turkish) and English: the pop-up is as tall in every
	// segment and with every Custom Trigger effect on either trigger, and nothing scrolls at 1100x700 and 1152x700.
	[AvaloniaFact]
	public async Task PopUp_KeepsOneHeight_AcrossSegments_AndFits()
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height) in new[] { (1100.0, 700.0), (1152.0, 700.0), (960.0, 640.0) })
				{
					(MainWindowViewModel vm, MainWindow window) = await GamePanelTests.OpenPanel("supported", code, width, height);
					vm.Settings.XboxTriggerRumbleOn = true;
					GamePanelViewModel panel = vm.SupportedGames.Panel;
					(GamePanelView view, _) = Find(window);
					Border sheet = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "GameDialog");
					ScrollViewer scroll = view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "PanelScroll");
					HashSet<double> heights = [];
					// Custom Trigger: both on Game's Effect, Resistance, Weapon, Vibration, and the two apart.
					foreach ((int left, int right) in new[] { (0, 0), (1, 1), (2, 2), (3, 3), (1, 2), (3, 0) })
					{
						panel.Feel.LeftTrigger.EffectIndex = left;
						panel.Feel.RightTrigger.EffectIndex = right;
						int effect = left * 10 + right;
						foreach (int segment in new[] { GamePanelViewModel.GameSegment, GamePanelViewModel.VibrationSegment, GamePanelViewModel.TriggersSegment })
						{
							panel.SelectedSegment = segment;
							FitCheck.Settle(window);
							FitCheck.Settle(window);
							string at = $"{code} {width}x{height} effect {effect} segment {segment}";
							heights.Add(Math.Round(sheet.Bounds.Height, 1));
							problems.AddRange(FitCheck.Scrolls(scroll, at));
							problems.AddRange(FitCheck.CutText(sheet).Select(p => $"{at}: {p}"));
						}
					}
					if (heights.Count != 1)
					{
						problems.Add($"{code} {width}x{height}: the pop-up is {string.Join(", ", heights)} tall");
					}
					panel.SelectedSegment = GamePanelViewModel.GameSegment;
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	// The segment is kept while the app runs: another game opens on it; a new start opens on Game. "Settings For This Game"
	// (offered on segments 2 and 3) goes to Game, where the name is typed.
	[AvaloniaFact]
	public void TheSegment_IsRememberedWhileTheAppRuns()
	{
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == "Returnal"));
		Assert.Equal(GamePanelViewModel.GameSegment, panel.SelectedSegment);
		panel.SelectedSegment = GamePanelViewModel.TriggersSegment;
		panel.CloseCommand.Execute(null);
		vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == "Cyberpunk 2077"));
		Assert.Equal(GamePanelViewModel.TriggersSegment, panel.SelectedSegment);
		Assert.True(panel.IsTriggersSegment);
		panel.SelectedSegment = -1;
		panel.SelectedSegment = 7;
		Assert.Equal(GamePanelViewModel.TriggersSegment, panel.SelectedSegment);

		panel.UseOwnSettingsCommand.Execute(null);
		Assert.Equal(GamePanelViewModel.GameSegment, panel.SelectedSegment);
		panel.CancelProfileNameCommand.Execute(null);

		var again = new MainWindowViewModel(new FakeControllerService(animate: false));
		again.SupportedGames.OpenGameCommand.Execute(again.SupportedGames.Games.Single(g => g.Name == "Returnal"));
		Assert.Equal(GamePanelViewModel.GameSegment, again.SupportedGames.Panel.SelectedSegment);
	}

	// On the default profile, segments 2 and 3 show the offer instead of settings; the switch stays the same.
	[AvaloniaFact]
	public async Task DefaultProfile_Segments2And3_OfferOwnSettings()
	{
		(MainWindowViewModel vm, MainWindow window) = await GamePanelTests.OpenPanel("supported-default", "en", 1100, 700);
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		(GamePanelView view, ListBox tabs) = Find(window);
		Assert.True(panel.UsesDefault);
		Assert.False(Named(view, "ResetRow").IsEffectivelyVisible);
		foreach (int segment in new[] { GamePanelViewModel.VibrationSegment, GamePanelViewModel.TriggersSegment })
		{
			panel.SelectedSegment = segment;
			FitCheck.Settle(window);
			Control shown = Named(view, segment == GamePanelViewModel.VibrationSegment ? "VibrationSegment" : "TriggersSegment");
			Assert.Single(shown.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("offer") && b.IsEffectivelyVisible);
			Assert.DoesNotContain(shown.GetVisualDescendants().OfType<Grid>(), g => g.Classes.Contains("panelRow") && g.IsEffectivelyVisible);
			Assert.True(tabs.IsEffectivelyEnabled);
			Assert.Equal(3, tabs.GetVisualDescendants().OfType<ListBoxItem>().Count(i => i.IsEffectivelyEnabled));
			Assert.Contains(shown.GetVisualDescendants().OfType<Button>(), b => b.Name == "OwnSettingsButton" && b.IsEffectivelyVisible);
		}

		// Settings For This Game from there: the name field on Game, then the segments edit the new profile.
		Click(window, Named(view, "TriggersSegment").GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OwnSettingsButton"));
		Assert.Equal(GamePanelViewModel.GameSegment, panel.SelectedSegment);
		Assert.True(Named(view, "ProfileNameRow").IsEffectivelyVisible);
		panel.SaveProfileNameCommand.Execute(null);
		FitCheck.Settle(window);
		Assert.True(Named(view, "ResetRow").IsEffectivelyVisible);
		panel.SelectedSegment = GamePanelViewModel.VibrationSegment;
		FitCheck.Settle(window);
		Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("offer") && b.IsEffectivelyVisible);
		Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == panel.ProfileLine && t.IsEffectivelyVisible);
		window.Close();
	}

	// "Shared By Games Using This Profile" under the profile's name only when another game uses the same profile.
	[AvaloniaFact]
	public async Task SharedCaption_OnlyWhenAnotherGameUsesTheProfile()
	{
		(MainWindowViewModel vm, MainWindow window) = await GamePanelTests.OpenPanel("supported", "en", 1152, 700);
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		(GamePanelView view, _) = Find(window);
		panel.SelectedSegment = GamePanelViewModel.TriggersSegment;
		FitCheck.Settle(window);
		// Beside the profile line right of the switch: the short form, the full sentence as its tooltip.
		bool CaptionShown() => view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == Text.ProfileSharedShort() && t.IsEffectivelyVisible
			&& Equals(ToolTip.GetTip(t), Text.ProfileSharedByGames()));
		Assert.False(panel.IsProfileShared);
		Assert.False(CaptionShown());

		string id = vm.Settings.Profiles.ProfileForGame(panel.Game!.Key)!;
		GameItemViewModel other = vm.SupportedGames.Games.First(g => g != panel.Game);
		vm.Settings.Profiles.AssignGame(other.Key, id);
		FitCheck.Settle(window);
		Assert.True(panel.IsProfileShared);
		Assert.True(CaptionShown());
		panel.SelectedSegment = GamePanelViewModel.VibrationSegment;
		FitCheck.Settle(window);
		Assert.True(CaptionShown());

		vm.Settings.Profiles.AssignGame(other.Key, ProfileBook.DefaultId);
		FitCheck.Settle(window);
		Assert.False(panel.IsProfileShared);
		panel.SelectedSegment = GamePanelViewModel.GameSegment;
		window.Close();
	}

	// The switch keeps one width whichever segment is selected (every name measured bold), and the right columns end on
	// Close's right edge (no room kept for a scroll bar where nothing scrolls).
	[AvaloniaFact]
	public async Task TheSwitchKeepsItsWidth_AndTheRightColumnsEndUnderClose()
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height) in new[] { (1100.0, 700.0), (1152.0, 700.0) })
				{
					(MainWindowViewModel vm, MainWindow window) = await GamePanelTests.OpenPanel("supported", code, width, height);
					GamePanelViewModel panel = vm.SupportedGames.Panel;
					(GamePanelView view, ListBox tabs) = Find(window);
					string at = $"{code} {width}x{height}";
					Rect close = FitCheck.Bounds(Named(view, "ClosePanelButton"), window);
					HashSet<double> widths = [];
					foreach (int segment in new[] { GamePanelViewModel.GameSegment, GamePanelViewModel.VibrationSegment, GamePanelViewModel.TriggersSegment })
					{
						panel.SelectedSegment = segment;
						FitCheck.Settle(window);
						widths.Add(Math.Round(tabs.Bounds.Width, 1));
						if (tabs.Bounds.Width < 420 - 0.5)
						{
							problems.Add($"{at}: the switch is {tabs.Bounds.Width:F1} wide");
						}
						string? right = segment switch { GamePanelViewModel.VibrationSegment => "VibrationRight", GamePanelViewModel.TriggersSegment => "TriggersRight", _ => null };
						if (right != null && Math.Abs(FitCheck.Bounds(Named(view, right), window).Right - close.Right) > 0.5)
						{
							problems.Add($"{at}: {right} ends at {FitCheck.Bounds(Named(view, right), window).Right:F1}, Close at {close.Right:F1}");
						}
					}
					if (widths.Count != 1)
					{
						problems.Add($"{at}: the switch is {string.Join(", ", widths)} wide");
					}
					panel.SelectedSegment = GamePanelViewModel.GameSegment;
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	// Every setting is in exactly one tab, the one for what it does: Game (start file, controller type, profile, the game's
	// light), Vibration (the game's haptics, classic rumble, sound to haptics), Triggers (trigger strength, trigger rumble,
	// Custom Trigger). A supported game with a profile of its own and everything shown.
	[AvaloniaFact]
	public async Task EverySetting_LivesInExactlyOneTab()
	{
		(MainWindowViewModel vm, MainWindow window) = await GamePanelTests.OpenPanel("supported", "en", 1152, 700);
		vm.Settings.XboxTriggerRumbleOn = true;
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		panel.Feel.LeftTrigger.EffectIndex = 2;
		panel.Feel.RightTrigger.EffectIndex = 3;
		(GamePanelView view, ListBox tabs) = Find(window);
		Dictionary<string, HashSet<int>> where = [];
		foreach (int segment in new[] { GamePanelViewModel.GameSegment, GamePanelViewModel.VibrationSegment, GamePanelViewModel.TriggersSegment })
		{
			panel.SelectedSegment = segment;
			FitCheck.Settle(window);
			foreach (Control c in view.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.TemplatedParent == null
				&& c is Slider or ComboBox or ToggleSwitch or ListBox or Button && c != tabs && c.Name is not ("ClosePanelButton" or "PanelPlayButton"
				or "PanelBackToGameButton" or "PanelHideButton" or "PanelCloseGameButton" or "PanelForceCloseButton" or "PanelKeepGameButton")))
			{
				string name = Avalonia.Automation.AutomationProperties.GetName(c) is { Length: > 0 } n ? n : c.Name ?? c.GetType().Name;
				(where.TryGetValue(name, out HashSet<int>? set) ? set : where[name] = []).Add(segment);
			}
		}
		window.Close();
		Assert.All(where, w => Assert.True(w.Value.Count == 1, $"{w.Key} is in {w.Value.Count} tabs"));
		void In(int segment, params string[] names)
		{
			foreach (string name in names)
			{
				Assert.True(where.TryGetValue(name, out HashSet<int>? set) && set.Single() == segment, $"{name} is not (only) in tab {segment}");
			}
		}
		In(GamePanelViewModel.GameSegment, Text.StartFileChoose(), Text.PadTypeLabel(), Text.ProfileLabel(), Text.LightFromGame());
		In(GamePanelViewModel.VibrationSegment, Text.GameHapticsLevel(), Text.RumbleLevel(), Text.RumbleFeelLabel(), Text.AudioHapticsLabel(),
			Text.AudioHapticsStrengthLabel(), Text.AudioHapticsSensitivityLabel(), Text.AudioHapticsBandLabel());
		In(GamePanelViewModel.TriggersSegment, Text.TriggerStrength(), Text.TriggerRumbleLabel(), Text.CustomTriggerLeft(), Text.CustomTriggerRight(),
			Text.CustomTriggerStart(), Text.CustomTriggerEnd(), Text.CustomTriggerForce(), Text.FallbackFrequency(), Text.TryAction());
	}

	// Triggers for an unsupported game without trigger rumble: nothing for the footer's left card, so Try takes the width.
	[AvaloniaFact]
	public async Task Triggers_WithNothingOnTheLeft_TryTakesTheFooter()
	{
		(MainWindowViewModel vm, MainWindow window) = await GamePanelTests.OpenPanel("unsupported", "en", 1152, 700);
		GamePanelViewModel panel = vm.SupportedGames.Panel;
		Assert.False(panel.HasGameTriggerSettings);
		panel.SelectedSegment = GamePanelViewModel.TriggersSegment;
		FitCheck.Settle(window);
		(GamePanelView view, _) = Find(window);
		Assert.False(Named(view, "TriggersLeft").IsEffectivelyVisible);
		Rect columns = FitCheck.Bounds(Named(view, "TriggersColumns"), window);
		Rect tryCard = FitCheck.Bounds(Named(view, "TryCard"), window);
		Assert.Equal(columns.Left, tryCard.Left, 0.5);
		Assert.Equal(columns.Right, tryCard.Right, 0.5);

		// Trigger rumble shown: one footer card with the picker and Try, under the trigger cards.
		vm.Settings.XboxTriggerRumbleOn = true;
		FitCheck.Settle(window);
		Assert.True(panel.HasGameTriggerSettings);
		Assert.True(Named(view, "PanelTriggerRumbleRow").IsEffectivelyVisible);
		Assert.True(FitCheck.Bounds(Named(view, "PanelTriggerRumbleRow"), window).Top >= FitCheck.Bounds(Named(view, "PanelCustomTrigger"), window).Bottom);
		panel.SelectedSegment = GamePanelViewModel.GameSegment;
		window.Close();
	}
}