// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Media;
using Kopru.Core.Input;

namespace Kopru.App.Controls;

// What a region looks like on the art; the overlay draws each kind slightly differently.
public enum RegionKind { Button, Bumper, Trigger, Stick, Touchpad }

// One control on the controller art: its outline (a closed polygon in the art's own pixels) and how it is drawn.
public sealed class ControllerRegion(PadButtons button, RegionKind kind, IReadOnlyList<Point> outline, bool edgeOnly = false)
{
	private Geometry? _geometry;

	public PadButtons Button { get; } = button;
	public RegionKind Kind { get; } = kind;
	public IReadOnlyList<Point> Outline { get; } = outline;
	public bool IsEdgeOnly { get; } = edgeOnly;
	public Rect Bounds { get; } = BoundsOf(outline);

	// Built on first use (needs the render platform), then shared by every overlay.
	public Geometry Geometry => _geometry ??= ControllerLayout.Polygon(Outline);

	public bool Contains(Point p)
	{
		bool inside = false;
		for (int i = 0, j = Outline.Count - 1; i < Outline.Count; j = i++)
		{
			Point a = Outline[i], b = Outline[j];
			if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
			{
				inside = !inside;
			}
		}
		return inside;
	}

	private static Rect BoundsOf(IReadOnlyList<Point> points)
	{
		double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
		foreach (Point p in points)
		{
			l = Math.Min(l, p.X);
			t = Math.Min(t, p.Y);
			r = Math.Max(r, p.X);
			b = Math.Max(b, p.Y);
		}
		return new Rect(l, t, r - l, b - t);
	}
}

// A thumbstick on the art: the cap at rest and the dark well it moves in.
public readonly record struct StickWell(PadButtons Button, Point Centre, double CapRadius, double WellRadius, double Travel);

// Where every control sits on the controller art (Assets/controller.png and controller-edge.png share the geometry),
// in the asset's own pixels (2290 x 1520). Measured from the drawing; tune here. The art is mirror-symmetric around
// x = 1145, so right-hand shapes are the left ones mirrored where the drawing agrees.
public static class ControllerLayout
{
	public const double ArtWidth = 2290;
	public const double ArtHeight = 1520;
	public const double MirrorX = 1145;

	// The touch sensor (1920 x 1080 reported) mapped onto the drawn touchpad.
	public static readonly Rect TouchSurface = new(712, 40, 864, 472);

	// Stick cap at rest: grey disc r 84 inside a dark well (inner edge of the black ring r ~130). The cap may move
	// Travel px in any direction so it stays inside the well.
	public static readonly StickWell LeftStick = new(PadButtons.L3, new Point(778, 743), 84, 129, 40);
	public static readonly StickWell RightStick = new(PadButtons.R3, new Point(1511, 743), 84, 129, 40);

	// The art's stick colours (well floor and cap) for the standard and the Edge drawing, sampled from the assets.
	public static readonly Color WellColor = Color.Parse("#252B31");
	public static readonly Color CapColor = Color.Parse("#454A51");
	public static readonly Color EdgeWellColor = Color.Parse("#121316");
	public static readonly Color EdgeCapColor = Color.Parse("#191A1E");

	private static readonly Point DpadCentre = new(436, 438);

	private static IReadOnlyList<ControllerRegion>? _regions;

	// Built on first use, after the shape tables below are initialised.
	public static IReadOnlyList<ControllerRegion> Regions => _regions ??= Build();

	public static ControllerRegion? Find(PadButtons button) => Regions.FirstOrDefault(r => r.Button == button);

