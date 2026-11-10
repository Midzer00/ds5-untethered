// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.Controls;
using Kopru.App.ViewModels;
using Kopru.App.Views;
using Kopru.Core.Input;
using Kopru.Core.Localization;

namespace Kopru.App.Tests;

// The controller test's live drawing: the position table, the animation state with a fake clock, no frames while idle or
// hidden, and renders of pressed states for checking alignment by eye.
public class TesterAnimationTests
{
	private static readonly TimeSpan T0 = TimeSpan.FromSeconds(10);

	private static TimeSpan Ms(double ms) => T0 + TimeSpan.FromMilliseconds(ms);

	// ---- Position table ----

	[Fact]
	public void Layout_EveryButtonHasARegionInsideTheArt()
	{
		foreach (PadButtons button in Enum.GetValues<PadButtons>().Where(b => b != PadButtons.None))
		{
			ControllerRegion? region = ControllerLayout.Find(button);
			Assert.True(region != null, $"{button} has no region");
			Assert.True(region!.Outline.Count >= 3, $"{button} outline");
			Rect b = region.Bounds;
			Assert.True(b.Width > 10 && b.Height > 10, $"{button} too small: {b}");
			Assert.True(b.X >= 0 && b.Y >= 0 && b.Right <= ControllerLayout.ArtWidth && b.Bottom <= ControllerLayout.ArtHeight,
				$"{button} outside the art: {b}");
			Assert.True(region.Contains(Centroid(region)), $"{button} does not contain its own centre");
		}
		Assert.Equal(ControllerLayout.Regions.Count, ControllerLayout.Regions.Select(r => r.Button).Distinct().Count());
	}

	[Fact]
	public void Layout_RegionsDoNotOverlap()
	{
		IReadOnlyList<ControllerRegion> regions = ControllerLayout.Regions;
		foreach (ControllerRegion a in regions)
		{
			Point c = Centroid(a);
			foreach (ControllerRegion b in regions)
			{
				if (a == b)
				{
					continue;
				}
				Assert.False(b.Contains(c), $"{a.Button}'s centre is inside {b.Button}");
				// Shared edges are fine (the bumper and the trigger touch); a real overlap is not. Area sampled on a 2 px grid.
				Rect both = a.Bounds.Intersect(b.Bounds);
				int shared = 0;
				for (double y = both.Top + 1; y < both.Bottom; y += 2)
				{
					for (double x = both.Left + 1; x < both.Right; x += 2)
					{
						if (a.Contains(new Point(x, y)) && b.Contains(new Point(x, y)))
						{
							shared++;
						}
					}
				}
				Assert.True(shared * 4 <= 30, $"{a.Button} overlaps {b.Button} by about {shared * 4} px²");
			}
		}
	}

	[Fact]
	public void Layout_DpadArmsAreTheSameShapeAroundOneCentre()
	{
		Rect up = ControllerLayout.Find(PadButtons.DpadUp)!.Bounds, down = ControllerLayout.Find(PadButtons.DpadDown)!.Bounds;
		Rect left = ControllerLayout.Find(PadButtons.DpadLeft)!.Bounds, right = ControllerLayout.Find(PadButtons.DpadRight)!.Bounds;
		Assert.Equal(up.Width, down.Width, 3);
		Assert.Equal(up.Width, left.Height, 3);
		Assert.InRange(right.Width - up.Height, -6, 6); // arm lengths follow the drawing within a few pixels
		Assert.InRange(down.Height - up.Height, -6, 6);
		Assert.Equal(up.Center.X, down.Center.X, 3);
		Assert.Equal(left.Center.Y, right.Center.Y, 3);
	}

	[Fact]
	public void Layout_StickCapStaysInsideItsWell()
	{
		foreach (StickWell stick in new[] { ControllerLayout.LeftStick, ControllerLayout.RightStick })
		{
			Assert.True(stick.CapRadius + stick.Travel <= stick.WellRadius);
			Assert.True(stick.Travel > 0);
		}
	}

	private static Point Centroid(ControllerRegion r) =>
		new(r.Outline.Average(p => p.X), r.Outline.Average(p => p.Y));

	// ---- Animator ----

