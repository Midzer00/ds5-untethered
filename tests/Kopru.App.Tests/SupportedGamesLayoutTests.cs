// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Games;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The Supported Games page at both window sizes and in both languages, with the list update on (its controls shown):
// every game row shares one column layout, so a row with marks or a Remove button keeps the same pickers and name width as
// the others. Writes supported-games-full-{small|large}-{tr|en}.png to KOPRU_PAGE_RENDER_DIR when it is set.
public sealed class SupportedGamesLayoutTests
{
	[AvaloniaFact]
	public void GameRows_ShareOneColumnLayout()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_PAGE_RENDER_DIR");
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height, string size) in PageSizeRenderTests.Sizes)
				{
					var vm = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(new MemorySettingsStore(new AppSettings
					{
						GameListCheckedAt = DateTimeOffset.Now,
						Language = code,
						AlwaysGames = ["Returnal"],
						UserGames = [new UserGame("Indie Game", @"C:\Games\Indie\Indie.exe")]
					}), GameList: new GameCatalogUpdater(new HttpClient(), Path.Combine(Path.GetTempPath(), "kopru-layout-games.json"), "https://example.invalid/list.json")));
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
					Settle();
					window.UpdateLayout();

					// The switches end on the sliders' right edge (no room kept for on/off text). Measured in layout units (inside the
					// window's scale), so a scaled window compares the same edges.
					Visual layout = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.VisualLayerManager>().First();
					double slidersRight = window.GetVisualDescendants().OfType<Slider>().Select(s => Math.Round(Bounds(s, layout).Right)).Max();
					foreach (ToggleSwitch toggle in window.GetVisualDescendants().OfType<ToggleSwitch>().Where(t => t.Parent is Grid g && g.Classes.Contains("settingRow")))
					{
						Assert.True(Math.Abs(Math.Round(Bounds(toggle, layout).Right) - slidersRight) <= 2, $"switch {Bounds(toggle, layout)} sliders {slidersRight} size {window.Width}x{window.Height} scale {((MainWindow)window).ContentScale}");
					}

					// Per row in view: the right edge and width of each picker, and the width of the name column.
					var rows = window.GetVisualDescendants().OfType<ComboBox>().Where(b => b.Classes.Contains("rowPicker") && b.DataContext is GameItemViewModel)
						.GroupBy(b => b.DataContext).ToList();
					Assert.True(rows.Count >= 3);
					Assert.Contains(rows, r => ((GameItemViewModel)r.Key!).IsUserAdded);
					var edges = rows.SelectMany(r => r.Select(b => Bounds(b, window))).ToList();
					Assert.Single(edges.Select(e => Math.Round(e.Right)).Distinct());
					Assert.Single(edges.Select(e => Math.Round(e.Width)).Distinct());
					// The controller type for every game sits above the rows' pickers, on the same edge and as wide.
					Rect all = Bounds(window.GetVisualDescendants().OfType<ComboBox>().Single(b => b.Name == "PadTypeChoicePicker"), window);
					Assert.Equal(Math.Round(edges[0].Right), Math.Round(all.Right));
					Assert.Equal(Math.Round(edges[0].Width), Math.Round(all.Width));
					// The name column (name, features, marks) is as wide in every row.
					var nameColumns = window.GetVisualDescendants().OfType<TextBlock>()
						.Where(t => t.DataContext is GameItemViewModel g && t.Text == g.Name && t.Parent is StackPanel)
						.Select(t => Math.Round(((Control)t.Parent!).Bounds.Width)).ToList();
					Assert.Equal(rows.Count, nameColumns.Count);
					Assert.Single(nameColumns.Distinct());

					using var frame = window.CaptureRenderedFrame();
					Assert.NotNull(frame);
					if (!string.IsNullOrEmpty(outDir))
					{
						Directory.CreateDirectory(outDir);
						frame!.Save(Path.Combine(outDir, $"supported-games-full-{size}-{code}.png"));
					}
					window.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Thread.Sleep(320);
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
	}

	private static Rect Bounds(Visual control, Visual root) =>
		control.TransformToVisual(root) is { } m ? new Rect(control.Bounds.Size).TransformToAABB(m) : default;
}
