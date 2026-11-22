// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Kopru.App.Controls;
using Kopru.App.Services;
using Kopru.App.ViewModels;
using Kopru.Core.Bridge;
using Kopru.Core.Input;

namespace Kopru.App.Tests;

// Not in parallel with other tests: the pack is static, and these tests put another one in its place for a while (an async
// test of another collection could otherwise run in between at an await and see it).
[CollectionDefinition("ControllerLook", DisableParallelization = true)]
public sealed class ControllerLookCollection;

// The controller's look: the built-in pack's manifest, the look per controller (the selected one's everywhere), the Edge
// without the pack, the own image (copied, downscaled, a missing or broken file falls back, removed), saved with the
// remembered controllers, and only the variant drawn is decoded.
[Collection("ControllerLook")]
public sealed class ControllerLookTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "kopru-look-tests-" + Guid.NewGuid().ToString("N")[..8]);

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

	internal static ControllerEntry Entry(string id, int slot = 1, bool edge = false) =>
		new(id, slot, edge ? "DualSense Edge" : "DualSense", edge, true, 80, false, BridgeState.Running);

	private (FakeControllerService Fake, MainWindowViewModel Vm) Start(params ControllerEntry[] controllers)
	{
		var fake = new FakeControllerService(animate: false);
		fake.ControllerList.AddRange(controllers);
		var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now })));
		vm.Settings.OwnImages = new OwnImageStore(Path.Combine(_folder, "looks"));
		fake.RaiseStateChanged();
		Settle();
		return (fake, vm);
	}

	// No picker of this test is still loading thumbnails when the next test starts: a load left running crosses into the
	// headless platform's change between tests (no asset loader for a moment) and must not decide what the next test sees.
	internal static void WaitForThumbnails()
	{
		var deadline = DateTime.UtcNow.AddSeconds(20);
		while (ControllerLookViewModel.Loading > 0 && DateTime.UtcNow < deadline)
		{
			Thread.Sleep(10);
		}
		Dispatcher.UIThread.RunJobs();
	}

	// Copies a picture in as a controller's own (the file is closed again).
	internal static bool Import(SettingsState settings, string id, string path)
	{
		using FileStream file = File.OpenRead(path);
		return settings.ImportOwnImage(id, file);
	}

	internal static void Settle()
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		Dispatcher.UIThread.RunJobs();
	}

	// A plain test picture (two flat colour blocks), saved as PNG.
	internal static string MakePicture(string folder, string name, int width, int height)
	{
		Directory.CreateDirectory(folder);
		var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
		using (ILockedFramebuffer buffer = bitmap.Lock())
		{
			var row = new byte[width * 4];
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					bool left = x < width / 2;
					row[x * 4] = left ? (byte)0xB0 : (byte)0x40;
					row[x * 4 + 1] = left ? (byte)0x80 : (byte)0x90;
					row[x * 4 + 2] = left ? (byte)0x50 : (byte)0x40;
					row[x * 4 + 3] = 255;
				}
				System.Runtime.InteropServices.Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
			}
		}
		string path = Path.Combine(folder, name);
		bitmap.Save(path);
		return path;
	}

	[Fact]
	public void Manifest_SkipsMalformedEntries_AndOnesWithoutPictures()
	{
		const string json = """
			[
			  { "id": "good-one", "group": "standard", "nameEn": "Good One", "nameTr": "İyi Bir", "swatch": "#112233" },
			  { "id": "special-one", "group": "special", "nameEn": "Special", "nameTr": "Özel", "swatch": "#AABBCC" },
			  { "id": "no-pictures", "group": "standard", "nameEn": "Gone", "nameTr": "Yok", "swatch": "#000000" },
			  { "id": "Bad Id", "group": "standard", "nameEn": "Bad", "nameTr": "Kötü", "swatch": "#000000" },
			  { "id": "bad-group", "group": "limited", "nameEn": "Bad", "nameTr": "Kötü", "swatch": "#000000" },
			  { "id": "bad-swatch", "group": "standard", "nameEn": "Bad", "nameTr": "Kötü", "swatch": "red" },
			  { "id": "no-name", "group": "standard", "nameTr": "Kötü", "swatch": "#000000" },
			  { "id": "good-one", "group": "special", "nameEn": "Again", "nameTr": "Yine", "swatch": "#000000" },
			  42,
			  { "id": 7, "group": "standard", "nameEn": "Number", "nameTr": "Sayı", "swatch": "#000000" }
			]
			""";
		IReadOnlyList<PadVariant> pack = PadPack.Parse(json, id => id != "no-pictures");
		Assert.Equal(["good-one", "special-one"], pack.Select(v => v.Id));
		Assert.False(pack[0].IsSpecial);
		Assert.True(pack[1].IsSpecial);
		Assert.Equal(Color.Parse("#112233"), pack[0].Swatch);
		Assert.Equal("İyi Bir", pack[0].NameTr);

		Assert.Empty(PadPack.Parse("not json", _ => true));
		Assert.Empty(PadPack.Parse("""{ "id": "x" }""", _ => true));
	}

	[AvaloniaFact]
	public void BuiltInPack_IsListed_AndItsPicturesAreHalfTheArt()
	{
		Assert.NotEmpty(PadPack.Variants);
		foreach (PadVariant variant in PadPack.Variants)
		{
			foreach (string part in PadPack.Parts)
			{
				using var bitmap = new Bitmap(AssetLoader.Open(PadPack.PartUri(variant.Id, part)));
				PixelSize expected = part == "body"
					? new PixelSize((int)ControllerLayout.ArtWidth / 2, (int)ControllerLayout.ArtHeight / 2)
					: new PixelSize((int)ControllerLayout.StickSpriteHalf, (int)ControllerLayout.StickSpriteHalf);
				Assert.Equal(expected, bitmap.PixelSize);
			}
			Assert.False(string.IsNullOrWhiteSpace(variant.NameEn));
			Assert.False(string.IsNullOrWhiteSpace(variant.NameTr));
		}
	}

	[AvaloniaFact]
	public void Look_IsPerController_AndTheSelectedOnesShowsEverywhere()
	{
		PadVariant variant = PadPack.Variants[0];
		(FakeControllerService fake, MainWindowViewModel vm) = Start(Entry("pad-a"), Entry("pad-b", 2));
		Assert.Equal(PadLook.Default, vm.Settings.ActiveLook);
		Assert.True(vm.Settings.SetLook("pad-a", variant.Id));
		Settle();
		Assert.Equal(PadLook.Variant(variant.Id), vm.Settings.ActiveLook);
		Assert.Equal(PadLook.Default, vm.Settings.LookOf("pad-b", false));

		List<string?> raised = [];
		vm.Settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
		fake.SelectController("pad-b");
		Settle();
		Assert.Equal(PadLook.Default, vm.Settings.ActiveLook);
		Assert.Contains(nameof(SettingsState.ActiveLook), raised);
		fake.SelectController("pad-a");
		Settle();
		Assert.Equal(PadLook.Variant(variant.Id), vm.Settings.ActiveLook);

		// The Controller page's row follows the selection too.
		Assert.Equal(variant.Name, vm.Controller.Look.CurrentName);
		fake.SelectController("pad-b");
		Settle();
		Assert.Equal(Kopru.Core.Localization.Text.LookDefault(), vm.Controller.Look.CurrentName);

		// Without a controller: the default art, and no row.
		fake.ControllerList.Clear();
		fake.RaiseStateChanged();
		Settle();
		Assert.Equal(PadLook.Default, vm.Settings.ActiveLook);
		Assert.False(vm.Controller.Look.IsAvailable);

		// A toast without its own look is drawn like the selected controller.
		fake.ControllerList.Add(Entry("pad-a"));
		fake.SelectController("pad-a");
		Settle();
		AppNotification? toast = null;
		vm.Notifications.Raised += n => toast = n;
		vm.Notifications.Post(new AppNotification(NotificationType.GameDetected, NotificationKind.Info, "t", "b", AppPage.Overview), force: true);
		Assert.Equal(PadLook.Variant(variant.Id), toast?.Look);
		Assert.Equal(PadLook.Variant(variant.Id), new ToastViewModel(toast!).Look);
	}

	[AvaloniaFact]
	public void Edge_GetsOnlyDefaultAndOwnImage()
	{
		PadVariant variant = PadPack.Variants[0];
		(_, MainWindowViewModel vm) = Start(Entry("pad-edge", edge: true));
		Assert.False(vm.Settings.SetLook("pad-edge", variant.Id));
		// A variant that got there anyway is drawn as the default art.
		vm.Settings.KnownControllers.SetLook("pad-edge", variant.Id, null);
		Assert.Equal(PadLook.Default, vm.Settings.LookOf("pad-edge", true));
		Assert.False(PadArt.Resolve(PadLook.Variant(variant.Id), edge: true).IsOwn);
		Assert.Same(PadArt.DefaultSet(true), PadArt.Resolve(PadLook.Variant(variant.Id), edge: true));

		ControllerLookViewModel look = vm.Controller.Look;
		look.OpenCommand.Execute(null);
		Assert.True(look.IsOpen);
		Assert.True(look.IsEdge);
		Assert.Equal([null, KnownController.OwnLook], look.Tiles.Select(t => t.Key));
		Assert.DoesNotContain(look.Groups, g => g.Title == Kopru.Core.Localization.Text.LookSpecial());

		// A standard controller gets the whole pack, Default first, standard before special.
		(_, MainWindowViewModel standard) = Start(Entry("pad-std"));
		standard.Controller.Look.OpenCommand.Execute(null);
		List<string?> keys = [.. standard.Controller.Look.Tiles.Select(t => t.Key)];
		Assert.Null(keys[0]);
		Assert.Equal(KnownController.OwnLook, keys[^1]);
		Assert.Equal(PadPack.Variants.Count + 2, keys.Count);
		Assert.Equal([.. PadPack.Variants.Where(v => !v.IsSpecial).Select(v => v.Id), .. PadPack.Variants.Where(v => v.IsSpecial).Select(v => v.Id)],
			keys.Skip(1).Take(PadPack.Variants.Count));
	}

	[AvaloniaFact]
	public void Picker_ChoosesAVariant_AndBackToDefault()
	{
		PadVariant variant = PadPack.Variants[^1];
		(_, MainWindowViewModel vm) = Start(Entry("pad-a"));
		ControllerLookViewModel look = vm.Controller.Look;
		look.OpenCommand.Execute(null);
		Assert.True(look.Tiles.First().IsSelected);
		PadLookTile tile = look.Tiles.Single(t => t.Key == variant.Id);
		tile.ChooseCommand.Execute(null);
		Settle();
		Assert.True(look.IsOpen);
		Assert.True(tile.IsSelected);
		Assert.Single(look.Tiles, t => t.IsSelected);
		Assert.Equal(Kopru.Core.Localization.Text.LookSelected(variant.Name), tile.AccessibleName);
		Assert.Equal(PadLook.Variant(variant.Id), vm.Settings.ActiveLook);
		Assert.Equal(variant.Id, vm.Settings.KnownControllers.Find("pad-a")!.Look);

		look.Tiles.First().ChooseCommand.Execute(null);
		Settle();
		Assert.Equal(PadLook.Default, vm.Settings.ActiveLook);
		Assert.Null(vm.Settings.KnownControllers.Find("pad-a")!.Look);
		look.CloseCommand.Execute(null);
		Assert.False(look.IsOpen);
	}

	[AvaloniaFact]
	public async Task OwnImage_IsCopiedAndDownscaled_RemoveGoesBack()
	{
		(_, MainWindowViewModel vm) = Start(Entry("pad-a"));
		string original = MakePicture(Path.Combine(_folder, "elsewhere"), "wide.png", 3000, 1200);
		ControllerLookViewModel look = vm.Controller.Look;
		look.PickImage = () => Task.FromResult<Stream?>(File.OpenRead(original));
		look.OpenCommand.Execute(null);
		await look.AddOwnImageAsync();
		Settle();

		string? copy = vm.Settings.OwnImageOf("pad-a");
		Assert.NotNull(copy);
		Assert.StartsWith(Path.Combine(_folder, "looks"), copy);
		using (var stored = new Bitmap(copy!))
		{
			Assert.Equal(new PixelSize(OwnImageStore.MaxWidth, 916), stored.PixelSize);
		}
		Assert.Equal(PadLook.Own(copy!), vm.Settings.ActiveLook);
		Assert.Equal(Kopru.Core.Localization.Text.LookOwn(), look.CurrentName);
		PadLookTile own = look.Tiles.Single(t => t.IsOwn);
		Assert.True(own.HasImage && own.CanRemove && own.IsSelected);

		// The original may go: the app keeps its own copy.
		File.Delete(original);
		Assert.True(PadArt.Resolve(vm.Settings.ActiveLook, false).IsOwn);

		// A variant chosen after it keeps the picture for later.
		PadVariant variant = PadPack.Variants[0];
		look.Tiles.Single(t => t.Key == variant.Id).ChooseCommand.Execute(null);
		Settle();
		Assert.Equal(PadLook.Variant(variant.Id), vm.Settings.ActiveLook);
		Assert.True(File.Exists(copy));
		look.Tiles.Single(t => t.IsOwn).ChooseCommand.Execute(null);
		Settle();
		Assert.Equal(PadLook.Own(copy!), vm.Settings.ActiveLook);

		// A small picture is kept at its size.
		string small = MakePicture(Path.Combine(_folder, "elsewhere"), "small.png", 400, 300);
		Assert.True(Import(vm.Settings, "pad-a", small));
		string? second = vm.Settings.OwnImageOf("pad-a");
		using (var stored = new Bitmap(second!))
		{
			Assert.Equal(new PixelSize(400, 300), stored.PixelSize);
		}
		// The replaced picture is deleted.
		Assert.False(File.Exists(copy));

		look.Tiles.Single(t => t.IsOwn).RemoveCommand.Execute(null);
		Settle();
		Assert.False(File.Exists(second));
		Assert.Null(vm.Settings.OwnImageOf("pad-a"));
		Assert.Equal(PadLook.Default, vm.Settings.ActiveLook);
		Assert.True(look.Tiles.Single(t => t.IsOwn).ShowsAdd);
	}

	[AvaloniaFact]
	public async Task OwnImage_MissingOrBrokenFile_FallsBackToTheDefault()
	{
		(_, MainWindowViewModel vm) = Start(Entry("pad-a"));
		ControllerLookViewModel look = vm.Controller.Look;

		// Not a picture: refused with a message, nothing changes.
		look.PickImage = () => Task.FromResult<Stream?>(new MemoryStream("not a picture"u8.ToArray()));
		look.OpenCommand.Execute(null);
		await look.AddOwnImageAsync();
		Settle();
		Assert.True(look.HasError);
		Assert.Null(vm.Settings.OwnImageOf("pad-a"));
		Assert.Equal(PadLook.Default, vm.Settings.ActiveLook);
		Assert.False(Directory.Exists(Path.Combine(_folder, "looks")) && Directory.EnumerateFiles(Path.Combine(_folder, "looks")).Any());

		// Cancelled: nothing happens.
		look.PickImage = () => Task.FromResult<Stream?>(null);
		await look.AddOwnImageAsync();
		Assert.Null(vm.Settings.OwnImageOf("pad-a"));

		// The copy deleted behind the app's back: drawn as the default art.
		string picture = MakePicture(_folder, "pic.png", 600, 400);
		Assert.True(Import(vm.Settings, "pad-a", picture));
		string copy = vm.Settings.OwnImageOf("pad-a")!;
		File.Delete(copy);
		Assert.Equal(PadLook.Default, vm.Settings.LookOf("pad-a", false));
		PadArtSet missing = PadArt.Resolve(PadLook.Own(copy), false);
		Assert.False(missing.IsOwn);
		Assert.Same(PadArt.DefaultSet(false), missing);

		// A broken file where the picture was: the default art, the art still renders.
		string broken = Path.Combine(_folder, "broken.png");
		File.WriteAllBytes(broken, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5]);
		Assert.Same(PadArt.DefaultSet(false), PadArt.Resolve(PadLook.Own(broken), false));
		Assert.Null(PadArt.Thumbnail(PadLook.Own(broken), false));
		var art = new ControllerArt { Look = PadLook.Own(broken), Width = 229, Height = 152 };
		var window = new Window { Width = 229, Height = 152, Content = art };
		window.Show();
		FitCheck.Settle(window);
		using (var frame = window.CaptureRenderedFrame())
		{
			Assert.NotNull(frame);
		}
		window.Close();
	}

	[AvaloniaFact]
	public void Settings_RoundTrip_AndOldSettingsLoadAsDefault()
	{
		PadVariant variant = PadPack.Variants[0];
		var store = new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now });
		var fake = new FakeControllerService(animate: false);
		fake.ControllerList.Add(Entry("pad-a"));
		fake.ControllerList.Add(Entry("pad-b", 2));
		var vm = new MainWindowViewModel(fake, new AppEnvironment(store));
		vm.Settings.OwnImages = new OwnImageStore(Path.Combine(_folder, "looks"));
		fake.RaiseStateChanged();
		Settle();
		Assert.True(vm.Settings.SetLook("pad-a", variant.Id));
		Assert.True(Import(vm.Settings, "pad-b", MakePicture(_folder, "b.png", 300, 200)));
		vm.Settings.SaveNow();

		// Through settings.json's own serializer.
		string file = Path.Combine(_folder, "settings.json");
		new JsonSettingsStore(file).Save(store.Saved!);
		AppSettings loaded = new JsonSettingsStore(file).Load()!;
		Assert.Contains("\"Look\"", File.ReadAllText(file));

		var again = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(new MemorySettingsStore(loaded)));
		again.Settings.OwnImages = new OwnImageStore(Path.Combine(_folder, "looks"));
		Assert.Equal(PadLook.Variant(variant.Id), again.Settings.LookOf("pad-a", false));
		Assert.Equal(PadLookKind.Own, again.Settings.LookOf("pad-b", false).Kind);

		// A settings file from before looks: every controller is drawn as the default.
		var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
		AppSettings old = JsonSerializer.Deserialize<AppSettings>("""
			{ "KnownControllers": [ { "Id": "pad-old", "Model": "DualSense", "Name": "Couch",
			  "FirstSeen": "2026-01-01T00:00:00+00:00", "LastSeen": "2026-01-02T00:00:00+00:00" } ] }
			""", options)!;
		var before = new MainWindowViewModel(new FakeControllerService(animate: false), new AppEnvironment(new MemorySettingsStore(old)));
		Assert.Null(before.Settings.KnownControllers.Find("pad-old")!.Look);
		Assert.Equal(PadLook.Default, before.Settings.LookOf("pad-old", false));
		Assert.Equal("Couch", before.Settings.ControllerName("pad-old", false));
	}

	[AvaloniaFact]
	public void Forget_DeletesTheOwnImage()
	{
		(FakeControllerService fake, MainWindowViewModel vm) = Start(Entry("pad-a"), Entry("pad-b", 2));
		Assert.True(Import(vm.Settings, "pad-b", MakePicture(_folder, "b.png", 300, 200)));
		string copy = vm.Settings.OwnImageOf("pad-b")!;
		fake.ControllerList.RemoveAt(1);
		fake.RaiseStateChanged();
		Settle();
		Assert.True(vm.Settings.ForgetController("pad-b"));
		Assert.False(File.Exists(copy));
	}

	[AvaloniaFact]
	public async Task OnlyTheDrawnVariantIsDecoded()
	{
		// Other tests in this process may have drawn variants already: only what this test adds is checked.
		PadVariant drawn = PadPack.Variants.First(v => !PadArt.LoadedVariants.Contains(v.Id));

		// An id the pack does not have draws as the default and is not remembered as unreadable: once a pack has it, it draws.
		string ghost = "ghost-" + Guid.NewGuid().ToString("N")[..8];
		Assert.Null(PadArt.Thumbnail(PadLook.Variant(ghost), false));
		Assert.Same(PadArt.DefaultSet(false), PadArt.Resolve(PadLook.Variant(ghost), false));
		Assert.DoesNotContain(ghost, PadArt.LoadedVariants);
		using (PadPack.Override([drawn with { Id = ghost, Folder = drawn.Folder ?? drawn.Id }]))
		{
			Assert.NotNull(PadArt.Thumbnail(PadLook.Variant(ghost), false));
			Assert.False(PadArt.Resolve(PadLook.Variant(ghost), false).IsOwn);
			Assert.NotSame(PadArt.DefaultSet(false), PadArt.Resolve(PadLook.Variant(ghost), false));
		}

		HashSet<string> before = [.. PadArt.LoadedVariants.Where(id => !id.StartsWith("ghost-", StringComparison.Ordinal))];

		// The picker's thumbnails are separate small decodes: they load no variant for drawing.
		(FakeControllerService fake, MainWindowViewModel vm) = Start(Entry("pad-a"));
		vm.Controller.Look.OpenCommand.Execute(null);
		await vm.Controller.Look.ThumbnailsReady;
		Settle();
		Assert.True(vm.Controller.Look.Tiles.Where(t => !t.ShowsAdd).All(t => t.Thumbnail != null),
			"no thumbnail: " + string.Join(", ", vm.Controller.Look.Tiles.Where(t => !t.ShowsAdd && t.Thumbnail == null).Select(t => t.Key ?? "default")));
		Assert.Equal(before, PadArt.LoadedVariants.Where(id => !id.StartsWith("ghost-", StringComparison.Ordinal)).ToHashSet());

		var art = new ControllerArt { Look = PadLook.Variant(drawn.Id), Width = 458, Height = 304 };
		var overlay = new ControllerInputOverlay { Look = PadLook.Variant(drawn.Id), Accent = Brushes.DodgerBlue, LeftStickX = 1, Pressed = PadButtons.Cross };
		var window = new Window { Width = 458, Height = 304, Content = new Panel { Children = { art, overlay } } };
		window.Show();
		FitCheck.Settle(window);
		using (window.CaptureRenderedFrame())
		{
		}
		window.Close();
		before.Add(drawn.Id);
		Assert.Equal(before, PadArt.LoadedVariants.Where(id => !id.StartsWith("ghost-", StringComparison.Ordinal)).ToHashSet());
		Assert.Same(PadArt.Resolve(PadLook.Variant(drawn.Id), false), PadArt.Resolve(PadLook.Variant(drawn.Id), false));
		_ = fake;
	}

	// A variant keeps the live light: the strip lights in the light's colour over the variant's body.
	[AvaloniaFact]
	public void Variant_KeepsTheLiveLight_OwnImageHasNoLightOrInput()
	{
		PadVariant variant = PadPack.Variants[0];
		byte[] off = RenderArt(PadLook.Variant(variant.Id), Colors.Black, out int w, out int h);
		byte[] red = RenderArt(PadLook.Variant(variant.Id), Color.FromRgb(255, 0, 0), out _, out _);
		Rect light = ControllerLayout.LightRect;
		double scale = w / ControllerLayout.ArtWidth;
		long redder = 0;
		for (int y = (int)(light.Y * scale); y < (int)(light.Bottom * scale) && y < h; y++)
		{
			for (int x = (int)(light.X * scale); x < (int)(light.Right * scale) && x < w; x++)
			{
				int i = (y * w + x) * 4;
				redder += Math.Max(0, red[i + 2] - off[i + 2] - Math.Max(0, red[i + 1] - off[i + 1]));
			}
		}
		Assert.True(redder > 20000, $"red added on the strip: {redder}");

		// An own picture: drawn without the light (lit or not, the same pixels) and the overlay draws nothing on it.
		string picture = MakePicture(_folder, "own.png", 800, 400);
		byte[] ownOff = RenderArt(PadLook.Own(picture), Colors.Black, out _, out _);
		byte[] ownRed = RenderArt(PadLook.Own(picture), Color.FromRgb(255, 0, 0), out _, out _);
		Assert.Equal(ownOff, ownRed);

		byte[] empty = Render(new Panel(), out _, out _);
		byte[] input = Render(new ControllerInputOverlay
		{
			Look = PadLook.Own(picture), Accent = Brushes.DodgerBlue, Pressed = PadButtons.Cross | PadButtons.L1 | PadButtons.Touchpad, LeftStickX = 1, L2Level = 100
		}, out _, out _);
		Assert.Equal(empty, input);
		// The same input on a variant does draw.
		byte[] variantInput = Render(new ControllerInputOverlay
		{
			Look = PadLook.Variant(variant.Id), Accent = Brushes.DodgerBlue, Pressed = PadButtons.Cross | PadButtons.L1 | PadButtons.Touchpad, LeftStickX = 1, L2Level = 100
		}, out _, out _);
		Assert.NotEqual(empty, variantInput);
	}

	private static byte[] RenderArt(PadLook look, Color colour, out int w, out int h) =>
		Render(new ControllerArt { Look = look, LightColor = colour, LightIntensity = 1, PlayerLeds = false, PlayerLedMask = 0 }, out w, out h);

	private static byte[] Render(Control content, out int w, out int h)
	{
		content.Width = 458;
		content.Height = 304;
		var window = new Window { Width = 458, Height = 304, Content = content, Background = new SolidColorBrush(Color.Parse("#1A1C20")) };
		window.Show();
		FitCheck.Settle(window);
		using var frame = window.CaptureRenderedFrame() as WriteableBitmap;
		Assert.NotNull(frame);
		w = frame!.PixelSize.Width;
		h = frame.PixelSize.Height;
		byte[] px = new byte[w * h * 4];
		using (ILockedFramebuffer buffer = frame.Lock())
		{
			for (int y = 0; y < h; y++)
			{
				System.Runtime.InteropServices.Marshal.Copy(buffer.Address + y * buffer.RowBytes, px, y * w * 4, w * 4);
			}
		}
		// As B, G, R, A.
		if (frame.Format == PixelFormats.Rgba8888)
		{
			for (int i = 0; i < px.Length; i += 4)
			{
				(px[i], px[i + 2]) = (px[i + 2], px[i]);
			}
		}
		window.Close();
		return px;
	}
}