	[Fact]
	public void Animator_PressIsFullAtOnce_ReleaseFadesOutInTheSetTime()
	{
		var a = new InputAnimator();
		Assert.Equal(0, a.Level(PadButtons.Cross, T0));
		Assert.False(a.IsAnimating(T0));

		a.SetButtons(PadButtons.Cross, T0);
		Assert.Equal(1, a.Level(PadButtons.Cross, T0));
		Assert.Equal(0, a.Level(PadButtons.Circle, T0));
		Assert.False(a.IsAnimating(Ms(500)));

		a.SetButtons(PadButtons.None, Ms(1000));
		Assert.Equal(1, a.Level(PadButtons.Cross, Ms(1000)), 6);
		double mid = a.Level(PadButtons.Cross, Ms(1000 + InputAnimator.FadeOut.TotalMilliseconds / 2));
		Assert.InRange(mid, 0.05, 0.5); // ease-out: past half way down at half time
		Assert.True(a.IsAnimating(Ms(1000 + InputAnimator.FadeOut.TotalMilliseconds - 1)));
		Assert.Equal(0, a.Level(PadButtons.Cross, Ms(1000) + InputAnimator.FadeOut));
		Assert.False(a.IsAnimating(Ms(1000) + InputAnimator.FadeOut));
		Assert.InRange(InputAnimator.FadeOut.TotalMilliseconds, 120, 160);
	}

	[Fact]
	public void Animator_FadeIsMonotonic()
	{
		var a = new InputAnimator();
		a.SetButtons(PadButtons.DpadUp, T0);
		a.SetButtons(PadButtons.None, T0);
		double previous = 1;
		for (int ms = 0; ms <= 160; ms += 5)
		{
			double level = a.Level(PadButtons.DpadUp, Ms(ms));
			Assert.True(level <= previous);
			previous = level;
		}
		Assert.Equal(0, previous);
	}

	[Fact]
	public void Animator_RepressDuringFadeIsFullAgain_AndTheNextReleaseFadesFromFull()
	{
		var a = new InputAnimator();
		a.SetButtons(PadButtons.Square, T0);
		a.SetButtons(PadButtons.None, Ms(10));
		Assert.True(a.Level(PadButtons.Square, Ms(80)) < 1);
		a.SetButtons(PadButtons.Square, Ms(80));
		Assert.Equal(1, a.Level(PadButtons.Square, Ms(80)));
		Assert.False(a.IsAnimating(Ms(80)));
		a.SetButtons(PadButtons.None, Ms(100));
		Assert.Equal(1, a.Level(PadButtons.Square, Ms(100)), 6);
		Assert.True(a.Level(PadButtons.Square, Ms(150)) > 0);
		Assert.Equal(0, a.Level(PadButtons.Square, Ms(100) + InputAnimator.FadeOut));
	}

	[Fact]
	public void Animator_DiagonalLightsTwoArms()
	{
		var a = new InputAnimator();
		a.SetButtons(PadButtons.DpadUp | PadButtons.DpadRight, T0);
		Assert.Equal(1, a.Level(PadButtons.DpadUp, T0));
		Assert.Equal(1, a.Level(PadButtons.DpadRight, T0));
		Assert.Equal(0, a.Level(PadButtons.DpadDown, T0));
		a.SetButtons(PadButtons.DpadUp, Ms(10));
		Assert.Equal(1, a.Level(PadButtons.DpadUp, Ms(20)));
		Assert.InRange(a.Level(PadButtons.DpadRight, Ms(20)), 0.01, 0.99);
	}

	[Fact]
	public void Animator_StickOffsetIsProportionalAndClamped()
	{
		Assert.Equal(new Vector(0, 0), InputAnimator.StickOffset(0, 0, 40));
		Assert.Equal(new Vector(20, 0), InputAnimator.StickOffset(0.5, 0, 40));
		Assert.Equal(new Vector(0, -40), InputAnimator.StickOffset(0, -1, 40));
		Vector corner = InputAnimator.StickOffset(1, 1, 40);
		Assert.Equal(40, corner.Length, 6);
		Assert.Equal(corner.X, corner.Y, 6);
		Assert.Equal(40, InputAnimator.StickOffset(5, -3, 40).Length, 6);
		Assert.Equal(new Vector(0, 0), InputAnimator.StickOffset(double.NaN, double.NaN, 40));
	}

