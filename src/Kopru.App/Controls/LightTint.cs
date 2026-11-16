// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Kopru.App.Controls;

// The light-strip masks (Assets/controller[-edge]-light-{glow,strip,core}.png, white with the mask in alpha) as bitmaps
// already in the light's colour, for plain DrawImage: the same path as the art itself, so they scale and line up with it at
// every size and screen scale (an opacity mask through an image brush did not, on the GPU at small sizes). The masks are
// kept at half resolution (the art is never drawn larger than about half its pixels); a colour is tinted once and kept,
// the few most recent per controller, so breathing (one hue) costs nothing and rainbow about a millisecond per new hue.
internal static class LightTint
{
	public const int Glow = 0, Strip = 1, Core = 2;
	private const int Factor = 2;
	private const int Kept = 4;

	private static readonly Lazy<(byte[] Alpha, int W, int H)>[] Masks =
	[
		Load("controller-light-glow.png"), Load("controller-light-strip.png"), Load("controller-light-core.png"),
		Load("controller-edge-light-glow.png"), Load("controller-edge-light-strip.png"), Load("controller-edge-light-core.png"),
	];

	private static readonly List<(bool Edge, int Part, Color Colour, WriteableBitmap Bitmap)> Cache = [];

	// The mask in colour (alpha from the mask, premultiplied), colour components rounded to steps of 4 so near colours share.
	public static Bitmap Get(bool edge, int part, Color colour)
	{
		colour = Color.FromRgb(Step(colour.R), Step(colour.G), Step(colour.B));
		int found = Cache.FindIndex(e => e.Edge == edge && e.Part == part && e.Colour == colour);
		if (found >= 0)
		{
			var hit = Cache[found];
			Cache.RemoveAt(found);
			Cache.Add(hit);
			return hit.Bitmap;
		}
		(byte[] alpha, int w, int h) = Masks[(edge ? 3 : 0) + part].Value;
		// Reuse the oldest bitmap of this mask once enough colours are kept (rainbow would otherwise allocate every frame).
		WriteableBitmap? bitmap = null;
		int same = Cache.Count(e => e.Edge == edge && e.Part == part);
		if (same >= Kept)
		{
			int oldest = Cache.FindIndex(e => e.Edge == edge && e.Part == part);
			bitmap = Cache[oldest].Bitmap;
			Cache.RemoveAt(oldest);
		}
		bitmap ??= new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
		byte[] px = new byte[w * h * 4];
		for (int i = 0; i < alpha.Length; i++)
		{
			int a = alpha[i];
			px[i * 4] = (byte)(colour.B * a / 255);
			px[i * 4 + 1] = (byte)(colour.G * a / 255);
			px[i * 4 + 2] = (byte)(colour.R * a / 255);
			px[i * 4 + 3] = (byte)a;
		}
		using (ILockedFramebuffer fb = bitmap.Lock())
		{
			for (int y = 0; y < h; y++)
			{
				Marshal.Copy(px, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
			}
		}
		Cache.Add((edge, part, colour, bitmap));
		return bitmap;
	}

	private static byte Step(byte v) => (byte)Math.Min(255, (v + 2) / 4 * 4);

	// The mask's alpha, averaged down by Factor.
	private static Lazy<(byte[] Alpha, int W, int H)> Load(string name) => new(() =>
	{
		using var bitmap = new Bitmap(AssetLoader.Open(new Uri($"avares://Kopru.App/Assets/{name}")));
		int w = bitmap.PixelSize.Width, h = bitmap.PixelSize.Height;
		byte[] px = new byte[w * h * 4];
		GCHandle handle = GCHandle.Alloc(px, GCHandleType.Pinned);
		try
		{
			bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), px.Length, w * 4);
		}
		finally
		{
			handle.Free();
		}
		int sw = w / Factor, sh = h / Factor;
		byte[] alpha = new byte[sw * sh];
		for (int y = 0; y < sh; y++)
		{
			for (int x = 0; x < sw; x++)
			{
				int sum = 0;
				for (int dy = 0; dy < Factor; dy++)
				{
					for (int dx = 0; dx < Factor; dx++)
					{
						sum += px[((y * Factor + dy) * w + x * Factor + dx) * 4 + 3];
					}
				}
				alpha[y * sw + x] = (byte)(sum / (Factor * Factor));
			}
		}
		return (alpha, sw, sh);
	});
}
