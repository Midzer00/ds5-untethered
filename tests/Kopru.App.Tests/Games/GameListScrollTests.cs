// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Games;
using Kopru.Core.Localization;
using Xunit.Abstractions;

namespace Kopru.App.Tests;

// The Supported Games list with a long made-up list: opening and changing a row's pickers keeps the list where it
// is, and the list shows enough rows at every window size. Writes games-{supported-all|supported-installed|other}-{size}-tr.png
// and games-rows.txt to KOPRU_PAGE_RENDER_DIR when it is set.
public sealed class GameListScrollTests(ITestOutputHelper output)
{
	// Window sizes and the fewest fully visible rows there: Supported Games "All" and "Installed" (the "Installed" view has a
	// line under the list). Before the compact rows: 2/2, 2/2, 3/2 and 4/4.
	public static readonly (double Width, double Height, int All, int Installed)[] RowSizes =
		[(960, 640, 5, 4), (1100, 700, 4, 4), (1920, 1080, 6, 6), (2560, 1440, 8, 7)];

	internal static GameCatalog LongCatalog { get; } = new(Enumerable.Range(1, 60)
		.Select(i => new CatalogGame($"Test Game {i:00}", [$"TestGame{i:00}.exe"], null, i % 2 == 0, i % 3 != 0)));

	// The long list; withRunningGame: the sample catalog too (Cyberpunk 2077, the fake's game in front, runs; Returnal is found installed).
	// unsupported: the "Desteklenmeyen" view.
	internal static MainWindowViewModel Open(bool installedOnly, string language = "tr", bool withRunningGame = false, bool unsupported = false)
	{
		var service = new FakeControllerService(animate: false);
		service.Games.SetCatalog(withRunningGame ? new GameCatalog(LongCatalog.Games.Concat(FakeControllerService.SampleCatalog.Games)) : LongCatalog);
		var vm = new MainWindowViewModel(service, new AppEnvironment(new MemorySettingsStore(new AppSettings
		{
			GameListCheckedAt = DateTimeOffset.Now,
			Language = language,
			InstalledGamesOnly = installedOnly,
			UserGames = Enumerable.Range(1, 24).Select(i => new UserGame($"My Game {i:00}", $@"C:\Games\My{i:00}\My{i:00}.exe")).ToList()
		}), GameList: new GameCatalogUpdater(new HttpClient(), Path.Combine(Path.GetTempPath(), "kopru-scroll-games.json"), "https://example.invalid/list.json"),
			InstalledGames: FakeFoundGames.Installed(FakeFoundGames.Folders())));
		vm.SupportedGames.ShowUnsupported = unsupported;
		return vm;
	}

	// The list scrolled down: a row's controller type and then its profile picker are opened with a click and changed; the
	// list stays where it was and the row stays in place.
	[AvaloniaFact]
	public async Task OpeningAndChangingARowPicker_KeepsTheScrollPosition()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.Turkish;
			MainWindowViewModel vm = Open(installedOnly: false);
			await vm.SupportedGames.ScanTask;
			vm.Settings.Profiles.Create("Yarış", vm.Settings.Profiles.Default.Values);
			var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
			window.Show();
			vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
			Settle();

			ScrollViewer list = GameList(window);
			Assert.True(list.Extent.Height > list.Viewport.Height * 3, $"extent {list.Extent} viewport {list.Viewport}");
			list.Offset = new Vector(0, 900);
			Settle();
			double offset = list.Offset.Y;
			Assert.True(offset > 800, $"offset {offset}");