	[Fact]
	public void Animator_TriggerFillFollowsPressure()
	{
		Assert.Equal(0, InputAnimator.TriggerFill(0, false));
		Assert.Equal(0.5, InputAnimator.TriggerFill(50, false), 6);
		Assert.Equal(0.25, InputAnimator.TriggerFill(25, true), 6);
		Assert.Equal(1, InputAnimator.TriggerFill(100, false));
		Assert.Equal(1, InputAnimator.TriggerFill(140, false));
		// Only the digital bit known (no analog level bound): full.
		Assert.Equal(1, InputAnimator.TriggerFill(0, true));
	}

	[Fact]
	public void Animator_TwoTouchPoints_FadeOnRelease()
	{
		var a = new InputAnimator();
		a.SetTouch(0, new TouchPoint(true, 1, 0, 0), T0);
		a.SetTouch(1, new TouchPoint(true, 2, TouchPoint.Width - 1, TouchPoint.Height - 1), T0);
		(double x0, double y0, double l0) = a.Touch(0, T0);
		(double x1, double y1, double l1) = a.Touch(1, T0);
		Assert.Equal((0.0, 0.0, 1.0), (x0, y0, l0));
		Assert.Equal((1.0, 1.0, 1.0), (x1, y1, l1));

		a.SetTouch(1, new TouchPoint(false, 2, 0, 0), Ms(100));
		(double rx, double ry, double rl) = a.Touch(1, Ms(150));
		Assert.Equal((1.0, 1.0), (rx, ry)); // fades where it was lifted
		Assert.InRange(rl, 0.01, 0.99);
		Assert.True(a.IsAnimating(Ms(150)));
		Assert.Equal(1, a.Touch(0, Ms(150)).Level);
		Assert.Equal(0, a.Touch(1, Ms(100) + InputAnimator.FadeOut).Level);
		Assert.False(a.IsAnimating(Ms(100) + InputAnimator.FadeOut));
	}

	[Fact]
	public void PixelSnap_LinesLandOnDevicePixels()
	{
		foreach (double scaling in new[] { 1.0, 1.25, 1.5, 2.0 })
		{
			double t = PixelSnap.Thickness(1, scaling);
			double device = t * scaling;
			Assert.Equal(Math.Round(device), device, 6);
			double edge = (PixelSnap.Line(37.3, t, scaling) - t / 2) * scaling;
			Assert.Equal(Math.Round(edge), edge, 6);
		}
	}

	[Fact]
	public void StickPreview_DotIsHollowInsideTheDeadzone()
	{
		Assert.True(StickPreview.IsInsideDeadzone(0.05, 0.05, 10));
		Assert.False(StickPreview.IsInsideDeadzone(0.2, 0, 10));
		Assert.False(StickPreview.IsInsideDeadzone(0.01, 0, 0));
		Assert.True(StickPreview.IsInsideDeadzone(0, 0, 0));
	}

	// ---- Frames: only while animating, never while hidden ----

	private sealed class FakeClock
	{
		public TimeSpan Now = T0;
		public TimeSpan Read() => Now;
	}

	private static void Pump(Window window, int frames = 3)
	{
		for (int i = 0; i < frames; i++)
		{
			Dispatcher.UIThread.RunJobs();
			AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			Dispatcher.UIThread.RunJobs();
			using var _ = window.CaptureRenderedFrame();
		}
	}

	[AvaloniaFact]
	public void Overlay_RequestsFramesOnlyWhileFading_AndNoneWhenHidden()
	{
		var clock = new FakeClock();
		var overlay = new ControllerInputOverlay { Clock = clock.Read, Accent = Brushes.DodgerBlue };
		var window = new Window { Width = 600, Height = 420, Content = overlay };
		window.Show();
		Pump(window);
		Assert.Equal(0, overlay.FrameRequests);

		// Held buttons, deflected sticks, pulled triggers: a still picture, no frames.
		overlay.Pressed = PadButtons.Cross | PadButtons.L1;
		overlay.LeftStickX = 0.7;
		overlay.L2Level = 50;
		Pump(window);
		Assert.Equal(0, overlay.FrameRequests);

		// Release: frames while the fade runs...
		clock.Now += TimeSpan.FromMilliseconds(10);
		overlay.Pressed = PadButtons.None;
		Pump(window);
		Assert.True(overlay.FrameRequests > 0);
		// ...and none once it is over.
		clock.Now += InputAnimator.FadeOut + TimeSpan.FromMilliseconds(1);
		Pump(window);
		int settled = overlay.FrameRequests;
		Pump(window, 5);
		Assert.Equal(settled, overlay.FrameRequests);

		// Hidden (a cached page): presses and releases cost no frames.
		overlay.IsVisible = false;
		overlay.Pressed = PadButtons.Triangle;
		Pump(window);
		clock.Now += TimeSpan.FromMilliseconds(10);
		overlay.Pressed = PadButtons.None;
		overlay.Touch1 = new TouchPoint(true, 1, 100, 100);
		overlay.Touch1 = new TouchPoint(false, 1, 100, 100);
		Pump(window, 5);
		Assert.Equal(settled, overlay.FrameRequests);
		window.Close();
	}

