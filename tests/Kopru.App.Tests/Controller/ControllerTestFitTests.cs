// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The controller page's Test tab fits the window: at every size in the matrix (the window's minimum, common screens,
// ultra-wide, tall, 4:3) and in both languages, through the window's own scale (UiScale), the tab shows all of its cards
// without vertical scrolling, nothing is clipped, cut off or outside the page column, and no two cards overlap. UiScale gives
// the layout at least 1100x700 at every allowed window size, so this holds down to the window's minimum.
public sealed class ControllerTestFitTests
{
	public static (double Width, double Height)[] Matrix => FitCheck.Matrix;

	[AvaloniaFact]
	public void TestTab_FitsWithoutScrolling_AtEverySize_InBothLanguages()
	{
		var minimum = new MainWindow();
		Assert.Equal((960, 640), (minimum.MinWidth, minimum.MinHeight));
		Assert.Contains(Matrix, s => s.Width == minimum.MinWidth && s.Height == minimum.MinHeight);

		List<string> problems = [];
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				var service = new FakeControllerService(animate: false);
				var vm = new MainWindowViewModel(service, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = code })));
				vm.Overview.ToggleBridgeCommand.Execute(null);
				service.Tick();
				foreach ((double width, double height) in Matrix)
				{
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.OpenController(ControllerPageViewModel.TestTab);
					service.LiveHandler!(TesterTests.Sample);
					vm.Tester.Tick();
					Settle(window);
					Assert.Equal(UiScale.Factor(width, height), window.ContentScale, 3);
					problems.AddRange(Check(window).Select(p => $"{code} {width}x{height}: {p}"));
					// For a design review: every size as a PNG (only with KOPRU_FIT_RENDER_DIR set).
					if (Environment.GetEnvironmentVariable("KOPRU_FIT_RENDER_DIR") is { Length: > 0 } dir)
					{
						Directory.CreateDirectory(dir);
						Thread.Sleep(320);
						service.LiveHandler!(TesterTests.Sample);
						vm.Tester.Tick();
						Settle(window);
						using var frame = window.CaptureRenderedFrame();
						frame!.Save(Path.Combine(dir, $"controller-test-{width}x{height}-{code}.png"));
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

	// Every tab label of the controller page is whole whichever tab is selected (the selected one is bold, so wider), in
	// both languages, at the minimum, the design and a large window.
	[AvaloniaFact]
	public void TabLabels_AreWhole_WhicheverTabIsSelected()
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
				// One window resized between the sizes (a scale change re-lays the labels out, as when the user resizes).
				var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
				window.Show();
				foreach ((double width, double height) in new[] { (2560.0, 1080.0), (960.0, 640.0), (1100.0, 700.0), (1920.0, 1080.0), (1100.0, 700.0) })
				{
					window.Width = width;
					window.Height = height;
					foreach (int tab in new[] { ControllerPageViewModel.TestTab, ControllerPageViewModel.OutputsTab, ControllerPageViewModel.RemapTab })
					{
						vm.OpenController(tab);
						Settle(window);
						ListBox tabs = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.IsEffectivelyVisible && ReferenceEquals(l.ItemsSource, vm.Controller.Tabs));
						foreach (ListBoxItem item in tabs.GetVisualDescendants().OfType<ListBoxItem>())
						{
							Rect itemRect = FitCheck.Bounds(item, tabs);
							foreach (TextBlock text in item.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Opacity > 0 && !string.IsNullOrEmpty(t.Text)))
							{
								Rect r = FitCheck.Bounds(text, tabs);
								// The width the label needs in its current weight, measured afresh (a label measured before it turned
								// bold keeps its narrower size and is cut off on the right; 1 DIP of slack for layout rounding).
								var fresh = new TextBlock { Text = text.Text, FontSize = text.FontSize, FontWeight = text.FontWeight, FontFamily = text.FontFamily, TextWrapping = Avalonia.Media.TextWrapping.NoWrap };
								fresh.Measure(Size.Infinity);
								if (r.Left < itemRect.Left - 0.5 || r.Right > itemRect.Right + 0.5 || text.Bounds.Width + 1 < fresh.DesiredSize.Width
									|| text.TextLayout.TextLines.Any(l => l.HasCollapsed))
								{
									problems.Add($"{code} {width}x{height} tab {tab} selected: \"{text.Text}\" {r} in {itemRect}");
								}
							}
						}
					}
				}
				window.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
		Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
	}

	internal static void Settle(Window window) => FitCheck.Settle(window);

	// What is wrong with the Test tab as laid out now.
	internal static IEnumerable<string> Check(Window window)
	{
		ControllerView page = window.GetVisualDescendants().OfType<ControllerView>().Single(v => v.IsEffectivelyVisible);
		ScrollViewer scroll = page.GetVisualDescendants().OfType<ScrollViewer>().First();
		StackPanel column = page.GetVisualDescendants().OfType<StackPanel>().First(p => p.Classes.Contains("page"));
		TesterView tab = page.GetVisualDescendants().OfType<TesterView>().Single();
		TesterLayout cards = tab.GetVisualDescendants().OfType<TesterLayout>().Single();

		// The cards (and the status line) are apart, at least 8 apart, and inside the page column; every visible control
		// lies inside its card, and no text is cut off.
		List<(string Name, Rect Rect)> blocks = [("status", FitCheck.Bounds(tab.GetVisualDescendants().OfType<WrapPanel>().First(), column))];
		blocks.AddRange(cards.Children.Select((c, i) => ($"card {i}", FitCheck.Bounds(c, column))));
		return FitCheck.Scrolls(scroll, cards.IsWide ? "wide" : "stacked")
			.Concat(FitCheck.Apart(blocks, FitCheck.Bounds(column, column)))
			.Concat(cards.Children.SelectMany(card => FitCheck.Inside(card, column)))
			.Concat(FitCheck.CutText(tab));
	}
}
