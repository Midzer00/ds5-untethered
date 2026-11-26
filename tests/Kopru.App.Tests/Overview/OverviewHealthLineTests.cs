// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Diagnostics;
using Kopru.Core.Health;
using Kopru.Core.Localization;
using Kopru.Core.Steam;

namespace Kopru.App.Tests;

// The Overview's health line: no line while all is good; otherwise one line (no row of cards), only the items that need a
// look and the worst first, every item's level and reason in its tooltip, and "Fix ›" at its end running the fix the item asks for (a menu
// with each fix by name when several items have one). It fits the page at the review sizes in every language.
public sealed class OverviewHealthLineTests
{
	private static readonly AdapterSummary GoodAdapter = new(AdapterVerdict.Suitable, OverviewHealthTests.Now.AddDays(-1));
	private static readonly SteamInputResult SteamOn = new(SteamInputStatus.OnGlobally, true, "advice");

	public static readonly string[] States = ["good", "one", "several", "blocking"];

	// All good; one warning (Steam Input on, its help as the fix); several (the adapter unsuitable, Steam Input on); blocking
	// (the driver missing, Steam Input on, the adapter never tested: the header offers Install Driver).
	public static HealthReport State(string state) => state switch
	{
		"good" => OverviewHealthTests.Report(adapter: GoodAdapter),
		"one" => OverviewHealthTests.Report(steam: SteamOn, adapter: GoodAdapter),
		"several" => OverviewHealthTests.Report(steam: SteamOn, adapter: new AdapterSummary(AdapterVerdict.Unsuitable, OverviewHealthTests.Now.AddDays(-1))),
		_ => OverviewHealthTests.Report(driver: false, steam: SteamOn),
	};

