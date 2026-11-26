// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// Every page fits 1100x700, 1152x700 and 960x640 without page scrolling or cut-off labels, for every shipped language
// (data-driven from Localizer.Available) and for the test-only pseudo-locale ("qps", ~35% longer accented strings).
public sealed class LocalizationFitTests
{
	private static readonly string[] SettingsTabs = ["general", "controllers", "audio", "shortcuts", "mouse", "notifications"];

	public static IEnumerable<object[]> ShippedLanguageCodes()
	{
		foreach (Language lang in Localizer.Available)
		{
			yield return [lang.Code];
		}
	}

	[AvaloniaTheory]
	[MemberData(nameof(ShippedLanguageCodes))]
	public async Task EveryPage_FitsReviewSizes_ForEveryShippedLanguage(string code)
	{
		List<string> problems = await CheckAllPagesAsync(new Language(code));
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	[AvaloniaFact]
	public async Task EveryPage_FitsReviewSizes_InPseudoLocale()
	{
		List<string> problems = await CheckAllPagesAsync(Language.Pseudo);
		Assert.True(problems.Count == 0, "Pseudo-locale overflows:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
	}

	internal static async Task<List<string>> CheckAllPagesAsync(Language language)
	{
		List<string> problems = [];
		string code = language.Code;
		bool allowVerticalSafetyNet = PseudoLocale.IsPseudo(code);
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = language;
			// Note: AppSettings.Language is ignored for qps (FromCode("qps") returns null), so keep Localizer.Chosen set.
			(MainWindowViewModel vm, FakeControllerService fake) = SettingsFitTests.FullCase(code);
			Localizer.Chosen = language;

			foreach ((double width, double height) in FitCheck.ReviewSizes)
			{
				string at = $"{code} {width}x{height}";
				var window = new MainWindow { DataContext = vm, Width = width, Height = height };
				window.Show();
				try
				{
					// Sidebar nav labels must not clip.
					FitCheck.Settle(window);
					CheckSidebar(window, at, problems);

					// 1. Overview
					vm.Navigate(vm.Overview);
					FitCheck.Settle(window);
					CheckOverview(window, at, problems);

					// 2. Supported Games
					vm.Navigate(vm.SupportedGames);
					FitCheck.Settle(window);
					CheckSupportedGames(window, at, problems);

					// 3. Other Games (Haptics & Triggers)
					vm.Navigate(vm.OtherGames);
					FitCheck.Settle(window);
					CheckOtherGames(window, at, problems);

					// 4. Controller (Test, Outputs, Remap)
					vm.OpenController(ControllerPageViewModel.TestTab);
					fake.LiveHandler!(TesterTests.Sample);
					vm.Tester.Tick();
					FitCheck.Settle(window);
					problems.AddRange(ControllerTestFitTests.Check(window, allowVerticalSafetyNet).Select(p => $"{at} controller-test: {p}"));

					foreach ((int tab, string tabName) in new[] { (ControllerPageViewModel.OutputsTab, "outputs"), (ControllerPageViewModel.ButtonsTab, "buttons"), (ControllerPageViewModel.GyroTab, "gyro-sticks") })
					{
						Kopru.Core.Input.GyroSettings savedGyro = vm.Settings.Gyro;
						if (tab == ControllerPageViewModel.GyroTab)
						{
							vm.Settings.Gyro = savedGyro with { Mode = Kopru.Core.Input.GyroMode.WhileL2 };
						}
						vm.OpenController(tab);
						FitCheck.Settle(window);
						CheckScrollableOrSinglePage(window, $"{at} controller-{tabName}", checkScroll: !allowVerticalSafetyNet, problems);
						if (tab == ControllerPageViewModel.GyroTab)
						{
							vm.Settings.Gyro = savedGyro;
						}
					}

					// 5. Light
					vm.Navigate(vm.Light);
					FitCheck.Settle(window);
					CheckLight(window, at, problems);

					// 6. Settings (all 6 tabs)
					vm.Navigate(vm.SettingsPage);
					for (int tab = 0; tab < SettingsTabs.Length; tab++)
					{
						vm.SettingsPage.SelectedTab = tab;
						FitCheck.Settle(window);
						problems.AddRange(SettingsFitTests.Check(window, allowVerticalSafetyNet).Select(p => $"{at} settings-{SettingsTabs[tab]}: {p}"));
					}
				}
				finally
				{
					window.Close();
				}

				// 7. Game Panel dialog (all 3 segments)
				Localizer.Chosen = language;
				(MainWindowViewModel gamesVm, MainWindow gamesWindow) = await AuditRenderTests.OpenGames(code, width, height);
				Localizer.Chosen = language;
				try
				{
					CheckGamePanel(gamesVm, gamesWindow, at, problems);
				}
				finally
				{
					gamesWindow.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
		return problems;
	}

	private static void CheckGamePanel(MainWindowViewModel gamesVm, MainWindow window, string at, List<string> problems)
	{
		GameItemViewModel running = gamesVm.SupportedGames.Games.Single(g => g.Name == AuditRenderTests.MadeUpGame);
		gamesVm.SupportedGames.OpenGameCommand.Execute(running);
		gamesVm.SupportedGames.Panel.UseOwnSettingsCommand.Execute(null);
		gamesVm.SupportedGames.Panel.SaveProfileNameCommand.Execute(null);

		(int Segment, string Name)[] segments =
		[
			(GamePanelViewModel.GameSegment, "game"),
			(GamePanelViewModel.VibrationSegment, "vibration"),
			(GamePanelViewModel.TriggersSegment, "triggers")
		];
		foreach ((int seg, string segName) in segments)
		{
			gamesVm.SupportedGames.Panel.SelectedSegment = seg;
			FitCheck.Settle(window);
			Border sheet = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "GameDialog");
			GamePanelView panelView = sheet.GetVisualDescendants().OfType<GamePanelView>().Single();
			ScrollViewer panelScroll = panelView.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "PanelScroll");
			problems.AddRange(FitCheck.Scrolls(panelScroll, segName).Select(p => $"{at} game-panel-{segName}: {p}"));
			problems.AddRange(FitCheck.CutText(sheet)
				.Where(p => !p.Contains(AuditRenderTests.MadeUpGame))
				.Select(p => $"{at} game-panel-{segName}: {p}"));
		}
	}

	private static void CheckSidebar(MainWindow window, string at, List<string> problems)
	{
		Border? nav = window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("sidebar"));
		if (nav == null)
		{
			return;
		}
		foreach (TextBlock text in nav.GetVisualDescendants().OfType<TextBlock>()
			.Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && !t.Classes.Contains("caption")))
		{
			if (FitCheck.IsTextCutOff(text))
			{
				problems.Add($"{at} sidebar: text cut off: \"{text.Text}\"");
			}
		}
	}

