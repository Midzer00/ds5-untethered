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
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// For reviewing the controller art (tools/art/controller): with KOPRU_PAGE_RENDER_DIR set, saves for the DualSense and
// the Edge the art with its overlay alone (buttons pressed, a trigger at half, a clicked stick, a finger on the
// touchpad; the light strip in three colours at full and low intensity; frames of a press fading out and of the light
// breathing), and the controller test and Overview at 1920x1080 in Turkish. Without the variable it does nothing.
public sealed class ControllerArtRenderTests
{
	private static void Held(ControllerInputOverlay o)
	{
		o.Pressed = PadButtons.Cross | PadButtons.Square | PadButtons.DpadUp | PadButtons.DpadLeft | PadButtons.L1 | PadButtons.Options
			| PadButtons.PS | PadButtons.R3;
		o.L2Level = 45;
		o.R2Level = 100;
		o.LeftStickX = -0.7;
		o.LeftStickY = -0.5;
		o.Touch1 = new TouchPoint(true, 1, 520, 360);
	}

	// The moved-stick sprites are rendered with the art (tools/art/controller): present for both controllers and both
	// sticks, the size the overlay draws them at, and the cap big enough for its own radius.
	[AvaloniaFact]
	public void StickSprites_MatchTheLayout()
	{
		int side = (int)(2 * ControllerLayout.StickSpriteHalf);
		foreach (bool edge in new[] { false, true })
		{
			Assert.Equal(new PixelSize(side, side), ControllerArt.StickCapSprite(edge).PixelSize);
			foreach (bool left in new[] { true, false })
			{
				Assert.Equal(new PixelSize(side, side), ControllerArt.StickWellSprite(edge, left).PixelSize);
			}
		}
		foreach (StickWell stick in new[] { ControllerLayout.LeftStick, ControllerLayout.RightStick })
		{
			Assert.True(stick.CapRadius + stick.Travel <= ControllerLayout.StickSpriteHalf);
		}
	}