	[AvaloniaFact]
	public void StickPreview_TrailFadesThenStopsRequestingFrames()
	{
		var clock = new FakeClock();
		var preview = new StickPreview { Clock = clock.Read, Width = 140, Height = 140, IsLive = true, InnerDeadzone = 10, Accent = Brushes.DodgerBlue };
		var window = new Window { Width = 300, Height = 300, Content = preview };
		window.Show();
		Pump(window);
		Assert.Equal(0, preview.FrameRequests);

		for (int i = 1; i <= 6; i++)
		{
			clock.Now += TimeSpan.FromMilliseconds(16);
			preview.TunedX = i * 0.12;
			preview.TunedY = -i * 0.05;
		}
		Assert.True(preview.TrailPoints(clock.Now) >= 5);
		Pump(window);
		Assert.True(preview.FrameRequests > 0);

		clock.Now += StickPreview.TrailLength + TimeSpan.FromMilliseconds(1);
		Assert.Equal(0, preview.TrailPoints(clock.Now));
		Pump(window);
		int settled = preview.FrameRequests;
		Pump(window, 5);
		Assert.Equal(settled, preview.FrameRequests);

		preview.IsVisible = false;
		clock.Now += TimeSpan.FromMilliseconds(16);
		preview.TunedX = -0.5;
		Pump(window, 5);
		Assert.Equal(settled, preview.FrameRequests);
		window.Close();
	}

	// ---- Renders (files only with KOPRU_RENDER_SCREENS=1) ----

	private static readonly PadButtons AllButtons = Enum.GetValues<PadButtons>().Aggregate(PadButtons.None, (a, b) => a | b)
		& ~(PadButtons.L2 | PadButtons.R2);

	private static readonly (string Name, Action<ControllerInputOverlay> Apply)[] States =
	[
		("all", o =>
		{
			o.Pressed = AllButtons | PadButtons.L2 | PadButtons.R2;
			o.L2Level = 100;
			o.R2Level = 100;
		}),
		("mixed", o =>
		{
			o.Pressed = PadButtons.DpadUp | PadButtons.DpadLeft | PadButtons.Circle | PadButtons.R3;
			o.L2Level = 45;
			o.R2Level = 75;
			o.LeftStickX = -0.8;
			o.LeftStickY = -0.55;
			o.RightStickX = 0.9;
			o.RightStickY = 0.35;
		}),
		("touch", o =>
		{
			o.Pressed = PadButtons.DpadDown | PadButtons.DpadRight | PadButtons.Options | PadButtons.Mute;
			o.Touch1 = new TouchPoint(true, 1, 420, 300);
			o.Touch2 = new TouchPoint(true, 2, 1500, 820);
			o.LeftStickY = 1;
			o.RightStickX = -0.4;
			o.R2Level = 20;
		}),
	];

