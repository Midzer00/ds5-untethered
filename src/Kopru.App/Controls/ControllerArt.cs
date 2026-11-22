// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Kopru.App.Controls;

// Front view of the controller (Assets/controller.png, or controller-edge.png for the Edge), scaled to fit. The pictures
// are renders of a model built from the same geometry (tools/art/controller). The light strip is lit live: LightColor is
// painted through three masks rendered with the art (the diffuser channel, its bright middle, and the light it throws on
// the shell and touchpad around it; LightTint has them in colour), stronger with LightIntensity and with the colour's own brightness, so breathing and
// the other effects look like the strip glowing. The player LEDs under the touchpad glow softly on top. No logos.
// Look picks the pictures (PadArt): the default art, a colour variant from the built-in pack (half resolution, drawn into
// the same art box, so the light, LEDs and stick sprites line up the same) or the user's own picture, drawn fitted in the
// art box with nothing on it (it is an arbitrary image: no light strip, LEDs or input).
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
	// The player LEDs' brightness 0..1 (the controller's low / medium / high).
	public static readonly StyledProperty<double> PlayerLedLevelProperty = AvaloniaProperty.Register<ControllerArt, double>(nameof(PlayerLedLevel), 1.0);
	// Null: the default art.
	public static readonly StyledProperty<PadLook?> LookProperty = AvaloniaProperty.Register<ControllerArt, PadLook?>(nameof(Look));

	private static readonly Lazy<Bitmap> Standard = new(() => Load("controller.png"));
	private static readonly Lazy<Bitmap> Edge = new(() => Load("controller-edge.png"));
	// Rendered with the art for drawing a moved stick: each stick's surroundings without its cap, and the cap alone.
	private static readonly Lazy<Bitmap>[] Wells =
	[
		new(() => Load("controller-well-left.png")), new(() => Load("controller-well-right.png")),
		new(() => Load("controller-edge-well-left.png")), new(() => Load("controller-edge-well-right.png")),
	];
	private static readonly Lazy<Bitmap> StandardCap = new(() => Load("controller-cap.png"));
	private static readonly Lazy<Bitmap> EdgeCap = new(() => Load("controller-edge-cap.png"));
	private static readonly IImmutableBrush LedGlow = new RadialGradientBrush
	{
		GradientStops = { new GradientStop(Color.FromArgb(150, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
	}.ToImmutable();

	// An own picture's corner radius (the app's RadiusSmall).
	private const double OwnCornerRadius = 8;

	// Image geometry (pixels of the asset): aspect ratio and where the player LEDs sit, under the touchpad.
	private const double ImageWidth = 2290;
	private const double ImageHeight = 1520;
	private const double LedCentreX = 1144;
	private const double LedY = 552;
	private const double LedSpacing = 34;
	private static readonly Lazy<(Geometry Pad, Geometry Band)> Shapes = new(BuildShapes);

	// The touchpad in asset pixels, as modelled in the render (the input overlay outlines it on a touchpad click).
	internal static Geometry PadGeometry => Shapes.Value.Pad;

	// The light strip in asset pixels (where the light masks may light up, with their glow round it).
	internal static Geometry BandGeometry => Shapes.Value.Band;

	static ControllerArt()
	{
		AffectsRender<ControllerArt>(IsEdgeProperty, LightColorProperty, LightIntensityProperty, PlayerLedsProperty, PlayerLedMaskProperty, PlayerLedLevelProperty,
			LookProperty);
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

	public double PlayerLedLevel
	{
		get => GetValue(PlayerLedLevelProperty);
		set => SetValue(PlayerLedLevelProperty, value);
	}

	public bool PlayerLeds
	{
		get => GetValue(PlayerLedsProperty);
		set => SetValue(PlayerLedsProperty, value);
	}

	public PadLook? Look
	{
		get => GetValue(LookProperty);
		set => SetValue(LookProperty, value);
	}

	public static Bitmap ArtBitmap(bool edge) => (edge ? Edge : Standard).Value;

	public static Bitmap StickWellSprite(bool edge, bool left) => Wells[(edge ? 2 : 0) + (left ? 0 : 1)].Value;

	public static Bitmap StickCapSprite(bool edge) => (edge ? EdgeCap : StandardCap).Value;

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
		PadArtSet art = PadArt.Resolve(Look, IsEdge);
		if (art.IsOwn)
		{
			// The user's picture, fitted (uniform) and centred in the art box, alone, with rounded corners (RadiusSmall, less on
			// a small drawing such as the sidebar card's).
			Size size = art.Body.Size;
			double fit = Math.Min(rect.Width / size.Width, rect.Height / size.Height);
			if (fit > 0)
			{
				var picture = new Rect(rect.X + (rect.Width - size.Width * fit) / 2, rect.Y + (rect.Height - size.Height * fit) / 2,
					size.Width * fit, size.Height * fit);
				double radius = Math.Min(OwnCornerRadius, Math.Min(picture.Width, picture.Height) * 0.12);
				using (context.PushClip(new RoundedRect(picture, radius)))
				{
					context.DrawImage(art.Body, picture);
				}
			}
			return;
		}
		context.DrawImage(art.Body, rect);

		// The light: the colour at full brightness (its own brightness, e.g. breathing, and LightIntensity set how strongly
		// it shows), through the masks: the glow on the surroundings, the diffuser, its hot middle.
		Color colour = LightColor;
		double peak = Math.Max(colour.R, Math.Max(colour.G, colour.B)) / 255.0;
		Color hue = peak > 0 ? Color.FromRgb((byte)(colour.R / peak), (byte)(colour.G / peak), (byte)(colour.B / peak)) : Colors.Black;
		double level = peak * (0.2 + 0.8 * Math.Clamp(LightIntensity, 0, 1));
		bool edge = IsEdge;
		using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(rect.X, rect.Y)))
		{
			Rect light = ControllerLayout.LightRect;
			Paint(context, edge, LightTint.Glow, light, hue, Math.Min(1, 0.95 * level));
			Paint(context, edge, LightTint.Strip, light, hue, Math.Min(1, 0.25 * Math.Min(1, peak * 8) + 0.75 * level));
			Paint(context, edge, LightTint.Core, light, Lerp(hue, Colors.White, 0.55), 0.85 * Math.Pow(level, 1.2));

			// Player LEDs (the mask, or the middle one lit when enabled): lit ones glow softly, unlit ones are faint slots.
			int mask = PlayerLedMask;
			double ledLevel = Math.Clamp(PlayerLedLevel, 0, 1);
			for (int i = 0; i < 5; i++)
			{
				bool on = mask >= 0 ? (mask >> i & 1) != 0 : PlayerLeds && i == 2;
				var centre = new Point(LedCentreX + (i - 2) * LedSpacing, LedY);
				if (on)
				{
					using (context.PushOpacity(ledLevel))
					{
						context.DrawEllipse(LedGlow, null, centre, 24, 15);
					}
				}
				byte alpha = on ? (byte)(46 + 194 * ledLevel) : (byte)46;
				context.DrawRectangle(new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)), null,
					new RoundedRect(new Rect(centre.X - 10, centre.Y - 4, 20, 8), 4));
			}
		}
	}

	// A light mask in colour over rect, at opacity (drawn like the art: a bitmap into a rectangle).
	private static void Paint(DrawingContext context, bool edge, int part, Rect rect, Color colour, double opacity)
	{
		if (opacity <= 0.004 || colour == Colors.Black)
		{
			return;
		}
		Bitmap tinted = LightTint.Get(edge, part, colour);
		using (context.PushOpacity(opacity))
		{
			context.DrawImage(tinted, new Rect(tinted.Size), rect);
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