	private static List<ControllerRegion> Build()
	{
		List<ControllerRegion> list =
		[
			// Face buttons: outer edge of the black rings.
			new(PadButtons.Triangle, RegionKind.Button, Circle(1846, 265, 74)),
			new(PadButtons.Square, RegionKind.Button, Circle(1702.5, 416, 74)),
			new(PadButtons.Circle, RegionKind.Button, Circle(1992, 416, 74)),
			new(PadButtons.Cross, RegionKind.Button, Circle(1846.5, 569, 74)),
			// D-pad: one arm shape, turned to each direction.
			new(PadButtons.DpadUp, RegionKind.Button, DpadArm(0, 197)),
			new(PadButtons.DpadRight, RegionKind.Button, DpadArm(90, 199)),
			new(PadButtons.DpadDown, RegionKind.Button, DpadArm(180, 202)),
			new(PadButtons.DpadLeft, RegionKind.Button, DpadArm(270, 198)),
			// Create / Options: slanted pills (tops lean towards the touchpad).
			new(PadButtons.Create, RegionKind.Button, Pill(615.5, 189, 30, 57, -10)),
			new(PadButtons.Options, RegionKind.Button, Pill(1671.5, 189, 30, 57, 10)),
			// Bumpers: the dark front face of the shoulder; the trigger is the lighter top face above it.
			new(PadButtons.L1, RegionKind.Bumper, BumperFace),
			new(PadButtons.R1, RegionKind.Bumper, Mirror(BumperFace)),
			new(PadButtons.L2, RegionKind.Trigger, TriggerFace),
			new(PadButtons.R2, RegionKind.Trigger, Mirror(TriggerFace)),
			// Sticks: the area the cap can cover.
			new(PadButtons.L3, RegionKind.Stick, Circle(LeftStick.Centre.X, LeftStick.Centre.Y, LeftStick.CapRadius + LeftStick.Travel)),
			new(PadButtons.R3, RegionKind.Stick, Circle(RightStick.Centre.X, RightStick.Centre.Y, RightStick.CapRadius + RightStick.Travel)),
			// The art has no PS logo: a round button between the sticks, where it sits on the controller.
			new(PadButtons.PS, RegionKind.Button, Circle(1144, 770, 34)),
			new(PadButtons.Mute, RegionKind.Button, Pill(1143.5, 871, 15, 56, 90)),
			new(PadButtons.Touchpad, RegionKind.Touchpad, RoundedRect(TouchSurface.Inflate(new Thickness(14, 8, 14, 10)), 40)),
			// Edge only, approximate: Fn buttons under the sticks; back paddles are not visible from the front.
			new(PadButtons.LeftFunction, RegionKind.Button, Pill(905, 940, 18, 40, 90), true),
			new(PadButtons.RightFunction, RegionKind.Button, Pill(1385, 940, 18, 40, 90), true),
			new(PadButtons.LeftPaddle, RegionKind.Button, Pill(380, 1230, 26, 70, 90), true),
			new(PadButtons.RightPaddle, RegionKind.Button, Pill(1910, 1230, 26, 70, 90), true),
		];
		return list;
	}

	// Left trigger's top face: the lighter band from its top edge down to where the bumper's darker front face begins.
	private static readonly Point[] TriggerFace =
	[
		new(285, 94), new(291, 80), new(297, 72), new(310, 61), new(330, 49), new(350, 40), new(370, 32), new(400, 22),
		new(430, 15), new(460, 10), new(490, 6), new(530, 6), new(550, 8), new(560, 11), new(570, 14), new(581, 19),
		new(570, 24), new(560, 27), new(550, 30), new(530, 34), new(500, 41), new(470, 47), new(440, 53), new(400, 61),
		new(370, 68), new(340, 73), new(310, 78), new(300, 78), new(292, 85),
	];

	// Left bumper's front face: from the trigger's lower edge down to the white body.
	private static readonly Point[] BumperFace =
	[
		new(292, 85), new(300, 78), new(310, 78), new(340, 73), new(370, 68), new(400, 61), new(440, 53), new(470, 47),
		new(500, 41), new(530, 34), new(550, 30), new(560, 27), new(570, 24), new(581, 19), new(585, 26), new(588, 39),
		new(590, 49), new(591, 63), new(580, 63), new(560, 64), new(540, 66), new(520, 68), new(500, 72), new(480, 75),
		new(460, 78), new(440, 82), new(420, 86), new(400, 90), new(380, 95), new(360, 99), new(340, 104), new(320, 108),
		new(300, 113), new(285, 115),
	];

