// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Lighting;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;

namespace Kopru.App.Tests;

// The second 0.2 layout pass, at the owner's window (1152x700) and the design size (1100x700), in both languages:
// - one control height: every picker, text box, segmented choice and chip on every page, Settings tab and the game pop-up is
//   ControlHeight high, and every button one of the three button sizes (small on a title or caption line, the regular
//   one, the large header action); the Overview's quick settings had pickers of 30 and 34 beside 32 px controls;
// - the Games list's row buttons line up: Play / Back To Game in one column, Hide / Remove in the next, one width each;
// - a game that gets its own profile from the pop-up leaves every other row's profile picker showing its profile (two rows
//   showed an empty picker behind the pop-up), also when that profile is renamed or deleted;
// - the pop-up's Turn Sound Into Haptics is one row named after the setting with its switch (its label was the switch's
//   state, "Off"); what it is doing shows as a caption only while it is on;
// - the Light page's "Your Light" card fits without scrolling inside, its mode a picker on the title's line.
public sealed class UiPolish2Tests
{
	private static readonly (double Width, double Height)[] Sizes = [(1152, 700), (1100, 700)];
	private static readonly string[] SettingsTabs = ["general", "controllers", "audio", "shortcuts", "mouse", "notifications"];

	[AvaloniaFact]
	public async Task EveryControl_HasOneOfTheHeightTokens_OnEveryPage()
	{
		double control = Token("ControlHeight");
		double[] buttons = [Token("ControlHeightSmall"), control, Token("ControlHeightLarge")];
		Assert.Equal(32, control);
		List<string> problems = [];
		await ForEachLanguage(async code =>
		{
			foreach ((double width, double height) in Sizes)
			{
				string at = $"{code} {width}x{height}";
				(MainWindowViewModel vm, FakeControllerService _) = SettingsFitTests.FullCase(code);
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
							problems.AddRange(OffTokenHeights(window, control, buttons).Select(p => $"{at} settings {SettingsTabs[tab]}: {p}"));
						}
						continue;
					}
					if (item.Page == vm.Controller)
					{
						foreach (int tab in new[] { ControllerPageViewModel.TestTab, ControllerPageViewModel.OutputsTab, ControllerPageViewModel.RemapTab })
						{
							vm.OpenController(tab);
							Settle(window);
							problems.AddRange(OffTokenHeights(window, control, buttons).Select(p => $"{at} controller {tab}: {p}"));
						}
						continue;
					}
					Settle(window);
					problems.AddRange(OffTokenHeights(window, control, buttons).Select(p => $"{at} {PageSizeRenderTests.Slug(item.Page)}: {p}"));
				}
				window.Close();

				(MainWindowViewModel games, MainWindow gamesWindow) = await AuditRenderTests.OpenGames(code, width, height);
				problems.AddRange(OffTokenHeights(gamesWindow, control, buttons).Select(p => $"{at} games list: {p}"));
				games.SupportedGames.OpenGameCommand.Execute(games.SupportedGames.Games.Single(g => g.Name == AuditRenderTests.MadeUpGame));
				games.SupportedGames.Panel.UseOwnSettingsCommand.Execute(null); games.SupportedGames.Panel.SaveProfileNameCommand.Execute(null);
				Settle(gamesWindow);
				GamePanelView panel = gamesWindow.GetVisualDescendants().OfType<GamePanelView>().Single();
				problems.AddRange(OffTokenHeights(panel, control, buttons).Select(p => $"{at} pop-up: {p}"));
				gamesWindow.Close();
			}
		});
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	[AvaloniaFact]
	public async Task GamesList_RowButtons_LineUpInColumnsOfOneWidth()
	{
		List<string> problems = [];
		await ForEachLanguage(async code =>
		{
			foreach ((double width, double height) in Sizes)
			{
				string at = $"{code} {width}x{height}";
				(MainWindowViewModel _, MainWindow window) = await AuditRenderTests.OpenGames(code, width, height);
				GameRowList list = window.GetVisualDescendants().OfType<GameRowList>().Single(l => l.IsEffectivelyVisible);
				List<Button> shown = list.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("rowAction")).ToList();
				List<Rect> play = shown.Where(b => b.Classes.Contains("play") || b.Classes.Contains("back")).Select(b => FitCheck.Bounds(b, list)).ToList();
				List<Rect> hide = shown.Where(b => b.Classes.Contains("hide") || b.Classes.Contains("remove")).Select(b => FitCheck.Bounds(b, list)).ToList();
				// The sample: four rows with Play or Back To Game, five with Hide or Remove.
				if (play.Count < 4 || hide.Count < 5)
				{
					problems.Add($"{at}: {play.Count} play and {hide.Count} hide buttons shown");
					window.Close();
					continue;
				}
				foreach ((string name, List<Rect> column) in new[] { ("play", play), ("hide", hide) })
				{
					foreach (Rect r in column.Skip(1))
					{
						if (Math.Abs(r.Left - column[0].Left) > 0.5 || Math.Abs(r.Width - column[0].Width) > 0.5)
						{
							problems.Add($"{at}: {name} button {r} is not in the column of {column[0]}");
						}
					}
				}
				if (play[0].Right > hide[0].Left - 4)
				{
					problems.Add($"{at}: the play column {play[0]} runs into the hide column {hide[0]}");
				}
				// No row button runs into a picker.
				foreach (ComboBox picker in list.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible && c.DataContext is GameItemViewModel))
				{
					Rect p = FitCheck.Bounds(picker, list);
					foreach (Rect b in play.Concat(hide).Where(b => b.Bottom > p.Top && b.Top < p.Bottom && b.Right > p.Left - 4))
					{
						problems.Add($"{at}: a row button {b} runs into a picker {p}");
					}
				}
				window.Close();
			}
		});
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	[AvaloniaFact]
	public async Task GamesList_OwnSettingsForOneGame_LeavesEveryRowPickerFilled()
	{
		List<string> problems = [];
		await ForEachLanguage(async code =>
		{
			(MainWindowViewModel vm, MainWindow window) = await AuditRenderTests.OpenGames(code, 1152, 700);
			GameItemViewModel game = vm.SupportedGames.Games.Single(g => g.Name == AuditRenderTests.MadeUpGame);
			vm.SupportedGames.OpenGameCommand.Execute(game);
			vm.SupportedGames.Panel.UseOwnSettingsCommand.Execute(null); vm.SupportedGames.Panel.SaveProfileNameCommand.Execute(null);
			Settle(window);
			problems.AddRange(EmptyRowPickers(window).Select(p => $"{code} own settings: {p}"));
			// Behind another game's pop-up, as in the renders.
			vm.SupportedGames.Panel.CloseCommand.Execute(null);
			vm.SupportedGames.OpenGameCommand.Execute(vm.SupportedGames.Games.First(g => !g.IsRunning && g.CanLaunch));
			Settle(window);
			problems.AddRange(EmptyRowPickers(window).Select(p => $"{code} other pop-up: {p}"));
			vm.SupportedGames.Panel.CloseCommand.Execute(null);
			// The game's profile renamed, then deleted: its row shows the new name, then the default profile.
			string id = vm.Settings.Profiles.ProfileForGame(game.Key)!;
			vm.Settings.Profiles.Rename(id, "Neon Night");
			Settle(window);
			problems.AddRange(EmptyRowPickers(window).Select(p => $"{code} renamed: {p}"));
			Assert.Equal("Neon Night", game.Profile!.Selected?.Name);
			Assert.Equal("Neon Night", (RowProfilePicker(window, game).SelectedItem as ProfileOption)?.Name);
			vm.Settings.Profiles.Delete(id);
			Settle(window);
			problems.AddRange(EmptyRowPickers(window).Select(p => $"{code} deleted: {p}"));
			Assert.Equal(ProfileBook.DefaultId, (RowProfilePicker(window, game).SelectedItem as ProfileOption)?.Id);
			window.Close();
		});
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	[AvaloniaFact]
	public async Task LightPage_YourLightCard_FitsWithoutScrolling()
	{
		List<string> problems = [];
		await ForEachLanguage(code =>
		{
			foreach (LightMode mode in new[] { LightMode.Static, LightMode.Rainbow, LightMode.Battery })
			{
				foreach ((double width, double height) in Sizes)
				{
					(MainWindowViewModel vm, FakeControllerService _) = SettingsFitTests.FullCase(code);
					vm.Settings.LightMode = mode;
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.Navigate(vm.Light);
					Settle(window);
					LightView view = window.GetVisualDescendants().OfType<LightView>().Single();
					Border card = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "OwnLightCard");
					ScrollViewer scroll = card.GetVisualDescendants().OfType<ScrollViewer>().First();
					string at = $"{code} {width}x{height} {mode}";
					if (scroll.Extent.Height > scroll.Viewport.Height + 0.5)
					{
						problems.Add($"{at}: the card scrolls inside ({scroll.Extent.Height:F1} > {scroll.Viewport.Height:F1})");
					}
					ComboBox picker = view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "LightModePicker");
					TextBlock title = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == vm.Light.ModeLabel);
					Rect p = FitCheck.Bounds(picker, view);
					Rect t = FitCheck.Bounds(title, view);
					if (Math.Abs((p.Top + p.Bottom) / 2 - (t.Top + t.Bottom) / 2) > 1.5)
					{
						problems.Add($"{at}: the mode picker {p} is not on the title's line {t}");
					}
					problems.AddRange(FitCheck.CutText(view).Select(x => $"{at}: {x}"));
					window.Close();
				}
			}
			return Task.CompletedTask;
		});
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	private static double Token(string key) => (double)Application.Current!.FindResource(key)!;

	// Pickers, text boxes, segmented choices and chips not ControlHeight high; text buttons of none of the button sizes.
	private static IEnumerable<string> OffTokenHeights(Visual root, double control, double[] buttons)
	{
		Application app = Application.Current!;
		object? segmented = app.FindResource("Segmented");
		object? chip = app.FindResource("ChipToggle");
		object? quiet = app.FindResource("QuietButton");
		object? primary = app.FindResource("PrimaryButton");
		foreach (Control c in root.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0))
		{
			bool isControl = c is ComboBox or TextBox || c is ListBox l && l.Theme == segmented || c is ToggleButton t && t.Theme == chip;
			if (isControl && Math.Abs(c.Bounds.Height - control) > 0.5)
			{
				yield return $"{Describe(c)} is {c.Bounds.Height:F1} high, not {control}";
			}
			// Text buttons (not the square icon buttons) of the two button themes.
			if (c is Button b && (b.Theme == quiet || b.Theme == primary) && Math.Abs(b.Bounds.Width - b.Bounds.Height) > 0.5
				&& !buttons.Any(h => Math.Abs(b.Bounds.Height - h) <= 0.5))
			{
				yield return $"{Describe(c)} is {c.Bounds.Height:F1} high, not one of {string.Join("/", buttons)}";
			}
		}
	}

	private static string Describe(Control c) =>
		$"{c.GetType().Name} {(c.Name is { Length: > 0 } n ? n : Avalonia.Automation.AutomationProperties.GetName(c) is { Length: > 0 } a ? a : c is Button { Content: string s } ? s : "")}".TrimEnd();

	// The Games list's row pickers (type and profile) that show no choice.
	private static IEnumerable<string> EmptyRowPickers(Window window)
	{
		GameRowList list = window.GetVisualDescendants().OfType<GameRowList>().Single(l => l.IsEffectivelyVisible);
		foreach (ComboBox picker in list.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible && c.Classes.Contains("rowPicker")))
		{
			string game = (picker.DataContext as GameItemViewModel)?.Name ?? "default row";
			if (picker.SelectedItem == null)
			{
				yield return $"{game}: picker {Avalonia.Automation.AutomationProperties.GetName(picker)} has no selection";
			}
			else if (picker.ItemsSource is System.Collections.IEnumerable items && !items.Cast<object>().Contains(picker.SelectedItem))
			{
				yield return $"{game}: picker {Avalonia.Automation.AutomationProperties.GetName(picker)} selection is not one of its items";
			}
		}
	}

	private static ComboBox RowProfilePicker(Window window, GameItemViewModel game) =>
		window.GetVisualDescendants().OfType<GameRowList>().Single(l => l.IsEffectivelyVisible).GetVisualDescendants().OfType<ComboBox>()
			.Single(c => c.Classes.Contains("rowPicker") && c.DataContext == game && c.ItemsSource == game.Profile!.Options);

	// After a page or tab switch: past the page transition, laid out and rendered.
	internal static void Settle(Window window)
	{
		FitCheck.Settle(window);
		Thread.Sleep(320);
		FitCheck.Settle(window);
	}

	private static async Task ForEachLanguage(Func<string, Task> action)
	{
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				await action(code);
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}
}
