// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Input;
using Kopru.Core.Lighting;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The segmented choice is the app's one standard control for picking one of a few options: its selected step is the accent
// tint with the accent hairline (not a near-invisible neutral fill); the Mouse Mode tab's two choices share one track width
// (ControlWidth) in rows of the shared height; the Light page's player LED pattern and brightness are segmented choices bound
// to the same settings as before. The pages these touch (Mouse Mode, Light, the Overview with all good) fit the review sizes
// without scrolling in both languages and the pseudo-locale.
public sealed class SegmentedChoiceTests
{
	private static IEnumerable<Language> Languages => Localizer.Available.Append(Language.Pseudo);

	[AvaloniaFact]
	public void SelectedStep_IsTheAccentTint_WithTheAccentHairline()
	{
		var tint = (ISolidColorBrush)Resource("AccentTintBrush");
		var edge = (ISolidColorBrush)Resource("AccentTintEdgeBrush");
		Assert.False(Application.Current!.TryGetResource("SegmentSelectedBrush", null, out _));
		Assert.False(Application.Current!.TryGetResource("SegmentSelectedHoverBrush", null, out _));

		var choice = new ListBox
		{
			Theme = (Avalonia.Styling.ControlTheme)Resource("Segmented"),
			ItemsSource = new[] { "One", "Two", "Three" },
			SelectedIndex = 1,
			Width = 240
		};
		var window = new Window { Width = 400, Height = 200, Content = choice };
		window.Show();
		try
		{
			FitCheck.Settle(window);
			ListBoxItem[] items = [.. choice.GetVisualDescendants().OfType<ListBoxItem>()];
			Assert.Equal(3, items.Length);
			ListBoxItem selected = items[1];
			Assert.Equal(tint.Color, ((ISolidColorBrush)selected.Background!).Color);
			Assert.Equal(edge.Color, ((ISolidColorBrush)selected.BorderBrush!).Color);
			Assert.NotEqual(tint.Color, (items[0].Background as ISolidColorBrush)?.Color);
		}
		finally
		{
			window.Close();
		}
	}