	[AvaloniaFact]
	public void RenderPressedStates()
	{
		bool writeFiles = Environment.GetEnvironmentVariable("KOPRU_RENDER_SCREENS") == "1";
		string dir = Path.Combine(RepoRoot(), "docs", "screens");
		// Large: the art and the overlay alone, at about half the asset's size, for checking alignment.
		foreach (bool edge in new[] { false, true })
		{
			foreach ((string name, Action<ControllerInputOverlay> apply) in States)
			{
				var clock = new FakeClock();
				var overlay = new ControllerInputOverlay { Clock = clock.Read, IsEdge = edge, Accent = new SolidColorBrush(Color.Parse("#3B82F6")) };
				var panel = new Panel { Children = { new ControllerArt { IsEdge = edge }, overlay } };
				var window = new Window { Width = 1145, Height = 760, Content = panel, Background = new SolidColorBrush(Color.Parse("#1A1C20")) };
				window.Show();
				apply(overlay);
				Pump(window, 1);
				using var frame = window.CaptureRenderedFrame();
				Assert.NotNull(frame);
				if (writeFiles && (!edge || name == "all"))
				{
					frame!.Save(Path.Combine(dir, $"tester-art-{name}{(edge ? "-edge" : "")}.png"));
				}
				window.Close();
			}
		}

		// The tester page itself with the same states.
		Language? before = Localizer.Chosen;
		try
		{
			Localizer.Chosen = Language.English;
			var service = new FakeControllerService(animate: false);
			var vm = new MainWindowViewModel(service);
			var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
			window.Show();
			vm.OpenController(ControllerPageViewModel.TestTab);
			service.LiveHandler!(TesterTests.Sample);
			vm.Tester.Tick();
			Pump(window, 1);
			var clock = new FakeClock();
			foreach ((string name, Action<ControllerInputOverlay> apply) in States)
			{
				ControllerInputOverlay overlay = window.GetVisualDescendants().OfType<ControllerInputOverlay>().First(o => o.IsEffectivelyVisible);
				overlay.Clock = clock.Read;
				overlay.Pressed = PadButtons.None;
				overlay.Touch1 = overlay.Touch2 = default;
				overlay.LeftStickX = overlay.LeftStickY = overlay.RightStickX = overlay.RightStickY = 0;
				overlay.L2Level = overlay.R2Level = 0;
				clock.Now += TimeSpan.FromSeconds(1); // the previous state's fades are over
				apply(overlay);
				Pump(window, 1);
				using var frame = window.CaptureRenderedFrame();
				Assert.NotNull(frame);
				if (writeFiles)
				{
					frame!.Save(Path.Combine(dir, $"tester-pressed-{name}-en.png"));
				}
			}
			window.Close();
		}
		finally
		{
			Localizer.Chosen = before;
		}
	}

	// The tester page's art follows the live input: sticks move (-1..1, Y down) and triggers fill with their pressure
	// (0..100 %); the stick cards' rings follow the inner deadzone slider next to them.
	[AvaloniaFact]
	public void TesterPage_OverlayAndStickPreviews_FollowTheLiveInputAndTheSliders()
	{
		var service = new FakeControllerService(animate: false);
		var vm = new MainWindowViewModel(service);
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		vm.OpenController(ControllerPageViewModel.TestTab);
		Pump(window, 1);
		ControllerInputOverlay overlay = window.GetVisualDescendants().OfType<ControllerInputOverlay>().First(o => o.IsEffectivelyVisible);
		Assert.Equal(0, overlay.LeftStickX);
		Assert.Equal(0, overlay.L2Level);

		service.LiveHandler!(TesterTests.Sample);
		vm.Tester.Tick();
		Pump(window, 1);
		// Sample: left stick (40, 70), right (215, 160), L2 96, R2 230.
		Assert.Equal((40 - 127.5) / 127.5, overlay.LeftStickX, 6);
		Assert.Equal((70 - 127.5) / 127.5, overlay.LeftStickY, 6);
		Assert.Equal((215 - 127.5) / 127.5, overlay.RightStickX, 6);
		Assert.Equal((160 - 127.5) / 127.5, overlay.RightStickY, 6);
		Assert.Equal(96 / 255.0 * 100, overlay.L2Level, 6);
		Assert.Equal(230 / 255.0 * 100, overlay.R2Level, 6);
		Assert.InRange(InputAnimator.TriggerFill(overlay.L2Level, true), 0.3, 0.45);

		// The stick cards: raw position and the deadzone ring from the slider beside them.
		StickPreview[] previews = window.GetVisualDescendants().OfType<StickPreview>().Where(p => p.IsEffectivelyVisible).ToArray();
		Assert.Equal(2, previews.Length);
		StickPreview left = previews.Single(p => p.RawX < 0);
		Assert.True(left.IsLive);
		vm.Tester.LeftPanel.Tune.Inner = 30;
		Pump(window, 1);
		Assert.Equal(30, left.InnerDeadzone);
		window.Close();
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