			GameItemViewModel? changed = null;
			foreach (int picker in new[] { 0, 1 })
			{
				// The first row fully in view: its picker (controller type, then profile).
				ComboBox box = window.GetVisualDescendants().OfType<ComboBox>()
					.Where(b => b.Classes.Contains("rowPicker") && b.DataContext is GameItemViewModel && b.IsEffectivelyVisible)
					.GroupBy(b => b.DataContext)
					.Select(g => g.ToList())
					.First(r => RowInside(r[0], list))[picker];
				var row = (GameItemViewModel)box.DataContext!;
				changed ??= row;
				Assert.Same(changed, row);
				double rowTop = Bounds(box, window).Top;

				// A real click opens it; the list does not move.
				Point center = Bounds(box, window).Center;
				window.MouseDown(center, MouseButton.Left);
				window.MouseUp(center, MouseButton.Left);
				Settle();
				Assert.True(box.IsDropDownOpen, $"picker {picker} did not open");
				Assert.Equal(offset, list.Offset.Y, 1);

				// Picking another value (Xbox 360; the second profile) and closing: still there, and the row too.
				box.SelectedIndex = picker == 0 ? 3 : 1;
				box.IsDropDownOpen = false;
				Settle();
				Assert.Equal(offset, list.Offset.Y, 1);
				ComboBox same = window.GetVisualDescendants().OfType<ComboBox>()
					.Where(b => b.Classes.Contains("rowPicker") && ReferenceEquals(b.DataContext, row)).ElementAt(picker);
				Assert.Equal(rowTop, Bounds(same, window).Top, 1);
			}
			Assert.Equal(Kopru.Core.Virtual.VirtualPadType.Xbox360, changed!.PadType!.Selected!.Type);
			Assert.True(changed.Profile!.HasProfile);
			Assert.Equal(vm.Settings.Profiles.Profiles[1].Id, vm.Settings.Profiles.ProfileForGame(changed.Key));
			window.Close();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	[AvaloniaFact]
	public async Task GameLists_ShowEnoughRows_AtEverySize()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_PAGE_RENDER_DIR");
		Language? before = Localizer.Chosen;
		List<string> report = [];
		try
		{
			Localizer.Chosen = Language.Turkish;
			foreach ((double width, double height, int minAll, int minInstalled) in RowSizes)
			{
				// "Installed" lists the catalog games found here (few in this made-up list); "Desteklenmeyen" has the same lines
				// around the rows (the hint above, the line under them) and many games.
				foreach (string page in new[] { "supported-all", "unsupported", "other" })
				{
					MainWindowViewModel vm = Open(installedOnly: page == "supported-installed", unsupported: page == "unsupported");
					await vm.SupportedGames.ScanTask;
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.SelectedNav = vm.NavItems.Single(n => n.Page == (page == "other" ? (PageViewModel)vm.OtherGames : vm.SupportedGames));
					Settle();
					window.UpdateLayout();

					int rows = VisibleRows(window);
					report.Add($"{width}x{height} {page}: {rows} rows");
					output.WriteLine(report[^1]);
					int min = page switch { "supported-all" => minAll, "unsupported" => minInstalled, _ => 0 };
					Assert.True(rows >= min, $"{report[^1]}, at least {min} expected");

					using var frame = window.CaptureRenderedFrame();
					Assert.NotNull(frame);
					if (!string.IsNullOrEmpty(outDir))
					{
						Directory.CreateDirectory(outDir);
						frame!.Save(Path.Combine(outDir, $"games-{page}-{width}x{height}-tr.png"));
					}
					window.Close();
				}
			}
			if (!string.IsNullOrEmpty(outDir))
			{
				File.WriteAllLines(Path.Combine(outDir, "games-rows.txt"), report);
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The view's name follows the main switch: the supported list (All or Installed), or the unsupported games: the found
	// and added games the list does not know.
	[AvaloniaFact]
	public async Task ListTitle_FollowsTheView()
	{
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			MainWindowViewModel vm = Open(installedOnly: false, "en");
			await vm.SupportedGames.ScanTask;
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(Text.SupportedGameList(), vm.SupportedGames.ListLabel);
			var changed = new List<string?>();
			vm.SupportedGames.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
			vm.Settings.InstalledGamesOnly = true;
			Assert.Equal(Text.SupportedGameList(), vm.SupportedGames.ListLabel);
			Assert.DoesNotContain(vm.SupportedGames.Games, g => g.IsFound || g.IsUnsupported);
			vm.SupportedGames.ViewIndex = 2;
			Assert.True(vm.SupportedGames.ShowUnsupported);
			Assert.Equal(Text.UnsupportedGamesTitle(), vm.SupportedGames.ListLabel);
			Assert.Contains(nameof(SupportedGamesViewModel.ListLabel), changed);
			Assert.Contains(vm.SupportedGames.Games, g => g.IsFound);
			Assert.All(vm.SupportedGames.Games, g => Assert.True(g.IsUnsupported));
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The game rows fully inside the list's view (0 without a list).
	internal static int VisibleRows(Window window)
	{
		if (window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(IsGameList) is not { } list)
		{
			return 0;
		}
		return window.GetVisualDescendants().OfType<ComboBox>()
			.Where(b => b.Classes.Contains("rowPicker") && b.DataContext is GameItemViewModel && b.IsEffectivelyVisible)
			.GroupBy(b => b.DataContext)
			.Count(g => RowInside(g.First(), list));
	}

	private static bool IsGameList(ScrollViewer s) => s.IsEffectivelyVisible && s.GetVisualDescendants().OfType<ItemsPresenter>().Any()
		&& s.GetVisualDescendants().Any(v => v is Control { DataContext: GameItemViewModel });

	internal static ScrollViewer GameList(Window window) => window.GetVisualDescendants().OfType<ScrollViewer>().First(IsGameList);

	// The whole row (the picker's row container) inside the list's viewport.
	private static bool RowInside(Control picker, ScrollViewer list)
	{
		Control row = picker.GetVisualAncestors().OfType<ContentPresenter>().First(p => p.DataContext is GameItemViewModel);
		return Inside(row, list);
	}

	private static bool Inside(Control control, ScrollViewer list)
	{
		Rect r = Bounds(control, list);
		return r.Top >= -0.5 && r.Bottom <= list.Viewport.Height + 0.5;
	}

	private static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Thread.Sleep(320);
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Dispatcher.UIThread.RunJobs();
	}

	private static Rect Bounds(Visual control, Visual root) =>
		control.TransformToVisual(root) is { } m ? new Rect(control.Bounds.Size).TransformToAABB(m) : default;
}
