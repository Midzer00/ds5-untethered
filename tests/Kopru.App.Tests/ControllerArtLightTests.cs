// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Kopru.App.Controls;

namespace Kopru.App.Tests;

// The light strip lines up with the art at every size the app draws it (the sidebar card's 62 x 42 up to large) and at
// the screen scale factors in use (drawn through a scale transform, as a high-DPI screen does): every pixel the light
// changes lies where the light masks have light (the strip and its glow, mapped onto the art's fit), none across the
// touchpad's middle.
public sealed class ControllerArtLightTests
{
	public static TheoryData<double, double, double> Cases => new()
	{
		{ 62, 42, 1.0 }, { 62, 42, 1.5 }, { 62, 42, 2.0 }, { 62, 42, 2.25 },
		{ 204, 136, 1.0 }, { 204, 136, 2.25 }, { 1145, 760, 1.0 },
	};

	[AvaloniaTheory]
	[MemberData(nameof(Cases))]
	public void LitPixels_FallOnTheStripAndItsGlow(double width, double height, double scaling)
	{
		foreach (bool edge in new[] { false, true })
		{
			byte[] dark = Render(edge, Colors.Black, width, height, scaling, out int w, out int h);
			byte[] lit = Render(edge, Color.FromRgb(0, 255, 0), width, height, scaling, out _, out _);
			// Where the art sits in the frame (the control's uniform fit, centred), in device pixels.
			double fit = Math.Min(width / ControllerLayout.ArtWidth, height / ControllerLayout.ArtHeight) * scaling;
			double ox = (w - ControllerLayout.ArtWidth * fit) / 2, oy = (h - ControllerLayout.ArtHeight * fit) / 2;
			bool[] allowed = Allowed(edge);
			Rect light = ControllerLayout.LightRect;
			int changed = 0, outside = 0, interior = 0;
			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					int i = (y * w + x) * 4;
					int diff = Math.Abs(lit[i] - dark[i]) + Math.Abs(lit[i + 1] - dark[i + 1]) + Math.Abs(lit[i + 2] - dark[i + 2]);
					if (diff < 24)
					{
						continue;
					}
					changed++;
					// The device pixel's centre in art pixels; it is on the light if the mask has light anywhere under it (its
					// centre or its corners: at the smallest size one device pixel covers dozens of art pixels).
					double ax = (x + 0.5 - ox) / fit, ay = (y + 0.5 - oy) / fit;
					bool on = false;
					foreach ((double dx, double dy) in new[] { (0.5, 0.5), (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0) })
					{
						int mx = (int)Math.Floor((x + dx - ox) / fit - light.X), my = (int)Math.Floor((y + dy - oy) / fit - light.Y);
						on |= mx >= 0 && my >= 0 && mx < light.Width && my < light.Height && allowed[my * (int)light.Width + mx];
					}
					if (!on)
					{
						outside++;
					}
					// The touchpad's middle (well inside the strip) stays unlit.
					if (ax > 860 && ax < 1430 && ay > 120 && ay < 430)
					{
						interior++;
					}
				}
			}
			string what = $"{(edge ? "Edge" : "DualSense")} {width}x{height} @ {scaling}";
			Assert.True(changed > 3, $"{what}: the light changes nothing");
			Assert.True(outside <= changed / 50, $"{what}: {outside} of {changed} lit pixels off the strip");
			Assert.True(interior == 0, $"{what}: {interior} lit pixels across the touchpad");
		}
	}

	// Every shipped mask in colour (as LightTint hands it to the drawing, at half the art's pixels) lights only the controller
	// and only the strip with a short halo, as a real DualSense does: no alpha beyond the art's own (nothing off the body,
	// at its edge no more than the body itself), nothing further than LightReach out from the strip's edge (no patch beside
	// its ends, no speckles on the touchpad), the strip and its bright middle only on the strip, and the halo never
	// stronger than LightGlowPeak.
	[AvaloniaFact]
	public void TintedLight_StaysOnTheBodyAndBesideTheStrip()
	{
		Rect light = ControllerLayout.LightRect;
		int lw = (int)light.Width, lh = (int)light.Height;
		bool[] strip = Raster(lw, lh, 3);
		// The halo's reach, plus a pixel and a half for anti-aliasing and the masks' halving.
		bool[] reach = Raster(lw, lh, ControllerLayout.LightReach + 1.5);
		var red = Color.FromRgb(255, 0, 0);
		foreach (bool edge in new[] { false, true })
		{
			byte[] art = Pixels(ControllerArt.ArtBitmap(edge), out int aw, out _);
			foreach ((int part, string name) in new[] { (LightTint.Glow, "glow"), (LightTint.Strip, "strip"), (LightTint.Core, "core") })
			{
				byte[] px = Pixels(LightTint.Get(edge, part, red), out int w, out int h);
				int factor = lw / w;
				Assert.Equal((lw / factor, lh / factor), (w, h));
				int offBody = 0, far = 0, offStrip = 0, bright = 0, lit = 0;
				for (int y = 0; y < h; y++)
				{
					for (int x = 0; x < w; x++)
					{
						int a = px[(y * w + x) * 4 + 3];
						if (a == 0)
						{
							continue;
						}
						lit++;
						// The art pixels under this one: the least body alpha, and whether any is on the strip / in reach.
						int body = 255;
						bool onStrip = false, inReach = false;
						for (int dy = 0; dy < factor; dy++)
						{
							for (int dx = 0; dx < factor; dx++)
							{
								int mx = x * factor + dx, my = y * factor + dy;
								body = Math.Min(body, art[((my + (int)light.Y) * aw + mx + (int)light.X) * 4 + 3]);
								onStrip |= strip[my * lw + mx];
								inReach |= reach[my * lw + mx];
							}
						}
						offBody += a > body + 2 ? 1 : 0;
						far += inReach ? 0 : 1;
						offStrip += part != LightTint.Glow && !onStrip ? 1 : 0;
						bright += part == LightTint.Glow && !onStrip && a > ControllerLayout.LightGlowPeak * 255 + 2 ? 1 : 0;
					}
				}
				string what = $"{(edge ? "Edge" : "DualSense")} {name}";
				Assert.True(lit > 1000, $"{what}: the mask is empty");
				Assert.True(offBody == 0, $"{what}: {offBody} pixels lit beyond the controller's body");
				Assert.True(far == 0, $"{what}: {far} pixels lit further than {ControllerLayout.LightReach} px out from the strip");
				Assert.True(offStrip == 0, $"{what}: {offStrip} pixels lit off the strip");
				Assert.True(bright == 0, $"{what}: {bright} glow pixels brighter than {ControllerLayout.LightGlowPeak}");
			}
		}
	}

	// The light strip (ControllerArt's band) filled and stroked round its edge `grow` asset pixels wide, over LightRect.
	private static bool[] Raster(int w, int h, double grow)
	{
		Rect light = ControllerLayout.LightRect;
		using var target = new RenderTargetBitmap(new PixelSize(w, h));
		using (DrawingContext dc = target.CreateDrawingContext())
		using (dc.PushTransform(Matrix.CreateTranslation(-light.X, -light.Y)))
		{
			dc.DrawGeometry(Brushes.White, new Pen(Brushes.White, 2 * grow), ControllerArt.BandGeometry);
		}
		byte[] px = Pixels(target, out _, out _);
		var inside = new bool[w * h];
		for (int i = 0; i < inside.Length; i++)
		{
			inside[i] = px[i * 4 + 3] > 0;
		}
		return inside;
	}

	// Mask pixels with any light, grown by 1 % of the art's width (a device pixel at the smallest size covers ~20 art
	// pixels, and smooth scaling spreads an edge by about one).
	private static bool[] Allowed(bool edge)
	{
		Rect light = ControllerLayout.LightRect;
		int w = (int)light.Width, h = (int)light.Height;
		var any = new bool[w * h];
		foreach (string part in new[] { "glow", "strip", "core" })
		{
			using var bitmap = new Bitmap(AssetLoader.Open(new Uri($"avares://Kopru.App/Assets/{(edge ? "controller-edge" : "controller")}-light-{part}.png")));
			byte[] px = Pixels(bitmap, out _, out _);
			for (int i = 0; i < w * h; i++)
			{
				any[i] |= px[i * 4 + 3] > 8;
			}
		}
		const int Grow = 24;
		var grown = new bool[w * h];
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				if (!any[y * w + x])
				{
					continue;
				}
				for (int dy = -Grow; dy <= Grow; dy += 4)
				{
					for (int dx = -Grow; dx <= Grow; dx += 4)
					{
						int yy = y + dy, xx = x + dx;
						if (yy >= 0 && yy < h && xx >= 0 && xx < w)
						{
							grown[yy * w + xx] = true;
						}
					}
				}
			}
		}
		return grown;
	}

	// The art alone at width x height, drawn at the given screen scale (a scale transform round it).
	private static byte[] Render(bool edge, Color colour, double width, double height, double scaling, out int w, out int h)
	{
		var art = new ControllerArt { IsEdge = edge, LightColor = colour, LightIntensity = 1, PlayerLeds = false, PlayerLedMask = 0, Width = width, Height = height };
		var holder = new LayoutTransformControl { LayoutTransform = new ScaleTransform(scaling, scaling), Child = art };
		var window = new Window
		{
			Width = Math.Ceiling(width * scaling), Height = Math.Ceiling(height * scaling), Content = holder,
			Background = new SolidColorBrush(Color.Parse("#1A1C20")), SizeToContent = SizeToContent.Manual,
		};
		window.Show();
		FitCheck.Settle(window);
		using WriteableBitmap? frame = window.CaptureRenderedFrame() as WriteableBitmap;
		Assert.NotNull(frame);
		byte[] px = Pixels(frame!, out w, out h);
		window.Close();
		return px;
	}

	private static byte[] Pixels(Bitmap bitmap, out int w, out int h)
	{
		w = bitmap.PixelSize.Width;
		h = bitmap.PixelSize.Height;
		byte[] px = new byte[w * h * 4];
		var handle = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
		try
		{
			bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), px.Length, w * 4);
		}
		finally
		{
			handle.Free();
		}
		return px;
	}
}
