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

// The Games page at every window size and in both languages, with the list update on (its controls shown): every game row
// shares one column layout, so a row with a button under its name (Oyna) keeps the same pickers and name width as the others (each picker
// on one edge, side by side where the list is wide); the page holds no profile settings (they are on Haptics And Triggers)
// and the list's options line is whole inside the card. Writes supported-games-full-{size}-{tr|en}.png to
// KOPRU_PAGE_RENDER_DIR when it is set.
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
						UserGames = [new UserGame("Indie Game", @"C:\Games\Indie\Indie.exe")]
					}), GameList: new GameCatalogUpdater(new HttpClient(), Path.Combine(Path.GetTempPath(), "kopru-layout-games.json"), "https://example.invalid/list.json")));
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
					Settle();
					window.UpdateLayout();

					// No profile settings here (one place for each: Haptics And Triggers, the light on the Light page); the list's
					// options line (update, pictures) is whole and inside the list card, which ends inside the window.
					SupportedGamesView view = window.GetVisualDescendants().OfType<SupportedGamesView>().Single();
					Assert.Empty(view.GetVisualDescendants().OfType<Slider>());
					Assert.Empty(view.GetVisualDescendants().OfType<ToggleSwitch>().Where(t => t.TemplatedParent == null));
					Grid options = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ListOptions");
					Border listCard = options.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("panel"));
					Assert.Empty(FitCheck.CutText(options).Concat(FitCheck.Inside(listCard, window)));
					Assert.True(Bounds(listCard, window).Bottom <= window.ClientSize.Height + 0.5, $"list card {Bounds(listCard, window)} size {window.Width}x{window.Height}");

					// Per row in view: the right edge and width of each picker, and the width of the name column.
					var rows = window.GetVisualDescendants().OfType<ComboBox>().Where(b => b.Classes.Contains("rowPicker") && b.DataContext is GameItemViewModel)
						.GroupBy(b => b.DataContext).ToList();
					Assert.True(rows.Count >= 3);
					List<Rect> typeEdges = PickerEdges(rows, 0, window);
					PickersShareTheirColumns(rows, window);
					// The default controller type sits above the rows' controller type pickers, on the same right edge and at least as
					// wide (its choice is whole).
					ComboBox defaultPicker = window.GetVisualDescendants().OfType<ComboBox>().Single(b => b.Name == "PadTypeChoicePicker");
					Rect all = Bounds(defaultPicker, window);
					Assert.Equal(Math.Round(typeEdges[0].Right), Math.Round(all.Right));
					Assert.True(Math.Round(all.Width) >= Math.Round(typeEdges[0].Width), $"default picker {all} row picker {typeEdges[0]}");
					Assert.Empty(FitCheck.CutText(defaultPicker));
					// The name column (name, features) is as wide in every row.
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

	// Rows of games found on this PC (made-up Steam and Epic games) at the smallest and the largest window, in both languages,
	// in "Installed" and in "All": the found rows share the one column layout (pickers on one edge, one name column width),
	// their launcher text, Hide and the line pointing to Add Game are whole, and nothing leaves the card. Writes
	// supported-games-found-{installed|all}-{min|xl}-{tr|en}.png to KOPRU_PAGE_RENDER_DIR when it is set.
	[AvaloniaFact]
	public async Task FoundGameRows_ShareTheLayout_AndFit()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_PAGE_RENDER_DIR");
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height, string size) in new[] { PageSizeRenderTests.Sizes[0], PageSizeRenderTests.Sizes[^1] })
				{
					foreach (bool unsupported in new[] { true, false })
					{
						string view = unsupported ? "unsupported" : "all";
						var vm = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(new MemorySettingsStore(new AppSettings
						{
							GameListCheckedAt = DateTimeOffset.Now,
							Language = code,
							UserGames = [new UserGame("Indie Game", @"C:\Games\Indie\Indie.exe")]
						}), GameList: new GameCatalogUpdater(new HttpClient(), Path.Combine(Path.GetTempPath(), "kopru-layout-games.json"), "https://example.invalid/list.json"),
							InstalledGames: FakeFoundGames.Installed(FakeFoundGames.Folders())));
						await vm.SupportedGames.ScanTask;
						vm.SupportedGames.ShowUnsupported = unsupported;
						var window = new MainWindow { DataContext = vm, Width = width, Height = height };
						window.Show();
						vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
						Settle();
						window.UpdateLayout();
						string where = $"{width}x{height} {code} {view}";

						var rows = window.GetVisualDescendants().OfType<ComboBox>().Where(b => b.Classes.Contains("rowPicker") && b.DataContext is GameItemViewModel)
							.GroupBy(b => b.DataContext).ToList();
						Assert.True(rows.Count >= 3, where);
						Assert.Equal(unsupported, rows.Any(r => ((GameItemViewModel)r.Key!).IsFound));
						PickersShareTheirColumns(rows, window);
						var nameColumns = window.GetVisualDescendants().OfType<TextBlock>()
							.Where(t => t.DataContext is GameItemViewModel g && t.Text == g.Name && t.Parent is StackPanel)
							.Select(t => Math.Round(((Control)t.Parent!).Bounds.Width)).ToList();
						Assert.Single(nameColumns.Distinct());

						// Captions and buttons of the rows (not the names, which may end in "…") and the hint line are whole.
						TextBlock hint = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "InstalledAddHint");
						Border card = hint.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("panel"));
						var problems = window.GetVisualDescendants().OfType<WrapPanel>().Where(w => w.DataContext is GameItemViewModel)
							.SelectMany(FitCheck.CutText).Concat(FitCheck.Inside(card, window)).Select(p => $"{where}: {p}").ToList();
						Assert.Equal(unsupported, hint.IsEffectivelyVisible);
						if (unsupported)
						{
							problems.AddRange(FitCheck.CutText((Visual)hint.Parent!).Where(p => p.Contains(hint.Text!)).Select(p => $"{where}: {p}"));
							Assert.True(Bounds(hint, window).Bottom <= Bounds(card, window).Bottom + 0.5, $"{where}: hint {Bounds(hint, window)}");
							Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.DataContext is GameItemViewModel { IsFound: true } && b.IsEffectivelyVisible
								&& Equals(b.Content, Text.HideGame()));
						}
						Assert.Empty(problems);

						using var frame = window.CaptureRenderedFrame();
						Assert.NotNull(frame);
						if (!string.IsNullOrEmpty(outDir))
						{
							Directory.CreateDirectory(outDir);
							frame!.Save(Path.Combine(outDir, $"supported-games-found-{view}-{size}-{code}.png"));
						}
						window.Close();
					}
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// Games with a long made-up list at every size of the fit matrix, in both languages, "All" and "Installed": the list card
	// holds everything, the header, filter, default controller type and list options lines are whole and the rows' pickers
	// share their columns. Writes games-fit-{page}-{w}x{h}-{tr|en}.png to KOPRU_FIT_RENDER_DIR
	// when it is set.
	[AvaloniaFact]
	public async Task GamePages_FitAtEverySize()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_FIT_RENDER_DIR") is { Length: > 0 } d ? d : null;
		Language? before = Localizer.Chosen;
		List<string> problems = [];
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				foreach ((double width, double height) in FitCheck.Matrix)
				{
					foreach (string page in new[] { "supported-all", "unsupported" })
					{
						MainWindowViewModel vm = GameListScrollTests.Open(installedOnly: page == "supported-installed", code, unsupported: page == "unsupported");
						await vm.SupportedGames.ScanTask;
						var window = new MainWindow { DataContext = vm, Width = width, Height = height };
						window.Show();
						vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
						FitCheck.Settle(window);
						string where = $"{width}x{height} {code} {page}";
						UserControl view = window.GetVisualDescendants().OfType<SupportedGamesView>().Single();
						problems.AddRange(FitCheck.CutText(view.GetVisualDescendants().OfType<Grid>().First(g => g.Classes.Contains("pageHeader"))).Select(p => $"{where}: {p}"));

						{
							GameRowList list = view.GetVisualDescendants().OfType<GameRowList>().Single();
							Border card = list.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("panel"));
							problems.AddRange(FitCheck.Inside(card, window).Select(p => $"{where}: {p}"));
							problems.AddRange(FitCheck.CutText(list.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "PadChoiceRow")).Select(p => $"{where}: {p}"));
							// The card's lines above the list (title, filter, search) are whole.
							foreach (Control line in ((Grid)list.Parent!).Children.Where(c => c != list && c.IsEffectivelyVisible))
							{
								problems.AddRange(FitCheck.CutText(line).Where(p => !p.Contains(vm.SupportedGames.ListLabel)).Select(p => $"{where}: {p}"));
							}
							var rows = list.GetVisualDescendants().OfType<ComboBox>().Where(b => b.Classes.Contains("rowPicker") && b.DataContext is GameItemViewModel)
								.GroupBy(b => b.DataContext).ToList();
							Assert.True(rows.Count >= 3, where);
							PickersShareTheirColumns(rows, window);
						}
						// The list's options line under the rows is whole.
						problems.AddRange(FitCheck.CutText(view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ListOptions")).Select(p => $"{where}: {p}"));

						if (outDir != null)
						{
							using var frame = window.CaptureRenderedFrame();
							Directory.CreateDirectory(outDir);
							frame!.Save(Path.Combine(outDir, $"games-fit-{page}-{width}x{height}-{code}.png"));
						}
						window.Close();
					}
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
		Assert.Empty(problems);
	}

	// A row's pickers in order (controller type, profile): their bounds in every row.
	private static List<Rect> PickerEdges(IEnumerable<IGrouping<object?, ComboBox>> rows, int index, Visual root) =>
		rows.Select(r => Bounds(r.ElementAt(index), root)).ToList();

	// Each picker keeps one right edge and one width from row to row (side by side or one above the other), and both
	// pickers are as wide.
	private static void PickersShareTheirColumns(List<IGrouping<object?, ComboBox>> rows, Visual root)
	{
		foreach (int index in new[] { 0, 1 })
		{
			List<Rect> edges = PickerEdges(rows, index, root);
			Assert.Single(edges.Select(e => Math.Round(e.Right)).Distinct());
			Assert.Single(edges.Select(e => Math.Round(e.Width)).Distinct());
		}
		Assert.Equal(Math.Round(PickerEdges(rows, 0, root)[0].Width), Math.Round(PickerEdges(rows, 1, root)[0].Width));
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
