// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The 0.2 layout pass's rules, at the owner's window (1152x700), the design size (1100x700) and the minimum (960x640), in
// both languages:
// - no title (page, card or group) touches a button on its line: at least 8 apart (the Xbox And DS4 Rumble card had its
//   try buttons on its title line, and the English title ran under them);
// - a setting row's text block and its control are centred on one line (Settings > General > Language looked top-aligned
//   beside a taller picker), and the language row's explanation keeps one line;
// - the game pop-up: every control of the left and of the right column is one width with one right edge (the type and
//   profile pickers had two widths), and every slider shows its value.
public sealed class UiPolishLayoutTests
{
	private static readonly (double Width, double Height)[] Sizes = [(1152, 700), (1100, 700), (960, 640)];
	private static readonly string[] SettingsTabs = ["general", "controllers", "audio", "shortcuts", "mouse", "notifications"];

	[AvaloniaFact]
	public void Titles_NeverTouchButtonsOnTheirLine_OnEveryPage()
	{
		List<string> problems = [];
		ForEachLanguage(code =>
		{
			foreach ((double width, double height) in Sizes)
			{
				(MainWindowViewModel vm, FakeControllerService _) = SettingsFitTests.FullCase(code);
				// With a fallback effect the try row holds all four buttons.
				vm.OtherGames.Feel.IsFallbackWeapon = true;
				var window = new MainWindow { DataContext = vm, Width = width, Height = height };
				window.Show();
				foreach (NavItem item in vm.NavItems)
				{
					vm.SelectedNav = item;
					if (item.Page == vm.SettingsPage)
					{
						for (int tab = 0; tab < SettingsTabs.Length; tab++)
						{
							vm.SettingsPage.SelectedTab = tab;
							Settle(window);
							problems.AddRange(TitlesTouchingButtons(window).Select(p => $"{code} {width}x{height} settings {SettingsTabs[tab]}: {p}"));
						}
						continue;
					}
					Settle(window);
					problems.AddRange(TitlesTouchingButtons(window).Select(p => $"{code} {width}x{height} {PageSizeRenderTests.Slug(item.Page)}: {p}"));
				}
				window.Close();
			}
		});
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	// The Xbox And DS4 Rumble card: its header holds no button, the try row does (the fallback effect's button only while an
	// effect is chosen).
	[AvaloniaFact]
	public void RumbleCard_TryButtonsAreARow_NotOnTheTitleLine()
	{
		ForEachLanguage(code =>
		{
			MainWindowViewModel vm = GameListScrollTests.Open(installedOnly: false, code);
			var window = new MainWindow { DataContext = vm, Width = 1152, Height = 700 };
			window.Show();
			vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.OtherGames);
			vm.OtherGames.Feel.IsFallbackOff = true;
			Settle(window);
			Border card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ClassicRumbleCard");
			StackPanel header = card.GetVisualDescendants().OfType<StackPanel>().First(s => s.Classes.Contains("cardHeader"));
			Assert.Empty(header.GetVisualDescendants().OfType<Button>());
			Grid tryRow = card.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "TryRow");
			Button trigger = tryRow.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TriggerTestButton");
			Assert.False(trigger.IsEffectivelyVisible);
			Assert.Equal(3, tryRow.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible));

			vm.OtherGames.Feel.IsFallbackFeedback = true;
			Settle(window);
			Assert.True(trigger.IsEffectivelyVisible);
			Assert.Equal(4, tryRow.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible));
			window.Close();
		});
	}

	[AvaloniaFact]
	public void SettingRows_TextAndControlCentredOnOneLine()
	{
		List<string> problems = [];
		ForEachLanguage(code =>
		{
			foreach ((double width, double height) in Sizes)
			{
				(MainWindowViewModel vm, FakeControllerService _) = SettingsFitTests.FullCase(code);
				var window = new MainWindow { DataContext = vm, Width = width, Height = height };
				window.Show();
				vm.Navigate(vm.SettingsPage);
				for (int tab = 0; tab < SettingsTabs.Length; tab++)
				{
					vm.SettingsPage.SelectedTab = tab;
					Settle(window);
					SettingsView page = window.GetVisualDescendants().OfType<SettingsView>().Single();
					problems.AddRange(OffCentreRows(page).Select(p => $"{code} {width}x{height} {SettingsTabs[tab]}: {p}"));
					if (tab == SettingsViewModel.GeneralTab)
					{
						// The language row: its explanation on one line beside the picker.
						ComboBox picker = page.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && c.ItemsSource == vm.SettingsPage.Languages);
						Grid row = picker.GetVisualAncestors().OfType<Grid>().First(g => g.Classes.Contains("settingRow"));
						foreach (TextBlock text in row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !t.GetVisualAncestors().Contains(picker)))
						{
							if (text.TextLayout.TextLines.Count != 1)
							{
								problems.Add($"{code} {width}x{height}: language row text \"{text.Text}\" has {text.TextLayout.TextLines.Count} lines");
							}
						}
					}
				}
				vm.Navigate(vm.OtherGames);
				Settle(window);
				problems.AddRange(OffCentreRows(window.GetVisualDescendants().OfType<OtherGamesView>().Single()).Select(p => $"{code} {width}x{height} haptics: {p}"));
				window.Close();
			}
		});
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	[AvaloniaFact]
	public async Task GamePopUp_ControlsShareOneWidthPerColumn_AndSlidersShowValues()
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height) in Sizes)
				{
					(MainWindowViewModel vm, MainWindow window) = await AuditRenderTests.OpenGames(code, width, height);
					vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == AuditRenderTests.MadeUpGame));
					vm.SupportedGames.Panel.UseOwnSettingsCommand.Execute(null); vm.SupportedGames.Panel.SaveProfileNameCommand.Execute(null);
					Settle(window);
					string at = $"{code} {width}x{height}";
					GamePanelView panel = window.GetVisualDescendants().OfType<GamePanelView>().Single();
					GamePanelViewModel model = vm.SupportedGames.Panel;
					Assert.True(panel.IsTwoColumns, at);
					// Custom Trigger with Weapon and Vibration, so its column has every kind of row.
					model.Feel.LeftTrigger.EffectIndex = 2;
					model.Feel.RightTrigger.EffectIndex = 3;
					foreach ((int segment, string column) in new[] { (GamePanelViewModel.GameSegment, "GameSegment"), (GamePanelViewModel.VibrationSegment, "VibrationLeft"),
						(GamePanelViewModel.VibrationSegment, "PanelAudio"), (GamePanelViewModel.TriggersSegment, "TriggersRight") })
					{
						model.SelectedSegment = segment;
						Settle(window);
						StackPanel host = panel.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == column);
						List<Control> controls = host.GetVisualDescendants().OfType<Control>()
							.Where(c => c.IsEffectivelyVisible && c.Classes.Contains("panelControl") && c.GetVisualParent() is Grid g && g.Classes.Contains("panelRow")).ToList();
						if (controls.Count < 2)
						{
							problems.Add($"{at} {column}: only {controls.Count} row controls");
							continue;
						}
						Rect first = FitCheck.Bounds(controls[0], window);
						foreach (Control c in controls.Skip(1))
						{
							Rect r = FitCheck.Bounds(c, window);
							if (Math.Abs(r.Width - first.Width) > 0.5 || Math.Abs(r.Right - first.Right) > 0.5)
							{
								problems.Add($"{at} {column}: {c.GetType().Name} {c.Name} {r} is not as wide as / does not end with {controls[0].GetType().Name} {controls[0].Name} {first}");
							}
						}
					}
					// The two pickers of the Game segment start on one edge; the profile picker and its actions button together
					// are as wide as the type picker.
					model.SelectedSegment = GamePanelViewModel.GameSegment;
					Settle(window);
					ComboBox type = panel.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "PanelPadType");
					ComboBox profile = panel.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "PanelProfile");
					Button more = panel.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "PanelProfileActions");
					Rect typeBox = FitCheck.Bounds(type, window), profileBox = FitCheck.Bounds(profile, window), moreBox = FitCheck.Bounds(more, window);
					if (Math.Abs(typeBox.Left - profileBox.Left) > 0.5 || Math.Abs(typeBox.Right - moreBox.Right) > 0.5)
					{
						problems.Add($"{at}: type picker {typeBox}, profile picker {profileBox} and its actions {moreBox} do not share the control column");
					}
					foreach (int segment in new[] { GamePanelViewModel.GameSegment, GamePanelViewModel.VibrationSegment, GamePanelViewModel.TriggersSegment })
					{
						model.SelectedSegment = segment;
						Settle(window);
						foreach (Slider slider in panel.GetVisualDescendants().OfType<Slider>().Where(s => s.IsEffectivelyVisible))
						{
							TextBlock? value = (slider.GetVisualParent() as Grid)?.Children.OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("value"));
							if (value == null || !value.IsEffectivelyVisible || string.IsNullOrEmpty(value.Text))
							{
								problems.Add($"{at} segment {segment}: a slider without its value ({AutomationName(slider)})");
							}
						}
						problems.AddRange(TitlesTouchingButtons(panel).Select(p => $"{at} pop-up segment {segment}: {p}"));
						problems.AddRange(OffCentreRows(panel, "panelRow").Select(p => $"{at} pop-up segment {segment}: {p}"));
					}
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

	// A long profile name (the longest allowed): the game pop-up's picker keeps its width and ends the name in "…" with the
	// full name as its tooltip, the "…" button stays right of it inside the control column, the sidebar's profile line does the same, and
	// the sidebar's support pill and star button fit their row side by side, nothing cut.
	[AvaloniaFact]
	public async Task LongProfileName_EndsInEllipsisWithATooltip_AndTheSupportRowFits()
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		string name = "Neon Drift Gece Yarışı Uzun Profil Adı X";
		Assert.Equal(Kopru.Core.Profiles.ProfileBook.MaxNameLength, name.Length);
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height) in new[] { (1152.0, 700.0), (1100.0, 700.0) })
				{
					(MainWindowViewModel vm, MainWindow window) = await AuditRenderTests.OpenGames(code, width, height);
					vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.Single(g => g.Name == AuditRenderTests.MadeUpGame));
					GamePanelViewModel panelVm = vm.SupportedGames.Panel;
					panelVm.UseOwnSettingsCommand.Execute(null);
					panelVm.ProfileName = name;
					panelVm.SaveProfileNameCommand.Execute(null);
					Settle(window);
					string at = $"{code} {width}x{height}";
					GamePanelView panel = window.GetVisualDescendants().OfType<GamePanelView>().Single();
					ComboBox type = panel.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "PanelPadType");
					ComboBox profile = panel.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "PanelProfile");
					Button actions = panel.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "PanelProfileActions");
					Rect typeRect = FitCheck.Bounds(type, window), profileRect = FitCheck.Bounds(profile, window), actionsRect = FitCheck.Bounds(actions, window);
					if (Math.Abs(typeRect.Left - profileRect.Left) > 0.5 || Math.Abs(typeRect.Right - actionsRect.Right) > 0.5)
					{
						problems.Add($"{at}: profile picker {profileRect} and its actions {actionsRect} do not span the type picker {typeRect}");
					}
					if (actionsRect.Left < profileRect.Right + 2 || actionsRect.Top < profileRect.Top - 0.5 || actionsRect.Bottom > profileRect.Bottom + 0.5)
					{
						problems.Add($"{at}: the profile actions button {actionsRect} is not right of the picker {profileRect}");
					}
					TextBlock? shown = profile.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == name);
					if (shown == null)
					{
						problems.Add($"{at}: the picker does not show the profile");
					}
					else
					{
						if (!shown.TextLayout.TextLines.Any(l => l.HasCollapsed) || FitCheck.Bounds(shown, window).Right > profileRect.Right + 0.5)
						{
							problems.Add($"{at}: the long name is not ended in an ellipsis inside the picker");
						}
						if (!Equals(ToolTip.GetTip(shown), name))
						{
							problems.Add($"{at}: the picker's name has no tooltip with the full name");
						}
					}
					TextBlock line = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ActiveProfileLine");
					if (!line.Text!.Contains(name) || !Equals(ToolTip.GetTip(line), line.Text))
					{
						problems.Add($"{at}: the sidebar's profile line \"{line.Text}\" has no tooltip with the full text");
					}
					Grid support = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "SupportRow");
					Rect row = FitCheck.Bounds(support, window);
					Rect pill = FitCheck.Bounds(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SupportPill"), window);
					Rect star = FitCheck.Bounds(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StarButton"), window);
					if (pill.Right > star.Left - 4 || pill.Left < row.Left - 0.5 || star.Right > row.Right + 0.5)
					{
						problems.Add($"{at}: support pill {pill} and star {star} do not fit the row {row}");
					}
					problems.AddRange(FitCheck.CutText(support).Select(p => $"{at} support row: {p}"));
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

	// Every shown title (h1, h2, section) and every shown button on its line: the title's text ends at least 8 before the
	// button starts (or starts 8 after it ends).
	private static IEnumerable<string> TitlesTouchingButtons(Visual root)
	{
		List<(TextBlock Text, Rect Ink)> titles = root.GetVisualDescendants().OfType<TextBlock>()
			.Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && (t.Classes.Contains("h1") || t.Classes.Contains("h2") || t.Classes.Contains("section")))
			.Select(t => (t, Ink(t, root))).ToList();
		List<Button> buttons = root.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0).ToList();
		foreach ((TextBlock text, Rect ink) in titles)
		{
			foreach (Button button in buttons.Where(b => !text.GetVisualAncestors().Contains(b)))
			{
				Rect b = FitCheck.Bounds(button, root);
				bool sameLine = b.Top < ink.Bottom - 1 && b.Bottom > ink.Top + 1;
				if (sameLine && b.Left < ink.Right + 8 && b.Right > ink.Left - 8)
				{
					yield return $"title \"{text.Text}\" {ink} and button {AutomationName(button)} {b} are closer than 8";
				}
			}
		}
	}

	// The drawn text of a TextBlock (its box can be wider than the text): from its left edge as wide as its widest line.
	private static Rect Ink(TextBlock text, Visual root)
	{
		Rect box = FitCheck.Bounds(text, root);
		double width = Math.Min(box.Width, text.TextLayout.WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right);
		double left = text.TextAlignment == Avalonia.Media.TextAlignment.Right ? box.Right - width : box.Left;
		return new Rect(left, box.Top, width, box.Height);
	}

	// Rows (Grid with the row class) whose text (column 0) and control (column 1) share the first row: their vertical centres
	// at most 1.5 apart.
	private static IEnumerable<string> OffCentreRows(Visual root, string rowClass = "settingRow")
	{
		foreach (Grid row in root.GetVisualDescendants().OfType<Grid>().Where(g => g.IsEffectivelyVisible && g.Classes.Contains(rowClass)))
		{
			List<Control> shown = row.Children.Where(c => c.IsEffectivelyVisible).ToList();
			Control? text = shown.FirstOrDefault(c => Grid.GetColumn(c) == 0 && Grid.GetRow(c) == 0 && Grid.GetColumnSpan(c) == 1);
			Control? control = shown.FirstOrDefault(c => Grid.GetColumn(c) == 1 && Grid.GetRow(c) == 0);
			if (text == null || control == null)
			{
				continue;
			}
			Rect t = FitCheck.Bounds(text, row);
			Rect c = FitCheck.Bounds(control, row);
			// Bounds leave out the margin (Settings gives the text 8 above and below).
			double textCentre = (t.Top + t.Bottom) / 2;
			if (Math.Abs(textCentre - (c.Top + c.Bottom) / 2) > 1.5)
			{
				string label = row.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(x => x.IsEffectivelyVisible)?.Text ?? "?";
				yield return $"row \"{label}\": text centre {textCentre:F1}, {control.GetType().Name} centre {(c.Top + c.Bottom) / 2:F1}";
			}
		}
	}

	// After a page or tab switch: past the page transition, laid out and rendered.
	private static void Settle(Window window)
	{
		FitCheck.Settle(window);
		Thread.Sleep(320);
		FitCheck.Settle(window);
	}

	private static string AutomationName(Control c) =>
		Avalonia.Automation.AutomationProperties.GetName(c) is { Length: > 0 } name ? name : c is Button { Content: string s } ? s : c.Name ?? c.GetType().Name;

	private static void ForEachLanguage(Action<string> action)
	{
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				action(code);
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}
}