	private static void CheckOverview(MainWindow window, string at, List<string> problems)
	{
		OverviewView view = window.GetVisualDescendants().OfType<OverviewView>().Single(v => v.IsEffectivelyVisible);
		foreach (ScrollViewer sv in view.GetVisualAncestors().OfType<ScrollViewer>())
		{
			problems.AddRange(FitCheck.Scrolls(sv, "overview").Select(p => $"{at} overview: {p}"));
		}
		foreach (ScrollViewer sv in view.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible))
		{
			problems.AddRange(FitCheck.Scrolls(sv, "overview-inner").Select(p => $"{at} overview: {p}"));
		}
		foreach (TextBlock label in view.GetVisualDescendants().OfType<TextBlock>()
			.Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)
				&& (t.Classes.Contains("label") || t.Classes.Contains("tileLabel") || t.Classes.Contains("pageTitle") || t.Classes.Contains("cardTitle")
					|| t.Classes.Contains("h1") || t.Classes.Contains("h2") || t.Classes.Contains("section"))))
		{
			if (FitCheck.IsTextCutOff(label))
			{
				problems.Add($"{at} overview: text cut off: \"{label.Text}\"");
			}
		}
	}

	private static void CheckSupportedGames(MainWindow window, string at, List<string> problems)
	{
		SupportedGamesView view = window.GetVisualDescendants().OfType<SupportedGamesView>().Single(v => v.IsEffectivelyVisible);
		foreach (ScrollViewer sv in view.GetVisualAncestors().OfType<ScrollViewer>())
		{
			problems.AddRange(FitCheck.Scrolls(sv, "games").Select(p => $"{at} games: {p}"));
		}
		Grid? options = view.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Name == "ListOptions");
		if (options != null)
		{
			problems.AddRange(FitCheck.CutText(options).Select(p => $"{at} games-options: {p}"));
		}
		foreach (TextBlock text in view.GetVisualDescendants().OfType<TextBlock>()
			.Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && !t.GetVisualAncestors().Any(a => a is ListBox)))
		{
			if (FitCheck.IsTextCutOff(text))
			{
				problems.Add($"{at} games: text cut off: \"{text.Text}\"");
			}
		}
	}

	private static void CheckOtherGames(MainWindow window, string at, List<string> problems)
	{
		OtherGamesView view = window.GetVisualDescendants().OfType<OtherGamesView>().Single(v => v.IsEffectivelyVisible);
		OtherGamesViewModel page = (OtherGamesViewModel)view.DataContext!;
		int before = page.SelectedTab;
		// Both tabs, with the fourth try button showing; no scrolling at all, in every language and the pseudo-locale too (the
		// page's scroll viewer is only a safety net).
		int fallback = page.Feel.FallbackIndex;
		page.Feel.FallbackIndex = 1;
		foreach ((int tab, string tabName) in new[] { (OtherGamesViewModel.GameEffectsTab, "game"), (OtherGamesViewModel.RumbleTab, "rumble") })
		{
			page.SelectedTab = tab;
			FitCheck.Settle(window);
			foreach (ScrollViewer sv in view.GetVisualAncestors().OfType<ScrollViewer>())
			{
				problems.AddRange(FitCheck.Scrolls(sv, "haptics").Select(p => $"{at} haptics-{tabName}: {p}"));
			}
			foreach (ScrollViewer sv in view.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible))
			{
				problems.AddRange(FitCheck.Scrolls(sv, "haptics-inner").Select(p => $"{at} haptics-{tabName}: {p}"));
			}
			problems.AddRange(FitCheck.CutText(view).Select(p => $"{at} haptics-{tabName}: {p}"));
		}
		page.Feel.FallbackIndex = fallback;
		page.SelectedTab = before;
	}

	private static void CheckLight(MainWindow window, string at, List<string> problems)
	{
		LightView view = window.GetVisualDescendants().OfType<LightView>().Single(v => v.IsEffectivelyVisible);
		foreach (ScrollViewer sv in view.GetVisualAncestors().OfType<ScrollViewer>())
		{
			problems.AddRange(FitCheck.Scrolls(sv, "light").Select(p => $"{at} light: {p}"));
		}
		problems.AddRange(FitCheck.CutText(view).Select(p => $"{at} light: {p}"));
	}

	private static void CheckScrollableOrSinglePage(MainWindow window, string label, bool checkScroll, List<string> problems)
	{
		ControllerView view = window.GetVisualDescendants().OfType<ControllerView>().Single(v => v.IsEffectivelyVisible);
		if (checkScroll)
		{
			foreach (ScrollViewer sv in view.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible))
			{
				problems.AddRange(FitCheck.Scrolls(sv, label).Select(p => $"{label}: {p}"));
			}
		}
		foreach (TextBlock text in view.GetVisualDescendants().OfType<TextBlock>()
			.Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && !t.GetVisualAncestors().Any(a => a is ListBox)))
		{
			if (FitCheck.IsTextCutOff(text))
			{
				problems.Add($"{label}: text cut off: \"{text.Text}\"");
			}
		}
	}

	[AvaloniaFact]
	public void FitCheck_TrimmedTextWithMatchingToolTip_Fits_WhileMissingOrMismatchedToolTip_Fails()
	{
		const string longText = "A deliberately long translated label that overflows a narrow slot";
		var plainTrimmed = new TextBlock
		{
			Text = longText,
			Width = 80,
			TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
			TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
		};
		var wrongTip = new TextBlock
		{
			Text = longText,
			Width = 80,
			TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
			TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
		};
		ToolTip.SetTip(wrongTip, "Unrelated description");

		var matchingTip = new TextBlock
		{
			Text = longText,
			Width = 80,
			TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
			TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
		};
		ToolTip.SetTip(matchingTip, longText);

		var richTipText = new TextBlock
		{
			Text = longText,
			Width = 80,
			TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
			TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
		};
		var richButton = new Button { Content = richTipText };
		ToolTip.SetTip(richButton, new StackPanel
		{
			Children =
			{
				new TextBlock { Text = longText },
				new TextBlock { Text = "Extra hint" }
			}
		});

		var untrimmedWithTip = new TextBlock
		{
			Text = longText,
			Width = 80,
			TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
			TextTrimming = Avalonia.Media.TextTrimming.None
		};
		ToolTip.SetTip(untrimmedWithTip, longText);

		var window = new Window
		{
			Width = 300,
			Height = 300,
			Content = new StackPanel
			{
				HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
				Children = { plainTrimmed, wrongTip, matchingTip, richButton, untrimmedWithTip }
			}
		};
		window.Show();
		try
		{
			FitCheck.Settle(window);
			Assert.True(FitCheck.IsTextCutOff(plainTrimmed));
			Assert.True(FitCheck.IsTextCutOff(wrongTip));
			Assert.False(FitCheck.IsTextCutOff(matchingTip));
			Assert.False(FitCheck.IsTextCutOff(richTipText));
			Assert.True(FitCheck.IsTextCutOff(untrimmedWithTip));
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public void FitCheck_VerticalScrollSafetyNet_AllowedOnlyWhenRequested_AndRequiresAutoOrVisibleBar()
	{
		var autoScroll = new ScrollViewer
		{
			Width = 200,
			Height = 100,
			HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
			VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
			Content = new Border { Height = 260 }
		};
		var disabledScroll = new ScrollViewer
		{
			Width = 200,
			Height = 100,
			HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
			VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
			Content = new Border { Height = 260 }
		};
		var window = new Window
		{
			Width = 440,
			Height = 160,
			Content = new StackPanel
			{
				Orientation = Avalonia.Layout.Orientation.Horizontal,
				Children = { autoScroll, disabledScroll }
			}
		};
		window.Show();
		try
		{
			FitCheck.Settle(window);
			// Strict mode (shipped en/tr): scrolling always fails.
			Assert.NotEmpty(FitCheck.Scrolls(autoScroll, "strict", allowVerticalSafetyNet: false));
			// Safety-net mode (qps only): Auto scrollbar fits, Disabled still fails.
			Assert.Empty(FitCheck.Scrolls(autoScroll, "qps", allowVerticalSafetyNet: true));
			Assert.NotEmpty(FitCheck.Scrolls(disabledScroll, "qps", allowVerticalSafetyNet: true));
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaFact]
	public void ProportionalRowPanel_SizesToText_FillsRow_AndWrapsWhenNarrow()
	{
		var shortLabel = new TextBlock { Text = "Audio", TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxLines = 2 };
		var longLabel = new TextBlock { Text = "Notifications And Alerts", TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxLines = 2 };
		var panel = new Kopru.App.Controls.ProportionalRowPanel
		{
			Children = { shortLabel, longLabel }
		};
		var host = new Border
		{
			Width = 400,
			HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
			VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
			Child = panel
		};
		var window = new Window { Width = 500, Height = 200, Content = host };
		window.Show();
		try
		{
			// Wide enough for one line: longer label gets more width, and both fill the full 400px row.
			FitCheck.Settle(window);
			Assert.True(longLabel.Bounds.Width > shortLabel.Bounds.Width + 40);
			Assert.Equal(400, shortLabel.Bounds.Width + longLabel.Bounds.Width, 1);
			Assert.Single(longLabel.TextLayout.TextLines);

			// Narrower than single-line sum: wraps onto two lines and grows vertically instead of clipping.
			double oneLineHeight = panel.Bounds.Height;
			host.Width = 140;
			FitCheck.Settle(window);
			Assert.Equal(140, shortLabel.Bounds.Width + longLabel.Bounds.Width, 1);
			Assert.Equal(2, longLabel.TextLayout.TextLines.Count);
			Assert.True(panel.Bounds.Height > oneLineHeight + 4);
		}
		finally
		{
			window.Close();
		}
	}
}