	// Both choices of the Mouse Mode tab are ControlWidth (240) wide, beside their names, in rows at least SettingRowHeight high;
	// the tab, the Light page and the Overview with all good fit every review size without scrolling.
	[AvaloniaFact]
	public void MouseModeTracks_AreOneWidth_AndThePagesFit()
	{
		double controlWidth = (double)Resource("ControlWidth");
		double rowHeight = (double)Resource("SettingRowHeight");
		Assert.Equal(240, controlWidth);
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		try
		{
			foreach (Language language in Languages)
			{
				Localizer.Chosen = language;
				string code = language.Code;
				(MainWindowViewModel vm, FakeControllerService fake) = SettingsFitTests.FullCase(code);
				Localizer.Chosen = language;
				vm.Settings.DesktopMode = vm.Settings.DesktopMode with { PointerStick = PointerStick.Left, RightClick = TouchpadRightClick.TwoFingers };
				foreach ((double width, double height) in FitCheck.ReviewSizes)
				{
					string at = $"{code} {width}x{height}";
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					try
					{
						vm.Navigate(vm.SettingsPage);
						vm.SettingsPage.SelectedTab = SettingsViewModel.MouseTab;
						FitCheck.Settle(window);
						problems.AddRange(SettingsFitTests.Check(window).Select(p => $"{at} mouse: {p}"));
						SettingsLayout layout = window.GetVisualDescendants().OfType<SettingsLayout>().Single(l => l.IsEffectivelyVisible);
						foreach (string name in new[] { "PointerStickChoice", "RightClickChoice" })
						{
							ListBox track = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == name);
							if (Math.Abs(track.Bounds.Width - controlWidth) > 0.5)
							{
								problems.Add($"{at} mouse: {name} is {track.Bounds.Width:F1} wide, not {controlWidth} (narrow: {layout.IsNarrow})");
							}
							var row = (Grid)track.Parent!;
							if (row.Bounds.Height < rowHeight - 0.5)
							{
								problems.Add($"{at} mouse: {name}'s row is {row.Bounds.Height:F1} high, under {rowHeight}");
							}
							if (Grid.GetRow(track) != 0)
							{
								problems.Add($"{at} mouse: {name} is not beside its name");
							}
						}

						vm.Navigate(vm.Light);
						FitCheck.Settle(window);
						LightView light = window.GetVisualDescendants().OfType<LightView>().Single(v => v.IsEffectivelyVisible);
						foreach (ScrollViewer sv in light.GetVisualAncestors().OfType<ScrollViewer>())
						{
							problems.AddRange(FitCheck.Scrolls(sv, "light").Select(p => $"{at} light: {p}"));
						}
						// The "Your Light" card may scroll inside when the window is short (its design); with the shipped
						// languages it does not at the review sizes. The pseudo-locale's longer hints already made it scroll
						// before the pattern became a segmented choice (the same height as the pills' one line).
						if (!PseudoLocale.IsPseudo(code))
						{
							foreach (ScrollViewer sv in light.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible))
							{
								problems.AddRange(FitCheck.Scrolls(sv, "light card").Select(p => $"{at} light: {p}"));
							}
						}
						problems.AddRange(FitCheck.CutText(light).Select(p => $"{at} light: {p}"));
						ListBox pattern = light.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "PlayerLedPatternChoice");
						problems.AddRange(pattern.GetVisualDescendants().OfType<TextBlock>().Where(FitCheck.IsTextCutOff)
							.Select(t => $"{at} light: pattern step \"{t.Text}\" is cut off"));

						fake.Health = OverviewHealthLineTests.State("good");
						fake.RaiseStateChanged();
						vm.Navigate(vm.Overview);
						FitCheck.Settle(window);
						OverviewView overview = window.GetVisualDescendants().OfType<OverviewView>().Single(v => v.IsEffectivelyVisible);
						foreach (ScrollViewer sv in overview.GetVisualAncestors().OfType<ScrollViewer>()
							.Concat(overview.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible)))
						{
							problems.AddRange(FitCheck.Scrolls(sv, "overview").Select(p => $"{at} overview: {p}"));
						}
						if (overview.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "HealthLine").IsEffectivelyVisible)
						{
							problems.Add($"{at} overview: the health line shows although all is good");
						}
					}
					finally
					{
						window.Close();
					}
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	// The Light page's converted choices write and show the same settings as before: the pattern (Off, 1-4, All) and the
	// player LEDs' brightness; a change of the setting elsewhere moves the selection.
	[AvaloniaFact]
	public void LightChoices_AreBoundToTheSameSettings()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			var fake = new FakeControllerService(animate: false);
			var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = "en" })));
			vm.Sticks.StopPreview();
			var window = new MainWindow { DataContext = vm, Width = 1152, Height = 700 };
			window.Show();
			try
			{
				vm.Navigate(vm.Light);
				FitCheck.Settle(window);
				LightView view = window.GetVisualDescendants().OfType<LightView>().Single();
				ListBox pattern = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "PlayerLedPatternChoice");
				ListBox level = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "LedBrightnessChoice");
				Assert.Same(Resource("Segmented"), pattern.Theme);
				Assert.Same(Resource("Segmented"), level.Theme);
				Assert.Equal(6, pattern.ItemCount);

				PlayerLedPattern[] patterns = [PlayerLedPattern.Off, PlayerLedPattern.One, PlayerLedPattern.Two, PlayerLedPattern.Three, PlayerLedPattern.Four, PlayerLedPattern.All];
				for (int i = 0; i < patterns.Length; i++)
				{
					pattern.SetCurrentValue(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedIndexProperty, i);
					FitCheck.Settle(window);
					Assert.Equal(patterns[i], vm.Settings.PlayerLedPattern);
					Assert.Equal(patterns[i] != PlayerLedPattern.Off, vm.Settings.PlayerLeds);
				}
				vm.Settings.PlayerLedPattern = PlayerLedPattern.Three;
				FitCheck.Settle(window);
				Assert.Equal(3, pattern.SelectedIndex);
				Assert.Same(vm.Light.SelectedPattern, vm.Light.Patterns[3]);

				LedBrightness[] levels = [LedBrightness.Low, LedBrightness.Medium, LedBrightness.High];
				for (int i = 0; i < levels.Length; i++)
				{
					level.SetCurrentValue(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedIndexProperty, i);
					FitCheck.Settle(window);
					Assert.Equal(levels[i], vm.Settings.PlayerLedBrightness);
				}
				vm.Settings.PlayerLedBrightness = LedBrightness.Medium;
				FitCheck.Settle(window);
				Assert.Equal(1, level.SelectedIndex);

				// Nothing selected (-1) changes nothing.
				pattern.SetCurrentValue(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedIndexProperty, -1);
				level.SetCurrentValue(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedIndexProperty, -1);
				FitCheck.Settle(window);
				Assert.Equal(PlayerLedPattern.Three, vm.Settings.PlayerLedPattern);
				Assert.Equal(LedBrightness.Medium, vm.Settings.PlayerLedBrightness);
			}
			finally
			{
				window.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The Mouse Mode choices (unchanged bindings, new width) still set the pointer stick and the right click.
	[AvaloniaFact]
	public void MouseModeChoices_StillSetTheirSettings()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			(MainWindowViewModel vm, FakeControllerService _) = SettingsFitTests.FullCase("en");
			var window = new MainWindow { DataContext = vm, Width = 1152, Height = 700 };
			window.Show();
			try
			{
				vm.Navigate(vm.SettingsPage);
				vm.SettingsPage.SelectedTab = SettingsViewModel.MouseTab;
				FitCheck.Settle(window);
				ListBox stick = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "PointerStickChoice");
				ListBox click = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "RightClickChoice");
				stick.SetCurrentValue(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedIndexProperty, 1);
				click.SetCurrentValue(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedIndexProperty, 1);
				FitCheck.Settle(window);
				Assert.Equal(PointerStick.Right, vm.Settings.DesktopMode.PointerStick);
				Assert.Equal(Enum.GetValues<TouchpadRightClick>()[1], vm.Settings.DesktopMode.RightClick);
				stick.SetCurrentValue(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedIndexProperty, 0);
				FitCheck.Settle(window);
				Assert.Equal(PointerStick.Left, vm.Settings.DesktopMode.PointerStick);
			}
			finally
			{
				window.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static object Resource(string key)
	{
		Assert.True(Application.Current!.TryGetResource(key, null, out object? value), key);
		return value!;
	}
}