	// The light-strip masks are rendered with the art: present for both controllers, all three the same size, and their
	// place lies inside the art.
	[AvaloniaFact]
	public void LightMasks_MatchTheLayout()
	{
		Rect light = ControllerLayout.LightRect;
		Assert.True(light.X >= 0 && light.Y >= 0 && light.Right <= ControllerLayout.ArtWidth && light.Bottom <= ControllerLayout.ArtHeight);
		foreach (string prefix in new[] { "controller", "controller-edge" })
		{
			foreach (string part in new[] { "glow", "strip", "core" })
			{
				using var bitmap = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri($"avares://Kopru.App/Assets/{prefix}-light-{part}.png")));
				Assert.Equal(new PixelSize((int)light.Width, (int)light.Height), bitmap.PixelSize);
			}
		}
	}

	private sealed class FakeClock
	{
		public TimeSpan Now = TimeSpan.FromSeconds(10);
		public TimeSpan Read() => Now;
	}

	private static Window ArtWindow(bool edge, out ControllerArt art, out ControllerInputOverlay overlay, FakeClock clock)
	{
		overlay = new ControllerInputOverlay { IsEdge = edge, Clock = clock.Read, Accent = new SolidColorBrush(Color.Parse("#3B82F6")) };
		art = new ControllerArt { IsEdge = edge };
		var window = new Window { Width = 1145, Height = 760, Content = new Panel { Children = { art, overlay } }, Background = new SolidColorBrush(Color.Parse("#1A1C20")) };
		window.Show();
		return window;
	}

	[AvaloniaFact]
	public void SaveReviewRenders()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_PAGE_RENDER_DIR");
		if (string.IsNullOrEmpty(outDir))
		{
			return;
		}
		Directory.CreateDirectory(outDir);
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.Turkish;
			foreach ((ControllerKind kind, string k) in new[] { (ControllerKind.DualSense, "ds"), (ControllerKind.DualSenseEdge, "edge") })
			{
				bool edge = kind == ControllerKind.DualSenseEdge;
				var clock = new FakeClock();

				// Pressed: face buttons, d-pad, Options, PS, L1, L2 at half, R3 clicked, a finger on the touchpad.
				Window window = ArtWindow(edge, out ControllerArt art, out ControllerInputOverlay overlay, clock);
				overlay.Pressed = PadButtons.Cross | PadButtons.Circle | PadButtons.DpadUp | PadButtons.Options | PadButtons.PS | PadButtons.L1 | PadButtons.R3;
				overlay.L2Level = 50;
				overlay.Touch1 = new TouchPoint(true, 1, 640, 420);
				FitCheck.Settle(window);
				Save(window, outDir, $"art-{k}-pressed.png");

				// A press fading out: six frames from the release on.
				overlay.Pressed = PadButtons.None;
				overlay.Touch1 = default;
				overlay.L2Level = 0;
				clock.Now += TimeSpan.FromSeconds(1);
				overlay.Pressed = PadButtons.Cross | PadButtons.Circle | PadButtons.R1;
				FitCheck.Settle(window);
				clock.Now += TimeSpan.FromMilliseconds(5);
				overlay.Pressed = PadButtons.None;
				for (int f = 0; f < 6; f++)
				{
					FitCheck.Settle(window);
					Save(window, outDir, $"frame-press-{k}-{f}.png");
					clock.Now += InputAnimator.FadeOut / 5;
				}

				// The light: three colours at full and low intensity.
				foreach ((uint colour, string name) in new[] { (0x2F6BFFu, "blue"), (0xFF3B30u, "red"), (0x22C55Eu, "green") })
				{
					foreach ((double intensity, string level) in new[] { (1.0, "full"), (0.15, "low") })
					{
						art.LightColor = Color.FromUInt32(0xFF000000 | colour);
						art.LightIntensity = intensity;
						FitCheck.Settle(window);
						Save(window, outDir, $"art-{k}-light-{name}-{level}.png");
					}
				}

				// Breathing (blue, 10..100 %): six frames over half a period.
				art.LightIntensity = 1;
				for (int f = 0; f < 6; f++)
				{
					double b = 0.1 + 0.9 * (0.5 + 0.5 * Math.Cos(Math.PI * f / 5));
					art.LightColor = Color.FromRgb((byte)(0x2F * b), (byte)(0x6B * b), (byte)(0xFF * b));
					FitCheck.Settle(window);
					Save(window, outDir, $"frame-breath-{k}-{f}.png");
				}
				window.Close();

				// The tester (buttons held) and the Overview, at 1920x1080.
				var fake = new FakeControllerService(kind, animate: false);
				var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = "tr" })));
				vm.Overview.ToggleBridgeCommand.Execute(null);
				fake.Tick();
				var main = new MainWindow { DataContext = vm, Width = 1920, Height = 1080 };
				main.Show();
				vm.Navigate(vm.Overview);
				FitCheck.Settle(main);
				Save(main, outDir, $"overview-{k}-1920x1080.png");
				vm.OpenController(ControllerPageViewModel.TestTab);
				fake.LiveHandler!(TesterTests.Sample);
				vm.Tester.Tick();
				FitCheck.Settle(main);
				ControllerInputOverlay live = main.GetVisualDescendants().OfType<ControllerInputOverlay>().First(o => o.IsEffectivelyVisible);
				Held(live);
				FitCheck.Settle(main);
				Save(main, outDir, $"tester-{k}-1920x1080.png");
				main.Close();
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The sidebar card and the Overview at 1920x1080 and 3840x2160 (Turkish), with the sidebar card's art cut out and
	// enlarged, for checking the light on the smallest drawing. Only with KOPRU_SIDEBAR_RENDER_DIR set.
	[AvaloniaFact]
	public void SaveSidebarRenders()
	{
		string? outDir = Environment.GetEnvironmentVariable("KOPRU_SIDEBAR_RENDER_DIR");
		if (string.IsNullOrEmpty(outDir))
		{
			return;
		}
		Directory.CreateDirectory(outDir);
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.Turkish;
			foreach ((ControllerKind kind, string k) in new[] { (ControllerKind.DualSense, "ds"), (ControllerKind.DualSenseEdge, "edge") })
			{
				var fake = new FakeControllerService(kind, animate: false);
				var vm = new MainWindowViewModel(fake, new AppEnvironment(new MemorySettingsStore(new AppSettings { GameListCheckedAt = DateTimeOffset.Now, Language = "tr" })));
				vm.Overview.ToggleBridgeCommand.Execute(null);
				fake.Tick();
				foreach ((double w, double h) in new[] { (1920.0, 1080.0), (3840.0, 2160.0) })
				{
					var main = new MainWindow { DataContext = vm, Width = w, Height = h };
					main.Show();
					vm.Navigate(vm.Overview);
					FitCheck.Settle(main);
					Save(main, outDir, $"overview-{k}-{w}x{h}.png");
					// The sidebar card's art: the smallest ControllerArt on screen.
					ControllerArt art = main.GetVisualDescendants().OfType<ControllerArt>().Where(a => a.IsEffectivelyVisible)
						.OrderBy(a => a.Bounds.Width).First();
					Matrix? toWindow = art.TransformToVisual(main);
					using var frame = main.CaptureRenderedFrame();
					if (toWindow is { } m && frame != null)
					{
						double s = frame.PixelSize.Width / main.Bounds.Width;
						Rect r = new Rect(art.Bounds.Size).TransformToAABB(m);
						var crop = new PixelRect((int)(r.X * s) - 8, (int)(r.Y * s) - 8, (int)(r.Width * s) + 16, (int)(r.Height * s) + 16);
						var target = new RenderTargetBitmap(new PixelSize(crop.Width * 8, crop.Height * 8));
						using (DrawingContext dc = target.CreateDrawingContext())
						{
							RenderOptions.SetBitmapInterpolationMode(main, Avalonia.Media.Imaging.BitmapInterpolationMode.None);
							dc.DrawImage(frame, new Rect(crop.X, crop.Y, crop.Width, crop.Height), new Rect(0, 0, crop.Width * 8, crop.Height * 8));
						}
						target.Save(Path.Combine(outDir, $"sidebar-{k}-{w}x{h}-x8.png"));
					}
					main.Close();
				}
			}
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	private static void Save(Window window, string dir, string file)
	{
		Dispatcher.UIThread.RunJobs();
		AvaloniaHeadlessPlatform.ForceRenderTimerTick();
		using var frame = window.CaptureRenderedFrame();
		Assert.NotNull(frame);
		frame!.Save(Path.Combine(dir, file));
	}
}