	// The up arm's outer edge, measured as half-widths at heights (art y), from the rounded top to the 45-degree point
	// that faces the centre; other arms are this one turned around the d-pad centre.
	private static readonly (double Y, double Half)[] ArmProfile =
	[
		(241, 0), (242, 20), (244, 34.5), (248, 43), (252, 49), (260, 57), (268, 63), (276, 66), (284, 68), (292, 69),
		(308, 69), (324, 67.5), (340, 65.5), (348, 64), (356, 61.5), (364, 57.5), (372, 51.5), (380, 44), (390, 34),
		(400, 23.5), (408, 14.5), (413, 7), (416, 0),
	];

	private const double ArmTip = 22;

	private static Point[] DpadArm(double degrees, double reach)
	{
		List<Point> up = [];
		foreach ((double y, double half) in ArmProfile)
		{
			up.Add(new Point(DpadCentre.X + half, y));
		}
		for (int i = ArmProfile.Length - 2; i >= 1; i--)
		{
			up.Add(new Point(DpadCentre.X - ArmProfile[i].Half, ArmProfile[i].Y));
		}
		double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
		return [.. up.Select(p =>
		{
			// Stretched along the arm so its outer end reaches the drawn one (the arms differ by a few pixels); the inner
			// point stays 22 px from the centre.
			double dx = p.X - DpadCentre.X, dy = -(ArmTip + (DpadCentre.Y - p.Y - ArmTip) * (reach - ArmTip) / (DpadCentre.Y - ArmProfile[0].Y - ArmTip));
			return new Point(DpadCentre.X + dx * cos - dy * sin, DpadCentre.Y + dx * sin + dy * cos);
		})];
	}

	private static Point[] Circle(double cx, double cy, double r, int segments = 72) =>
		[.. Enumerable.Range(0, segments).Select(i => new Point(cx + r * Math.Cos(2 * Math.PI * i / segments), cy + r * Math.Sin(2 * Math.PI * i / segments)))];

	// A vertical pill (half width, half length) turned by degrees (clockwise on screen) around its centre.
	private static Point[] Pill(double cx, double cy, double halfWidth, double halfLength, double degrees)
	{
		List<Point> local = [];
		double straight = Math.Max(0, halfLength - halfWidth);
		for (int i = 0; i <= 24; i++)
		{
			double a = Math.PI + Math.PI * i / 24; // top cap, left to right
			local.Add(new Point(halfWidth * Math.Cos(a), -straight + halfWidth * Math.Sin(a)));
		}
		for (int i = 0; i <= 24; i++)
		{
			double a = Math.PI * i / 24; // bottom cap, right to left
			local.Add(new Point(halfWidth * Math.Cos(a), straight + halfWidth * Math.Sin(a)));
		}
		double t = degrees * Math.PI / 180, cos = Math.Cos(t), sin = Math.Sin(t);
		return [.. local.Select(p => new Point(cx + p.X * cos - p.Y * sin, cy + p.X * sin + p.Y * cos))];
	}

	private static Point[] RoundedRect(Rect r, double radius)
	{
		List<Point> points = [];
		void Corner(double cx, double cy, double from)
		{
			for (int i = 0; i <= 8; i++)
			{
				double a = (from + 90.0 * i / 8) * Math.PI / 180;
				points.Add(new Point(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)));
			}
		}
		Corner(r.Right - radius, r.Top + radius, 270);
		Corner(r.Right - radius, r.Bottom - radius, 0);
		Corner(r.Left + radius, r.Bottom - radius, 90);
		Corner(r.Left + radius, r.Top + radius, 180);
		return [.. points];
	}

	private static Point[] Mirror(Point[] points) => [.. points.Reverse().Select(p => new Point(2 * MirrorX - p.X, p.Y))];

	internal static Geometry Polygon(IReadOnlyList<Point> points)
	{
		var geometry = new StreamGeometry();
		using (StreamGeometryContext g = geometry.Open())
		{
			g.BeginFigure(points[0], true);
			for (int i = 1; i < points.Count; i++)
			{
				g.LineTo(points[i]);
			}
			g.EndFigure(true);
		}
		return geometry;
	}
}
