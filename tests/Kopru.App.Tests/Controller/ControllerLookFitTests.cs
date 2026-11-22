// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The Controller page with its colour tile (a variant chosen) still fits its Test tab in one window, and the open look
// picker fits the page: at the window's minimum and the two review sizes, in both languages. With KOPRU_LOOK_RENDER_DIR
// set, the review pictures are saved there (1152x700, tr and en): the Controller page with a variant, the picker open, the
// Overview and Light pages with a variant (light lit), and the Controller page with an own image; and the Controller
// page at each review size with the default look, a variant, the longest name and an Edge.
[Collection("ControllerLook")]
public sealed class ControllerLookFitTests : IDisposable
{
	private static readonly (double Width, double Height)[] Sizes = [(1100, 700), (1152, 700), (960, 640)];

	private readonly string _folder = Path.Combine(Path.GetTempPath(), "kopru-look-fit-" + Guid.NewGuid().ToString("N")[..8]);

	public void Dispose()
	{
		ControllerLookTests.WaitForThumbnails();
		try
		{
			Directory.Delete(_folder, recursive: true);
		}
		catch (DirectoryNotFoundException)
		{
		}
	}

	private MainWindowViewModel Start(string code, out FakeControllerService fake, bool edge = false)
	{
		fake = new FakeControllerService(animate: false);
		fake.ControllerList.Add(ControllerLookTests.Entry("pad-a", edge: edge));
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = code })));
		vm.Settings.OwnImages = new OwnImageStore(Path.Combine(_folder, "looks-" + code));
		vm.Overview.ToggleBridgeCommand.Execute(null);
		fake.Tick();
		fake.RaiseStateChanged();
		ControllerLookTests.Settle();
		return vm;
	}

	[AvaloniaFact]
	public async Task ColourTileAndPicker_Fit_AtTheReviewSizes_InBothLanguages()
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				MainWindowViewModel vm = Start(code, out FakeControllerService fake);
				// The longest variant name shows in the tile.
				PadVariant longest = PadPack.Variants.MaxBy(v => v.Name.Length)!;
				Assert.True(vm.Settings.SetLook("pad-a", longest.Id));
				foreach ((double width, double height) in Sizes)
				{
					string at = $"{code} {width}x{height}";
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.OpenController(ControllerPageViewModel.TestTab);
					fake.LiveHandler!(TesterTests.Sample);
					vm.Tester.Tick();
					FitCheck.Settle(window);
					problems.AddRange(ControllerTestFitTests.Check(window).Select(p => $"{at}: {p}"));

					ControllerView page = window.GetVisualDescendants().OfType<ControllerView>().Single(v => v.IsEffectivelyVisible);
					problems.AddRange(TileProblems(page, vm, longest.Name).Select(p => $"{at}: {p}"));

					// The picker: over the page, the dialog inside it, its tiles inside the dialog's scrolling area, Done in view.
					vm.Controller.Look.OpenCommand.Execute(null);
					await vm.Controller.Look.ThumbnailsReady;
					FitCheck.Settle(window);
					problems.AddRange(PickerProblems(page).Select(p => $"{at} picker: {p}"));
					// The card keeps its place and size when the own image (and its remove X) comes.
					Rect card = FitCheck.Bounds(Dialog(page), page);
					Assert.True(ControllerLookTests.Import(vm.Settings, "pad-a", ControllerLookTests.MakePicture(_folder, $"own-{code}-{width}.png", 900, 600)));
					await vm.Controller.Look.ThumbnailsReady;
					FitCheck.Settle(window);
					if (FitCheck.Bounds(Dialog(page), page) != card)
					{
						problems.Add($"{at} picker: the card moved from {card} to {FitCheck.Bounds(Dialog(page), page)} with an own image");
					}
					problems.AddRange(PickerProblems(page).Select(p => $"{at} picker with an own image: {p}"));
					Assert.True(vm.Settings.RemoveOwnImage("pad-a"));
					Assert.True(vm.Settings.SetLook("pad-a", longest.Id));
					vm.Controller.Look.CloseCommand.Execute(null);
					FitCheck.Settle(window);
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

	// The colour tile: right of the header and the tabs, inside the page column, no taller than the two together (the page
	// below starts where it did), its title whole and its name (trimmed at worst) a useful width with the whole name in its
	// tooltip; nothing beside it is cut.
	internal static IEnumerable<string> TileProblems(ControllerView page, MainWindowViewModel vm, string name)
	{
		StackPanel column = page.GetVisualDescendants().OfType<StackPanel>().First(p => p.Classes.Contains("page"));
		Button tile = page.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LookTileButton");
		ListBox tabs = page.GetVisualDescendants().OfType<ListBox>().Single(l => ReferenceEquals(l.ItemsSource, vm.Controller.Tabs));
		Grid header = page.GetVisualDescendants().OfType<Grid>().Single(g => g.Classes.Contains("pageHeader"));
		Button reset = page.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ResetButton");
		Rect tileRect = FitCheck.Bounds(tile, column), tabsRect = FitCheck.Bounds(tabs, column), headerRect = FitCheck.Bounds(header, column),
			resetRect = FitCheck.Bounds(reset, column), columnRect = FitCheck.Bounds(column, column);
		if (!tile.IsEffectivelyVisible)
		{
			yield return "the colour tile is not shown";
			yield break;
		}
		if (tileRect.Right > columnRect.Right + 0.5 || tileRect.Left < tabsRect.Right + 8 || tileRect.Left < headerRect.Right + 8
			|| (reset.IsEffectivelyVisible && tileRect.Left < resetRect.Right + 20))
		{
			yield return $"colour tile {tileRect} beside the header {headerRect}, the tabs {tabsRect} and reset {resetRect} in the column {columnRect}";
		}
		if (tileRect.Top < headerRect.Top - 0.5 || tileRect.Bottom > tabsRect.Bottom + 0.5 || tileRect.Height < 80
			|| tile.DesiredSize.Height > tileRect.Height + 0.5)
		{
			yield return $"colour tile {tileRect} not as tall as the header {headerRect} and the tabs {tabsRect}";
		}
		if (reset.IsEffectivelyVisible && (resetRect.Left < tabsRect.Right + 8 || Math.Abs(resetRect.Center.Y - tabsRect.Center.Y) > 1))
		{
			yield return $"reset {resetRect} not on the tabs' line {tabsRect}";
		}
		Image icon = tile.GetVisualDescendants().OfType<Image>().Single(i => i.Name == "LookIcon");
		Rect badge = FitCheck.Bounds(icon, column);
		if (icon.Source is not Bitmap { PixelSize.Width: >= 120 } || badge.Width < 59.5 || badge.Height < 39.5 || badge.Left < tileRect.Left
			|| badge.Right > tileRect.Right || badge.Top < tileRect.Top)
		{
			yield return $"the DualSense {badge} ({icon.Source?.GetType().Name}) in the tile {tileRect}";
		}
		TextBlock title = tile.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "LookTileTitle");
		TextBlock current = tile.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "LookName");
		foreach (TextBlock text in new[] { title, current })
		{
			Rect r = FitCheck.Bounds(text, column);
			if (r.Top < badge.Bottom - 0.5 || r.Bottom > tileRect.Bottom + 0.5 || r.Left < tileRect.Left || r.Right > tileRect.Right)
			{
				yield return $"\"{text.Text}\" {r} not under the DualSense {badge} in the tile {tileRect}";
			}
		}
		foreach (string cut in FitCheck.CutText(tile).Where(p => !p.Contains(current.Text!, StringComparison.Ordinal)))
		{
			yield return $"tile: {cut}";
		}
		bool trimmed = current.TextLayout.TextLines.Any(l => l.HasCollapsed);
		if (current.Text != name || current.Bounds.Width > 115.5 || (trimmed && current.Bounds.Width < 100) || ToolTip.GetTip(current) as string != name)
		{
			yield return $"the look's name \"{current.Text}\" is {current.Bounds.Width:F0} wide, tooltip {ToolTip.GetTip(current)}";
		}
		foreach (string cut in FitCheck.CutText(tabs).Concat(FitCheck.CutText(header)).Concat(FitCheck.CutText(reset)))
		{
			yield return cut;
		}
	}

	private static Border Dialog(ControllerView page) => page.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "LookDialog");

	// The scrolling tiles (the pack); the own image's tile is in the footer.
	private static List<Button> PackTiles(ScrollViewer scroll) => [.. scroll.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("lookTile"))];

	internal static IEnumerable<string> PickerProblems(ControllerView page)
	{
		Border dialog = Dialog(page);
		ScrollViewer scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "LookScroll");
		Button done = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LookDoneButton");
		Border footer = dialog.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "LookFooter");
		ContentControl own = dialog.GetVisualDescendants().OfType<ContentControl>().Single(b => b.Name == "LookOwnTile");
		Rect pageRect = FitCheck.Bounds(page, page), dialogRect = FitCheck.Bounds(dialog, page);
		if (!dialog.IsEffectivelyVisible)
		{
			yield return "not shown";
			yield break;
		}
		if (dialogRect.Left < pageRect.Left || dialogRect.Top < pageRect.Top || dialogRect.Right > pageRect.Right + 0.5 || dialogRect.Bottom > pageRect.Bottom + 0.5)
		{
			yield return $"dialog {dialogRect} outside the page {pageRect}";
		}
		if (dialogRect.Height > 600.5 || dialogRect.Width > 780.5)
		{
			yield return $"dialog {dialogRect} larger than 780x600";
		}
		// The footer (own image tile, Done) and the scrolling area are whole inside the card.
		foreach (Control c in new Control[] { done, scroll, footer, own })
		{
			Rect r = FitCheck.Bounds(c, page);
			if (!c.IsEffectivelyVisible || r.Height < 1 || r.Left < dialogRect.Left || r.Right > dialogRect.Right + 0.5 || r.Top < dialogRect.Top
				|| r.Bottom > dialogRect.Bottom + 0.5)
			{
				yield return $"{c.Name} {r} outside the dialog {dialogRect}";
			}
		}
		Rect footerRect = FitCheck.Bounds(footer, page);
		foreach (Control c in new Control[] { done, own })
		{
			Rect r = FitCheck.Bounds(c, page);
			if (r.Top < footerRect.Top - 0.5 || r.Bottom > footerRect.Bottom + 0.5)
			{
				yield return $"{c.Name} {r} outside the footer {footerRect}";
			}
		}
		if (FitCheck.Bounds(scroll, page).Bottom > footerRect.Top + 0.5)
		{
			yield return "the tiles run into the footer";
		}
		// The tiles are 96 wide, about seven to a row, within the scrolling area's width (it scrolls only down); the first
		// row is in view.
		Rect viewport = FitCheck.Bounds(scroll, page);
		List<Button> tiles = PackTiles(scroll);
		if (tiles.Count == 0)
		{
			yield return "no tiles";
			yield break;
		}
		foreach (Button tile in tiles)
		{
			Rect r = FitCheck.Bounds(tile, page);
			if (r.Left < viewport.Left - 0.5 || r.Right > viewport.Right + 0.5 || Math.Abs(r.Width - 96) > 0.5)
			{
				yield return $"tile {(tile.DataContext as PadLookTile)?.Name} {r} not within the scroll area {viewport}";
			}
		}
		Rect first = FitCheck.Bounds(tiles[0], page);
		if (!tiles.Any(t => FitCheck.Bounds(t, page).Intersects(viewport)))
		{
			yield return "no tile in view";
		}
		int perRow = tiles.Count(t => Math.Abs(FitCheck.Bounds(t, page).Top - first.Top) < 0.5);
		if (tiles.Count >= 7 && perRow < 7)
		{
			yield return $"{perRow} tiles a row, not 7";
		}
		if (scroll.Extent.Width > scroll.Viewport.Width + 0.5)
		{
			yield return $"scrolls sideways: {scroll.Extent.Width} > {scroll.Viewport.Width}";
		}
		// No text is cut (a tile's name may take two lines).
		foreach (string cut in FitCheck.CutText(dialog))
		{
			yield return cut;
		}
	}

	// A pack as large as the real one (19 standard, 27 special) from a 46-entry manifest, its pictures the placeholder
	// variants' under other ids; the names vary in length up to what two lines of a tile hold.
	internal static IReadOnlyList<PadVariant> ManyVariants()
	{
		IReadOnlyList<PadVariant> real = PadPack.Variants;
		string[] en = ["Colour", "Deep Colour", "Bright Test Colour", "Soft Matte Colour", "Two Tone Colour"];
		string[] tr = ["Renk", "Koyu Renk", "Parlak Deneme Rengi", "Yumuşak Mat Renk", "İki Tonlu Renk"];
		var entries = Enumerable.Range(1, 46).Select(i =>
			$"{{ 'id': 'many-{i:00}', 'group': '{(i <= 19 ? "standard" : "special")}', 'nameEn': '{en[i % en.Length]} {i}', 'nameTr': '{tr[i % tr.Length]} {i}', 'swatch': '#{i * 5:X2}4060' }}"
				.Replace('\x27', '\x22'));
		IReadOnlyList<PadVariant> parsed = PadPack.Parse("[" + string.Join(",\n", entries) + "]", _ => true);
		Assert.Equal(46, parsed.Count);
		Assert.Equal(27, parsed.Count(v => v.IsSpecial));
		return [.. parsed.Select((v, i) => v with { Folder = real[i % real.Count].Id })];
	}

	// The real pack's size: the card keeps its footer in view and the tiles scroll; a chosen tile far down is scrolled into
	// view when the picker opens.
	[AvaloniaFact]
	public async Task ManyVariants_ScrollInsideThePicker_FooterStaysInView()
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		using IDisposable pack = PadPack.Override(ManyVariants());
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				MainWindowViewModel vm = Start(code, out _);
				PadVariant far = PadPack.Variants[^2];
				Assert.True(vm.Settings.SetLook("pad-a", far.Id));
				foreach ((double width, double height) in Sizes)
				{
					string at = $"{code} {width}x{height}";
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.OpenController(ControllerPageViewModel.TestTab);
					FitCheck.Settle(window);
					ControllerView page = window.GetVisualDescendants().OfType<ControllerView>().Single(v => v.IsEffectivelyVisible);
					vm.Controller.Look.OpenCommand.Execute(null);
					await vm.Controller.Look.ThumbnailsReady;
					FitCheck.Settle(window);
					Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
					FitCheck.Settle(window);
					problems.AddRange(PickerProblems(page).Select(p => $"{at}: {p}"));

					ScrollViewer scroll = page.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "LookScroll");
					Rect viewport = FitCheck.Bounds(scroll, page);
					List<Button> tiles = PackTiles(scroll);
					Assert.Equal(47, tiles.Count);
					if (scroll.Extent.Height <= scroll.Viewport.Height + 0.5)
					{
						problems.Add($"{at}: does not scroll ({scroll.Extent.Height} in {scroll.Viewport.Height})");
					}
					Button chosen = tiles.Single(t => t.DataContext is PadLookTile { IsSelected: true });
					Rect chosenRect = FitCheck.Bounds(chosen, page);
					if (chosenRect.Top < viewport.Top - 0.5 || chosenRect.Bottom > viewport.Bottom + 0.5)
					{
						problems.Add($"{at}: the chosen tile {chosenRect} is not scrolled into view {viewport}");
					}
					// Scrolled to the top and to the bottom: the first and the last tile come into view, the footer stays.
					scroll.Offset = new Vector(0, 0);
					FitCheck.Settle(window);
					if (FitCheck.Bounds(tiles[0], page).Top < viewport.Top - 0.5)
					{
						problems.Add($"{at}: the first tile is not reachable");
					}
					scroll.Offset = new Vector(0, scroll.Extent.Height);
					FitCheck.Settle(window);
					Rect last = FitCheck.Bounds(tiles[^1], page);
					if (last.Bottom > viewport.Bottom + 0.5 || last.Top < viewport.Top - 0.5)
					{
						problems.Add($"{at}: the last tile {last} is not reachable in {viewport}");
					}
					problems.AddRange(PickerProblems(page).Select(p => $"{at} scrolled down: {p}"));
					vm.Controller.Look.CloseCommand.Execute(null);
					FitCheck.Settle(window);
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

	// An Edge (only Default and the own image to choose from) has the same tile; it fits and opens the picker. The tile is a
	// quiet button (its looks and cursor).
	[AvaloniaFact]
	public void Edge_ColourTile_FitsAndOpensThePicker()
	{
		List<string> problems = [];
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				MainWindowViewModel vm = Start(code, out FakeControllerService fake, edge: true);
				Assert.True(vm.Controller.Look.IsEdge);
				foreach ((double width, double height) in Sizes)
				{
					string at = $"{code} {width}x{height}";
					var window = new MainWindow { DataContext = vm, Width = width, Height = height };
					window.Show();
					vm.OpenController(ControllerPageViewModel.TestTab);
					fake.LiveHandler!(TesterTests.Sample);
					vm.Tester.Tick();
					FitCheck.Settle(window);
					problems.AddRange(ControllerTestFitTests.Check(window).Select(p => $"{at}: {p}"));
					ControllerView page = window.GetVisualDescendants().OfType<ControllerView>().Single(v => v.IsEffectivelyVisible);
					problems.AddRange(TileProblems(page, vm, Text.LookDefault()).Select(p => $"{at}: {p}"));
					Button tile = page.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LookTileButton");
					Assert.Equal(Text.LookTileAccessible(Text.LookDefault()), AutomationProperties.GetName(tile));
					// The quiet buttons' rest, hover, pressed and focus looks and their hand cursor.
					Assert.Same(Application.Current!.FindResource("QuietButton"), tile.Theme!.BasedOn);
					Assert.NotNull(tile.Cursor);
					Assert.True(tile.Focusable);
					tile.Command!.Execute(null);
					FitCheck.Settle(window);
					Assert.True(vm.Controller.Look.IsOpen);
					Assert.Equal(2, vm.Controller.Look.Tiles.Count());
					vm.Controller.Look.CloseCommand.Execute(null);
					FitCheck.Settle(window);
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

	// The picker by keyboard: the colour tile opens it with the chosen tile focused, arrows move between tiles, Enter
	// chooses, Esc closes and gives the keyboard back to the colour tile.
	[AvaloniaFact]
	public void Picker_IsKeyboardNavigable()
	{
		MainWindowViewModel vm = Start("en", out _);
		var window = new MainWindow { DataContext = vm, Width = 1152, Height = 700 };
		window.Show();
		vm.OpenController(ControllerPageViewModel.TestTab);
		FitCheck.Settle(window);
		ControllerLookViewModel look = vm.Controller.Look;
		Button colourTile = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LookTileButton" && b.IsEffectivelyVisible);
		Assert.True(colourTile.Focus(NavigationMethod.Tab));
		window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
		FitCheck.Settle(window);
		Assert.True(look.IsOpen);
		Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
		FitCheck.Settle(window);
		IInputElement? focused = window.FocusManager?.GetFocusedElement();
		Assert.True(focused is Button { DataContext: PadLookTile { Key: null } }, $"focused: {focused}");

		window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
		FitCheck.Settle(window);
		focused = window.FocusManager?.GetFocusedElement();
		PadLookTile? second = (focused as Button)?.DataContext as PadLookTile;
		Assert.Equal(look.Tiles.ElementAt(1), second);

		window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
		FitCheck.Settle(window);
		Assert.True(second!.IsSelected);
		Assert.Equal(PadLook.Variant(second.Key!), vm.Settings.ActiveLook);

		window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
		FitCheck.Settle(window);
		Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
		Assert.False(look.IsOpen);
		focused = window.FocusManager?.GetFocusedElement();
		Assert.True(focused is Button { Name: "LookTileButton" }, $"focused: {focused}");
		window.Close();
	}

	[AvaloniaFact]
	public async Task ReviewRenders()
	{
		string? dir = Environment.GetEnvironmentVariable("KOPRU_LOOK_RENDER_DIR");
		if (string.IsNullOrEmpty(dir))
		{
			return;
		}
		Directory.CreateDirectory(dir);
		Language? before = Localizer.Chosen;
		try
		{
			foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
			{
				Localizer.Chosen = language;
				MainWindowViewModel vm = Start(code, out FakeControllerService fake);
				PadVariant variant = PadPack.Variants.FirstOrDefault(v => v.Id == "metallic-blue") ?? PadPack.Variants[0];
				PadVariant red = PadPack.Variants.FirstOrDefault(v => v.Id == "metallic-red") ?? variant;
				PadVariant longest = PadPack.Variants.MaxBy(v => v.Name.Length)!;
				// The colour tile at each review size: the default look and a variant.
				foreach ((double width, double height) in Sizes)
				{
					var sized = new MainWindow { DataContext = vm, Width = width, Height = height };
					sized.Show();
					vm.OpenController(ControllerPageViewModel.TestTab);
					foreach ((string? look, string name) in new[] { ((string?)null, "default"), (red.Id, "red"), (longest.Id, "long") })
					{
						vm.Settings.SetLook("pad-a", look);
						fake.LiveHandler!(TesterTests.Sample);
						vm.Tester.Tick();
						Save(sized, Path.Combine(dir, $"tile-{name}-{width}x{height}-{code}.png"));
					}
					sized.Close();
				}
				MainWindowViewModel edgeVm = Start(code, out FakeControllerService edgeFake, edge: true);
				foreach ((double width, double height) in Sizes)
				{
					var sized = new MainWindow { DataContext = edgeVm, Width = width, Height = height };
					sized.Show();
					edgeVm.OpenController(ControllerPageViewModel.TestTab);
					edgeFake.LiveHandler!(TesterTests.Sample);
					edgeVm.Tester.Tick();
					Save(sized, Path.Combine(dir, $"tile-edge-{width}x{height}-{code}.png"));
					sized.Close();
				}

				Assert.True(vm.Settings.SetLook("pad-a", variant.Id));
				var window = new MainWindow { DataContext = vm, Width = 1152, Height = 700 };
				window.Show();

				vm.OpenController(ControllerPageViewModel.TestTab);
				fake.LiveHandler!(TesterTests.Sample);
				vm.Tester.Tick();
				Save(window, Path.Combine(dir, $"controller-variant-{code}.png"));

				vm.Controller.Look.OpenCommand.Execute(null);
				await vm.Controller.Look.ThumbnailsReady;
				Save(window, Path.Combine(dir, $"picker-open-{code}.png"));
				vm.Controller.Look.CloseCommand.Execute(null);

				// The light shows best on a dark body.
				PadVariant dark = PadPack.Variants.FirstOrDefault(v => v.Id == "jet-black") ?? variant;
				Assert.True(vm.Settings.SetLook("pad-a", dark.Id) || dark == variant);
				vm.Navigate(vm.Overview);
				Save(window, Path.Combine(dir, $"overview-variant-{code}.png"));
				vm.Navigate(vm.Light);
				Save(window, Path.Combine(dir, $"light-variant-{code}.png"));

				// An own image: a neutral generated picture (two flat colour blocks).
				string picture = ControllerLookTests.MakePicture(_folder, "own-" + code + ".png", 1200, 800);
				Assert.True(ControllerLookTests.Import(vm.Settings, "pad-a", picture));
				vm.OpenController(ControllerPageViewModel.TestTab);
				fake.LiveHandler!(TesterTests.Sample);
				vm.Tester.Tick();
				Save(window, Path.Combine(dir, $"controller-own-{code}.png"));
				vm.Controller.Look.OpenCommand.Execute(null);
				await vm.Controller.Look.ThumbnailsReady;
				Save(window, Path.Combine(dir, $"picker-own-{code}.png"));
				vm.Controller.Look.CloseCommand.Execute(null);

				// The real pack's size, a special edition far down chosen (scrolled into view).
				using (PadPack.Override(ManyVariants()))
				{
					Assert.True(vm.Settings.SetLook("pad-a", PadPack.Variants[^4].Id));
					vm.Controller.Look.OpenCommand.Execute(null);
					await vm.Controller.Look.ThumbnailsReady;
					FitCheck.Settle(window);
					Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
					Save(window, Path.Combine(dir, $"picker-many-{code}.png"));
					vm.Controller.Look.CloseCommand.Execute(null);
					FitCheck.Settle(window);
				}
				window.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static void Save(Window window, string path)
	{
		FitCheck.Settle(window);
		Thread.Sleep(320);
		FitCheck.Settle(window);
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		using var frame = window.CaptureRenderedFrame();
		frame!.Save(path);
	}
}