	// All good: no line at all (no "All Good" summary), no fix, and the line takes no room on the page.
	[AvaloniaFact]
	public void AllGood_ShowsNoLine_WithNoFix()
	{
		ForEach("good", (vm, _) =>
		{
			Assert.True(vm.Overview.IsHealthAllGood);
			Assert.False(vm.Overview.ShowsHealthLine);
			Assert.Equal(string.Empty, vm.Overview.HealthSummary);
			Assert.Empty(vm.Overview.HealthFixItems);
			Assert.False(vm.Overview.HasHealthFix);
			Assert.False(vm.Overview.HasHealthFixes);
		});
		WithLanguage(Language.English, () =>
		{
			(MainWindowViewModel vm, _) = Create("good");
			MainWindow window = Show(vm, 1152, 700);
			try
			{
				OverviewView view = window.GetVisualDescendants().OfType<OverviewView>().Single();
				Grid line = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "HealthLine");
				Assert.False(line.IsEffectivelyVisible);
				// Its row collapses: the page ends with the cards (no gap or empty line below them).
				var page = (Grid)line.Parent!;
				Control cards = page.Children.Single(c => Grid.GetRow(c) == 1);
				Assert.Equal(page.Bounds.Height, cards.Bounds.Bottom, 0.5);
			}
			finally
			{
				window.Close();
			}
		});
	}

	// Something needs a look: the line is there again, with the worst first (the other tests check its words and fixes).
	[AvaloniaFact]
	public void Warnings_ShowTheLine()
	{
		foreach (string state in States.Where(s => s != "good"))
		{
			ForEach(state, (vm, _) =>
			{
				Assert.True(vm.Overview.ShowsHealthLine, state);
				Assert.NotEqual(string.Empty, vm.Overview.HealthSummary);
				// Every item with its level and reason in the tooltip.
				string[] details = vm.Overview.HealthDetails.Split(Environment.NewLine);
				Assert.Equal(5, details.Length);
			});
		}
		WithLanguage(Language.English, () =>
		{
			(MainWindowViewModel vm, _) = Create("one");
			MainWindow window = Show(vm, 1152, 700);
			try
			{
				OverviewView view = window.GetVisualDescendants().OfType<OverviewView>().Single();
				Grid line = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "HealthLine");
				Assert.True(line.IsEffectivelyVisible);
				Assert.True(line.Bounds.Height > 0);
			}
			finally
			{
				window.Close();
			}
		});
	}

	[AvaloniaFact]
	public void OneWarning_NamesIt_AndFixRunsItsFix()
	{
		ForEach("one", (vm, fake) =>
		{
			Assert.False(vm.Overview.IsHealthAllGood);
			Assert.Equal(HealthFixes.Group(Text.HealthLevelWarning(), Text.HealthChipSteamInput()), vm.Overview.HealthSummary);
			Assert.True(vm.Overview.HasHealthFix);
			Assert.False(vm.Overview.HasHealthFixes);
			Assert.Contains(Text.FixSteamInputHelp(), vm.Overview.HealthFixTip);
			Assert.Contains(HealthFixes.Detail(Text.HealthChipSteamInput(), Text.HealthLevelWarning(), Text.HealthSteamOnGlobally()),
				vm.Overview.HealthDetails.Split(Environment.NewLine));

			vm.Overview.HealthFixCommand.Execute(null);
			Settle();
			Assert.Equal([FixAction.OpenSteamInputHelp], fake.Fixes);
			Assert.Same(vm.SupportedGames, vm.CurrentPage);
		});
		WithLanguage(Language.Turkish, () => Assert.Equal("Dikkat: Steam Girişi", Create("one").Vm.Overview.HealthSummary));
		WithLanguage(Language.English, () => Assert.Equal("Attention: Steam Input", Create("one").Vm.Overview.HealthSummary));
	}

	[AvaloniaFact]
	public void SeveralWarnings_WorstFirst_AndEveryFixStaysReachable()
	{
		ForEach("blocking", (vm, fake) =>
		{
			Assert.False(vm.Overview.IsHealthAllGood);
			Assert.StartsWith(HealthFixes.Group(Text.HealthLevelError(), Text.HealthChipDriver()) + " · "
				+ HealthFixes.Group(Text.HealthLevelWarning(), Text.HealthChipSteamInput()), vm.Overview.HealthSummary);
			Assert.Contains(Text.HealthChipAdapter(), vm.Overview.HealthSummary);
			Assert.DoesNotContain(Text.HealthChipHiding(), vm.Overview.HealthSummary);
			Assert.False(vm.Overview.HasHealthFix);
			Assert.True(vm.Overview.HasHealthFixes);
			FixAction[] fixes = [.. vm.Overview.HealthFixItems.Select(c => c.Fix)];
			Assert.Equal([FixAction.InstallDriver, FixAction.OpenSteamInputHelp, FixAction.RunAdapterTest], fixes.Take(3));
			Assert.Equal(Text.FixInstallDriver(), vm.Overview.HealthFixItems[0].FixLabel);

			// Each fix of the menu runs its own fix.
			vm.Overview.HealthFixItems[1].RunCommand.Execute(null);
			Settle();
			Assert.Equal([FixAction.OpenSteamInputHelp], fake.Fixes);
		});
		ForEach("several", (vm, _) =>
		{
			Assert.Equal(HealthFixes.Group(Text.HealthLevelError(), Text.HealthChipAdapter()) + " · "
				+ HealthFixes.Group(Text.HealthLevelWarning(), Text.HealthChipSteamInput()), vm.Overview.HealthSummary);
			Assert.True(vm.Overview.HasHealthFixes);
			Assert.Equal([FixAction.RunAdapterTest, FixAction.OpenSteamInputHelp], vm.Overview.HealthFixItems.Select(c => c.Fix));
			Assert.Equal([Text.FixRunAdapterTest(), Text.FixSteamInputHelp()], vm.Overview.HealthFixItems.Select(c => c.FixLabel));
		});
		WithLanguage(Language.Turkish, () => Assert.Equal("Sorun Var: Adaptör · Dikkat: Steam Girişi", Create("several").Vm.Overview.HealthSummary));
		WithLanguage(Language.Turkish, () => Assert.StartsWith("Sorun Var: Sürücü · Dikkat: Steam Girişi · Belirsiz: Adaptör",
			Create("blocking").Vm.Overview.HealthSummary));
		WithLanguage(Language.English, () => Assert.StartsWith("Problem: Driver · Attention: Steam Input · Unknown: Adapter",
			Create("blocking").Vm.Overview.HealthSummary));
	}

	// In the window: no cards, the one line; a click on "Fix ›" runs the fix (one fix), or opens the menu with each fix by
	// name and a click on one runs it (several).
	[AvaloniaFact]
	public void FixLink_IsClickable()
	{
		WithLanguage(Language.English, () =>
		{
			(MainWindowViewModel vm, FakeControllerService fake) = Create("one");
			MainWindow window = Show(vm, 1152, 700);
			try
			{
				OverviewView view = window.GetVisualDescendants().OfType<OverviewView>().Single();
				Assert.Empty(view.GetVisualDescendants().OfType<ItemsControl>().Where(i => i.IsEffectivelyVisible && i.ItemsSource == vm.Overview.HealthChips));
				Button fix = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "HealthFixButton");
				Assert.True(fix.IsEffectivelyVisible && fix.IsEffectivelyEnabled);
				Assert.False(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "HealthFixMenuButton").IsEffectivelyVisible);
				Assert.Equal(Text.HealthFixLink(), fix.GetVisualDescendants().OfType<TextBlock>().First().Text);
				Click(window, fix);
				Assert.Equal([FixAction.OpenSteamInputHelp], fake.Fixes);
			}
			finally
			{
				window.Close();
			}

			(vm, fake) = Create("several");
			window = Show(vm, 1152, 700);
			try
			{
				OverviewView view = window.GetVisualDescendants().OfType<OverviewView>().Single();
				Assert.False(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "HealthFixButton").IsEffectivelyVisible);
				Button menu = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "HealthFixMenuButton");
				Assert.True(menu.IsEffectivelyVisible && menu.IsEffectivelyEnabled);
				Click(window, menu);
				var flyout = (MenuFlyout)menu.Flyout!;
				Assert.True(flyout.IsOpen);
				MenuItem[] items = [.. OpenMenuItems(flyout)];
				Assert.Equal(vm.Overview.HealthFixItems.Select(c => c.FixLabel), items.Select(i => i.Header as string));
				MenuItem steam = items.Single(i => (string?)i.Header == Text.FixSteamInputHelp());
				steam.Command!.Execute(steam.CommandParameter);
				Settle();
				Assert.Equal([FixAction.OpenSteamInputHelp], fake.Fixes);
			}
			finally
			{
				window.Close();
			}
		});
	}

	// At the review sizes, in every shipped language and the pseudo-locale, in every state: the line is the page's last row,
	// one line inside the page, and "Fix ›" follows the text, whole, never on it.
	[AvaloniaFact]
	public void HealthLine_Fits_InEveryState()
	{
		List<string> problems = [];
		foreach (Language language in Localizer.Available.Append(Language.Pseudo))
		{
			WithLanguage(language, () =>
			{
				// Not "blocking" in the pseudo-locale: its header (four buttons with a game running) is too long there whatever the line.
				foreach (string state in States.Where(s => !(PseudoLocale.IsPseudo(language.Code) && s == "blocking")))
				{
					(MainWindowViewModel vm, _) = Create(state);
					foreach ((double w, double h) in FitCheck.ReviewSizes)
					{
						MainWindow window = Show(vm, w, h);
						try
						{
							string at = $"{language.Code} {state} {w}x{h}";
							OverviewView view = window.GetVisualDescendants().OfType<OverviewView>().Single();
							Grid line = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "HealthLine");
							if (state == "good")
							{
								// All good: no line to fit.
								if (line.IsEffectivelyVisible)
								{
									problems.Add($"{at}: the line shows although all is good");
								}
								continue;
							}
							TextBlock text = line.Children.OfType<TextBlock>().Single();
							Rect lineRect = FitCheck.Bounds(line, view);
							if (lineRect.Bottom > view.Bounds.Height + 0.5 || lineRect.Right > view.Bounds.Width + 0.5)
							{
								problems.Add($"{at}: the line {lineRect} leaves the page {view.Bounds.Size}");
							}
							if (text.TextLayout.TextLines.Count != 1)
							{
								problems.Add($"{at}: the line has {text.TextLayout.TextLines.Count} lines");
							}
							// The shipped languages show the whole line; the longer pseudo-locale may trim it (the tooltip has every item).
							if (!PseudoLocale.IsPseudo(language.Code) && text.TextLayout.TextLines.Any(l => l.HasCollapsed))
							{
								problems.Add($"{at}: \"{text.Text}\" is trimmed");
							}
							foreach (Button fix in line.Children.OfType<Button>().Where(b => b.IsEffectivelyVisible))
							{
								Rect t = FitCheck.Bounds(text, view);
								Rect f = FitCheck.Bounds(fix, view);
								if (f.Left < t.Right - 0.5 || f.Right > view.Bounds.Width + 0.5 || f.Bottom > view.Bounds.Height + 0.5 || f.Width < 1)
								{
									problems.Add($"{at}: Fix {f} is not after the text {t} inside the page");
								}
								if (FitCheck.IsTextCutOff(fix.GetVisualDescendants().OfType<TextBlock>().Single()))
								{
									problems.Add($"{at}: Fix is cut off");
								}
							}
							Assert.Contains(line.Children.OfType<Button>(), b => b.IsEffectivelyVisible);
						}
						finally
						{
							window.Close();
						}
					}
				}
			});
		}
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	// The window in the language chosen now (the view model applies its settings' language; the pseudo-locale is not one, so
	// the choice is put back), the report built in it.
	private static (MainWindowViewModel Vm, FakeControllerService Fake) Create(string state)
	{
		Language? chosen = Localizer.Chosen;
		var fake = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = chosen?.Code }),
			Now: () => OverviewHealthTests.Now));
		Localizer.Chosen = chosen;
		vm.Setup = null;
		fake.Health = State(state);
		fake.RaiseStateChanged();
		Settle();
		return (vm, fake);
	}

	private static void ForEach(string state, Action<MainWindowViewModel, FakeControllerService> check)
	{
		foreach (Language language in Localizer.Available)
		{
			WithLanguage(language, () =>
			{
				(MainWindowViewModel vm, FakeControllerService fake) = Create(state);
				check(vm, fake);
			});
		}
	}

	private static MainWindow Show(MainWindowViewModel vm, double width, double height)
	{
		var window = new MainWindow { DataContext = vm, Width = width, Height = height };
		window.Show();
		vm.Navigate(vm.Overview);
		FitCheck.Settle(window);
		return window;
	}

	private static void Click(Window window, Visual target)
	{
		Point center = FitCheck.Bounds(target, window).Center;
		window.MouseDown(center, MouseButton.Left);
		window.MouseUp(center, MouseButton.Left);
		FitCheck.Settle(window);
	}

	// The open menu's items: the flyout's popup is a top level of its own, its child the menu presenter.
	private static IEnumerable<MenuItem> OpenMenuItems(MenuFlyout flyout)
	{
		var popup = (Avalonia.Controls.Primitives.Popup)typeof(Avalonia.Controls.Primitives.PopupFlyoutBase)
			.GetProperty("Popup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
			.GetValue(flyout)!;
		Control presenter = (Control)popup.Child!;
		return presenter.GetVisualDescendants().OfType<MenuItem>();
	}

	private static void Settle() => Dispatcher.UIThread.RunJobs();

	private static void WithLanguage(Language language, Action action)
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = language;
			action();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}
}
