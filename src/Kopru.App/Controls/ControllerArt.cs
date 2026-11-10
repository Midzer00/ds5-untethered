// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Kopru.App.Controls;

// Front view of the controller (Assets/controller.png, or the dark controller-edge.png for the Edge), scaled to fit.
// The touchpad and the light strips around it are vector shapes over the bitmap (clean edges at any size), the strips
// painted live in LightColor at LightIntensity; the player LEDs under the touchpad are drawn on top. No logos.
public sealed class ControllerArt : Control
{
	public static readonly StyledProperty<bool> IsEdgeProperty = AvaloniaProperty.Register<ControllerArt, bool>(nameof(IsEdge));
	public static readonly StyledProperty<Color> LightColorProperty =
		AvaloniaProperty.Register<ControllerArt, Color>(nameof(LightColor), Color.FromRgb(0x2F, 0x6B, 0xFF));
	public static readonly StyledProperty<double> LightIntensityProperty =
		AvaloniaProperty.Register<ControllerArt, double>(nameof(LightIntensity), 1.0);
	public static readonly StyledProperty<bool> PlayerLedsProperty = AvaloniaProperty.Register<ControllerArt, bool>(nameof(PlayerLeds), true);
	// Player LED bits 0..4 left to right; -1 uses PlayerLeds (the middle one).
	public static readonly StyledProperty<int> PlayerLedMaskProperty = AvaloniaProperty.Register<ControllerArt, int>(nameof(PlayerLedMask), -1);

	private static readonly Lazy<Bitmap> Standard = new(() => Load("controller.png"));
	private static readonly Lazy<Bitmap> Edge = new(() => Load("controller-edge.png"));

	// Image geometry (pixels of the asset): aspect ratio and where the player LEDs sit, under the touchpad.
	private const double ImageWidth = 2290;
	private const double ImageHeight = 1520;
	private const double LedCentreX = 1144;
	private const double LedY = 552;
	private const double LedSpacing = 34;
	private static readonly Color LightOff = Color.Parse("#3A3E44");
	private static readonly IBrush PadBrush = new SolidColorBrush(Color.Parse("#D4D9E2"));
	private static readonly IBrush EdgePadBrush = new SolidColorBrush(Color.Parse("#41444C"));
	private static readonly Lazy<(Geometry Pad, Geometry Band)> Shapes = new(BuildShapes);

	// The drawn touchpad in asset pixels (the input overlay outlines it on a touchpad click).
	internal static Geometry PadGeometry => Shapes.Value.Pad;

	static ControllerArt()
	{
		AffectsRender<ControllerArt>(IsEdgeProperty, LightColorProperty, LightIntensityProperty, PlayerLedsProperty, PlayerLedMaskProperty);
	}

