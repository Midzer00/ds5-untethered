// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Games;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// Pictures on the supported games list. Only generated pictures are used (flat shapes), never real game artwork.
public sealed class GameImagesAppTests : IDisposable
{
	private readonly string folder = Path.Combine(Path.GetTempPath(), "kopru-images-app-" + Guid.NewGuid().ToString("N"));
	private readonly TimeSpan settleBefore = GameThumbnail.SettleDelay;

	public GameImagesAppTests()
	{
		Directory.CreateDirectory(folder);
		GameThumbnail.SettleDelay = TimeSpan.Zero;
	}

	public void Dispose()
	{
		GameThumbnail.SettleDelay = settleBefore;
		try
		{
			Directory.Delete(folder, recursive: true);
		}
		catch (IOException)
		{
			// A decoder may still hold a file for a moment.
		}
	}

	// A made-up 460x215 "header": a flat colour field with two simple shapes.
	private string GeneratedHeader(string name, Color back, Color shape)
	{
		string path = Path.Combine(folder, name + ".png");
		using var target = new RenderTargetBitmap(new PixelSize(460, 215));
		using (DrawingContext context = target.CreateDrawingContext())
		{
			context.FillRectangle(new SolidColorBrush(back), new Rect(0, 0, 460, 215));
			context.DrawEllipse(new SolidColorBrush(shape), null, new Point(330, 108), 120, 120);
			context.FillRectangle(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), new Rect(40, 150, 150, 18), 4);
		}
		target.Save(path);
		return path;
	}

	// A made-up 48x48 icon: a rounded square.
	private static IconPixels GeneratedIcon()
	{
		const int size = 48;
		byte[] bgra = new byte[size * size * 4];
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				bool inside = x is >= 6 and < 42 && y is >= 6 and < 42 && !((x < 10 || x >= 38) && (y < 10 || y >= 38));
				int i = (y * size + x) * 4;
				(bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = inside ? ((byte)0xC8, (byte)0x8A, (byte)0x3A, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0);
			}
		}
		return new IconPixels(size, size, bgra);
	}

	private static List<GameThumbnail> Thumbnails(Window window) => window.GetVisualDescendants().OfType<GameThumbnail>().Where(t => t.IsEffectivelyVisible).ToList();

	// Runs the UI jobs until every visible picture has loaded.
	private static void WaitForPictures(Window window)
	{
		for (int i = 0; i < 200; i++)
		{
			Dispatcher.UIThread.RunJobs();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			List<GameThumbnail> shown = Thumbnails(window);
			if (shown.Count > 0 && shown.All(t => t.Shown != null))
			{
				return;
			}
			Thread.Sleep(15);
		}
		Assert.Fail("pictures did not load");
	}

	private static (MainWindowViewModel Vm, MainWindow Window) Open(FakeControllerService service, GameImageService images, AppSettings? settings = null,
		double height = 700)
	{
		var vm = new MainWindowViewModel(service, new AppEnvironment(new MemorySettingsStore(settings ?? new AppSettings()), GameImages: images,
			GameList: new GameCatalogUpdater(new HttpClient(), Path.Combine(Path.GetTempPath(), "kopru-render-games.json"), "https://example.invalid/list.json")));
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = height };
		window.Show();
		vm.SelectedNav = vm.NavItems.Single(n => n.Page == vm.SupportedGames);
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
		return (vm, window);
	}

	// Renders the page with generated pictures (docs/screens/supported-games-pictures-{tr,en}.png when KOPRU_RENDER_SCREENS=1):
	// three from "Steam's library cache", one game without a picture (placeholder) and an added game with its exe icon.
	[AvaloniaFact]
	public void RenderSupportedGamesWithPictures()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		var pictures = new Dictionary<long, string>
		{
			[1091500] = GeneratedHeader("a", Color.Parse("#2B3A55"), Color.Parse("#E0B44C")),
			[805550] = GeneratedHeader("b", Color.Parse("#3D2B2B"), Color.Parse("#C9573F")),
			[1649240] = GeneratedHeader("c", Color.Parse("#1E3B3A"), Color.Parse("#5FB3A6"))
		};
		foreach ((Language language, string code) in new[] { (Language.Turkish, "tr"), (Language.English, "en") })
		{
			Language? before = Localizer.Chosen;
			try
			{
				Localizer.Chosen = language;
				var resolver = new GameImageResolver(id => pictures.GetValueOrDefault(id), null, null, () => true, () => false, exeExists: _ => true);
				var images = new GameImageService(resolver, _ => GeneratedIcon());
				(MainWindowViewModel vm, MainWindow window) = Open(new FakeControllerService(animate: false), images, new AppSettings
				{
					GameListCheckedAt = DateTimeOffset.Now,
					Language = code,
					AlwaysGames = ["Returnal"],
					UserGames = [new UserGame("Indie Game", @"C:\Games\Indie\Indie.exe")]
				}, height: 760); // tall enough that the fourth row (above the list: the controller type for every game) is in view
				WaitForPictures(window);
				Dictionary<string, GameImageSource> sources = Thumbnails(window).ToDictionary(t => ((GameItemViewModel)t.DataContext!).Name, t => t.Shown!.Source);
				Assert.Equal(GameImageSource.SteamLibrary, sources["Cyberpunk 2077"]);
				Assert.Equal(GameImageSource.Placeholder, sources["Marvel's Spider-Man Remastered"]);
				Assert.Equal(GameImageSource.ExeIcon, sources["Indie Game"]);
				// The download switch is on the page, on by default.
				CheckBox box = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "DownloadImagesBox");
				Assert.True(box.IsChecked);
				Assert.True(box.IsEffectivelyVisible);

				AvaloniaHeadlessPlatform.ForceRenderTimerTick();
				using var frame = window.CaptureRenderedFrame();
				Assert.NotNull(frame);
				if (writeFiles)
				{
					string outDir = Path.Combine(RepoRoot(), "docs", "screens");
					Directory.CreateDirectory(outDir);
					frame!.Save(Path.Combine(outDir, $"supported-games-pictures-{code}.png"));
				}
				window.Close();
			}
			finally
			{
				Localizer.Chosen = before;
			}
		}
	}

	// The long list asks only for the pictures of the rows in view.
	[AvaloniaFact]
	public void OnlyRowsInView_AskForPictures()
	{
		int asked = 0;
		var resolver = new GameImageResolver(_ => { Interlocked.Increment(ref asked); return null; }, null, null, () => true, () => false);
		var images = new GameImageService(resolver);
		var service = new FakeControllerService(animate: false);
		service.Games.SetCatalog(GameCatalog.LoadEmbedded());
		(MainWindowViewModel vm, MainWindow window) = Open(service, images);
		WaitForPictures(window);
		ItemsControl list = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.ItemCount == vm.SupportedGames.Games.Count);
		int realized = list.GetRealizedContainers().Count();
		Assert.True(list.ItemCount > 200);
		Assert.InRange(images.Loads, 1, realized);
		Assert.InRange(asked, 1, realized);
		window.Close();
	}

	// A row that scrolls away before its picture starts loading asks for nothing.
	[AvaloniaFact]
	public void RowScrolledAway_BeforeLoading_AsksForNothing()
	{
		GameThumbnail.SettleDelay = TimeSpan.FromMilliseconds(250);
		int asked = 0;
		var resolver = new GameImageResolver(_ => { Interlocked.Increment(ref asked); return null; }, null, null, () => true, () => false);
		var images = new GameImageService(resolver);
		var thumb = new GameThumbnail { Width = 112, Height = 52, Source = new GameImageHandle(images, new GameImageRequest("k", 1)) };
		var host = new StackPanel();
		var window = new Window { Content = host, Width = 300, Height = 200 };
		window.Show();
		host.Children.Add(thumb);
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
		host.Children.Remove(thumb);
		Thread.Sleep(400);
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(0, images.Loads);
		Assert.Equal(0, asked);
		Assert.Null(thumb.Shown);
		window.Close();
	}

	// The setting is saved (on by default) and turning it off loads the rows again without downloaded pictures.
	[AvaloniaFact]
	public void Setting_DefaultsOn_IsSaved_AndReloadsTheRows()
	{
		Assert.True(new AppSettings().DownloadGameImages);
		var store = new MemorySettingsStore(new AppSettings());
		var resolver = new GameImageResolver(_ => null, null, null, () => true, () => false);
		var images = new GameImageService(resolver);
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(store, GameImages: images));
		Assert.True(vm.Settings.DownloadGameImages);
		GameItemViewModel first = vm.SupportedGames.Games[0];
		Assert.NotNull(first.Image);

		vm.Settings.DownloadGameImages = false;
		Assert.NotSame(first, vm.SupportedGames.Games[0]);
		Assert.False(vm.Settings.ToAppSettings().DownloadGameImages);
	}

	// An added game's exe icon becomes a small bitmap drawn in the middle of the tile; a missing file falls back to the placeholder.
	[AvaloniaFact]
	public async Task ExeIcon_AndPlaceholder()
	{
		var resolver = new GameImageResolver(_ => null, null, null, () => true, () => false, exeExists: p => p.EndsWith("game.exe", StringComparison.Ordinal));
		var images = new GameImageService(resolver, _ => GeneratedIcon());
		GameThumb icon = await images.LoadAsync(new GameImageRequest("k", null, @"C:\Games\game.exe"), 112, default);
		Assert.True(icon.IsIcon);
		Assert.Equal(GameImageSource.ExeIcon, icon.Source);
		Assert.Equal(new PixelSize(48, 48), icon.Image!.PixelSize);
		GameThumb none = await images.LoadAsync(new GameImageRequest("m", null, @"C:\Games\gone.exe"), 112, default);
		Assert.Same(GameThumb.Placeholder, none);
		// Kept in memory: asked again, nothing is resolved again.
		Assert.Same(icon, images.Cached(new GameImageRequest("k", null, @"C:\Games\game.exe"), 112));
	}

	// A header picture is decoded at the size it is shown.
	[AvaloniaFact]
	public async Task Header_IsDecodedAtTheShownWidth()
	{
		string header = GeneratedHeader("d", Colors.SlateGray, Colors.Orange);
		var resolver = new GameImageResolver(_ => header, null, null, () => true, () => false);
		GameThumb thumb = await new GameImageService(resolver).LoadAsync(new GameImageRequest("k", 5), 168, default);
		Assert.Equal(GameImageSource.SteamLibrary, thumb.Source);
		Assert.Equal(168, thumb.Image!.PixelSize.Width);
	}

	private static string RepoRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (dir != null && !File.Exists(Path.Combine(dir, "Kopru.sln")))
		{
			dir = Path.GetDirectoryName(dir);
		}
		return dir ?? throw new InvalidOperationException("Kopru.sln not found");
	}
}