	// The asset is larger than most places it is shown; smooth downscaling keeps the outlines clean.
	public ControllerArt() => RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);

	public bool IsEdge
	{
		get => GetValue(IsEdgeProperty);
		set => SetValue(IsEdgeProperty, value);
	}

	public Color LightColor
	{
		get => GetValue(LightColorProperty);
		set => SetValue(LightColorProperty, value);
	}

	// 0..1, e.g. the brightness setting.
	public double LightIntensity
	{
		get => GetValue(LightIntensityProperty);
		set => SetValue(LightIntensityProperty, value);
	}

	public int PlayerLedMask
	{
		get => GetValue(PlayerLedMaskProperty);
		set => SetValue(PlayerLedMaskProperty, value);
	}

	public bool PlayerLeds
	{
		get => GetValue(PlayerLedsProperty);
		set => SetValue(PlayerLedsProperty, value);
	}

	private static Bitmap Load(string name) => new(AssetLoader.Open(new Uri($"avares://Kopru.App/Assets/{name}")));

	protected override Size MeasureOverride(Size availableSize)
	{
		double w = double.IsInfinity(availableSize.Width)
			? (double.IsInfinity(availableSize.Height) ? ImageWidth / 6 : availableSize.Height * ImageWidth / ImageHeight)
			: availableSize.Width;
		double h = w * ImageHeight / ImageWidth;
		if (!double.IsInfinity(availableSize.Height) && h > availableSize.Height)
		{
			h = availableSize.Height;
			w = h * ImageWidth / ImageHeight;
		}
		return new Size(w, h);
	}

	public override void Render(DrawingContext context)
	{
		double scale = Math.Min(Bounds.Width / ImageWidth, Bounds.Height / ImageHeight);
		if (scale <= 0)
		{
			return;
		}
		var rect = new Rect((Bounds.Width - ImageWidth * scale) / 2, (Bounds.Height - ImageHeight * scale) / 2, ImageWidth * scale, ImageHeight * scale);
		context.DrawImage(IsEdge ? Edge.Value : Standard.Value, rect);

		// Touchpad and light strips as vectors over the bitmap, so their edges stay clean at every size; the light is the
		// chosen colour dimmed by brightness. No bloom.
		double intensity = Math.Clamp(LightIntensity, 0, 1);
		Color lit = Lerp(LightOff, LightColor, 0.25 + 0.75 * intensity);
		using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(rect.X, rect.Y)))
		{
			context.DrawGeometry(IsEdge ? EdgePadBrush : PadBrush, null, Shapes.Value.Pad);
			context.DrawGeometry(new SolidColorBrush(lit), null, Shapes.Value.Band);
		}

		// Player LEDs (the mask, or the middle one lit when enabled).
		int mask = PlayerLedMask;
		for (int i = 0; i < 5; i++)
		{
			bool on = mask >= 0 ? (mask >> i & 1) != 0 : PlayerLeds && i == 2;
			var led = new Rect(
				rect.X + (LedCentreX + (i - 2) * LedSpacing - 10) * scale,
				rect.Y + (LedY - 4) * scale,
				20 * scale,
				8 * scale);
			context.DrawRectangle(new SolidColorBrush(on ? Color.FromArgb(235, 255, 255, 255) : Color.FromArgb(60, 255, 255, 255)), null,
				new RoundedRect(led, 4 * scale));
		}
	}

	// The light strip runs down both sides of the touchpad and along its bottom: straight, slightly converging sides, a
	// rounded corner (a cubic) and a straight bottom, measured from the drawing and mirrored around the centre so both sides
	// match. The band is 21 px wide on the sides and 26 px at the bottom, plus 3 px outwards to cover the seam in the
	// bitmap; the touchpad is filled up to the band's centre line, with a rounded top.
	private static (Geometry Pad, Geometry Band) BuildShapes()
	{
		const double Centre = LedCentreX, Slope = 0.197, Top = 60, CornerStart = 400, CornerEndX = 880, Bottom = 527, Handle = 0.45;
		const double SideWidth = 21, BottomWidth = 26, Outward = 3;
		static double SideOffset(double y) => 456.5 - Slope * (y - 148);

		// Centre line of the left half, top to middle, with t = 0 on the sides and 1 along the bottom (for the width).
		List<(Point P, double T)> line = [];
		for (int i = 0; i < 39; i++)
		{
			double y = Top + (CornerStart - Top) * i / 39;
			line.Add((new Point(Centre - SideOffset(y), y), 0));
		}
		var p0 = new Point(Centre - SideOffset(CornerStart), CornerStart);
		var p3 = new Point(CornerEndX, Bottom);
		double tangentLength = Math.Sqrt(Slope * Slope + 1);
		double handle = Handle * Distance(p0, p3);
		var c1 = new Point(p0.X + Slope / tangentLength * handle, p0.Y + 1 / tangentLength * handle);
		var c2 = new Point(p3.X - handle, p3.Y);
		for (int i = 0; i < 39; i++)
		{
			double t = i / 39.0;
			line.Add((Cubic(p0, c1, c2, p3, t), t));
		}
		for (int i = 0; i < 20; i++)
		{
			line.Add((new Point(CornerEndX + (Centre - CornerEndX) * i / 19, Bottom), 1));
		}

		// The centre line moved along its normal (positive = away from the touchpad).
		List<Point> Offset(double sign)
		{
			List<Point> result = [];
			for (int i = 0; i < line.Count; i++)
			{
				Point a = line[Math.Max(0, i - 1)].P, b = line[Math.Min(line.Count - 1, i + 1)].P;
				double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
				double half = (SideWidth + (BottomWidth - SideWidth) * line[i].T) / 2 + (sign > 0 ? Outward : 0);
				result.Add(new Point(line[i].P.X + sign * dy / len * half, line[i].P.Y - sign * dx / len * half));
			}
			return result;
		}
		// Left half (top to middle) followed by its mirror image (middle to top).
		List<Point> Mirror(List<Point> half) => [.. half, .. Enumerable.Reverse(half).Select(p => new Point(2 * Centre - p.X, p.Y))];

		List<Point> outer = Mirror(Offset(1)), inner = Mirror(Offset(-1)), middle = Mirror(Offset(0));
		// The two top ends get round caps (a half circle bulging up along the strip) instead of a square cut.
		List<Point> Cap(Point from, Point to)
		{
			var mid = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2);
			double r = Distance(from, to) / 2;
			double a0 = Math.Atan2(from.Y - mid.Y, from.X - mid.X), a1 = Math.Atan2(to.Y - mid.Y, to.X - mid.X);
			// Of the two half circles between the ends, take the one through the top (smaller y).
			double sweep = Math.IEEERemainder(a1 - a0, 2 * Math.PI);
			if (Math.Sin(a0 + sweep / 2) > 0)
			{
				sweep -= Math.Sign(sweep) * 2 * Math.PI;
			}
			return [.. Enumerable.Range(1, 11).Select(i => new Point(mid.X + r * Math.Cos(a0 + sweep * i / 12), mid.Y + r * Math.Sin(a0 + sweep * i / 12)))];
		}
		Geometry band = Polygon([.. Cap(inner[0], outer[0]), .. outer, .. Cap(outer[^1], inner[^1]), .. Enumerable.Reverse(inner)]);

		// Touchpad: a rounded top-left corner, a gentle arc to the middle, mirrored, then down the band's centre line.
		Point corner = middle[0];
		var shoulder = new Point(corner.X + 34, 44);
		List<Point> topLeft = [];
		for (int i = 0; i <= 16; i++)
		{
			topLeft.Add(Cubic(corner, new Point(corner.X + 4, corner.Y - 14), new Point(corner.X + 14, 47), shoulder, i / 16.0));
		}
		for (int i = 1; i <= 16; i++)
		{
			topLeft.Add(Cubic(shoulder, new Point(900, 33), new Point(1000, 29), new Point(Centre, 29), i / 16.0));
		}
		List<Point> pad = [.. topLeft, .. Enumerable.Reverse(topLeft).Skip(1).Select(p => new Point(2 * Centre - p.X, p.Y)), .. Enumerable.Reverse(middle)];
		return (Polygon(pad), band);
	}

	private static Point Cubic(Point p0, Point c1, Point c2, Point p3, double t)
	{
		double u = 1 - t;
		return new Point(
			u * u * u * p0.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * p3.X,
			u * u * u * p0.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * p3.Y);
	}

	private static Geometry Polygon(IReadOnlyList<Point> points)
	{
		var geometry = new StreamGeometry();
		using (StreamGeometryContext g = geometry.Open())
		{
			g.BeginFigure(points[0], true);
			foreach (Point p in points.Skip(1))
			{
				g.LineTo(p);
			}
			g.EndFigure(true);
		}
		return geometry;
	}

	private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

	private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
		(byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
