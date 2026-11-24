// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Kopru.App.Controls;

// How one node of the chain is drawn: On = a filled tile with a bright icon; Off = a hollow tile with a dim icon.
public enum NodeLook { On, Off }

// How one link of the chain is drawn:
//   Solid   carrying through the app now: 2 px, accent
//   Quiet   connected, carrying nothing through the app (idle, or the cable going straight to the game): 1 px, neutral
//   Dashed  not there yet or lost: a dashed hairline
//   Chevron "then" between two steps (no line: a sequence, not a connection)
public enum LinkLook { Solid, Quiet, Dashed, Chevron }

public sealed record ChainNode(Geometry Icon, string Name, string? State, NodeLook Look);

public sealed record ChainLink(LinkLook Look, Geometry? Icon = null);

// Controller -> app (the virtual controller) -> game. CableCaption: when set, a quiet lane runs over the top from the
// controller straight to the game (the USB cable, bypassing the app) with this caption.
public sealed record ChainSpec(ChainNode Left, ChainLink First, ChainNode Middle, ChainLink Second, ChainNode Right, string? CableCaption = null);

// The small diagram of the Overview's live card while there are no live numbers: three icon tiles in a row with a name
// and a state word under each, joined by link lines (solid = in use, dashed hairline = not carrying) or chevrons (steps).
// Stretches to the width it gets: the outer tiles sit on the edges, the middle one in the centre.
public sealed class ChainDiagram : Control
{
	public static readonly StyledProperty<ChainSpec?> SpecProperty = AvaloniaProperty.Register<ChainDiagram, ChainSpec?>(nameof(Spec));
	public static readonly StyledProperty<double> TileProperty = AvaloniaProperty.Register<ChainDiagram, double>(nameof(Tile), 36);

	private const double CableZone = 30;   // room above the tiles for the cable lane
	private const double CableY = 10;      // the cable lane's line inside that room
	private const double LinkGap = 6;      // between a tile and a line that ends on it
	private const double LabelGap = 7;     // between the tiles and their names
	private const double NameLine = 16;
	private const double StateLine = 15;
	private const double TextSize = 12;

	static ChainDiagram()
	{
		AffectsRender<ChainDiagram>(SpecProperty, TileProperty);
		AffectsMeasure<ChainDiagram>(SpecProperty, TileProperty);
	}

	public ChainSpec? Spec { get => GetValue(SpecProperty); set => SetValue(SpecProperty, value); }
	public double Tile { get => GetValue(TileProperty); set => SetValue(TileProperty, value); }

	protected override Size MeasureOverride(Size availableSize)
	{
		if (Spec is not { } s)
		{
			return default;
		}
		bool states = s.Left.State != null || s.Middle.State != null || s.Right.State != null;
		double height = (s.CableCaption != null ? CableZone : 0) + Tile + LabelGap + NameLine + (states ? StateLine : 0);
		return new Size(double.IsInfinity(availableSize.Width) ? 316 : availableSize.Width, height);
	}

	public override void Render(DrawingContext context)
	{
		if (Spec is not { } s)
		{
			return;
		}
		double w = Bounds.Width, t = Tile;
		double top = s.CableCaption != null ? CableZone : 0;
		double cy = top + t / 2;
		Rect left = new(0, top, t, t);
		Rect middle = new(Math.Round((w - t) / 2), top, t, t);
		Rect right = new(w - t, top, t, t);
		DrawLink(context, s.First, left.Right + LinkGap, middle.X - LinkGap, cy);
		DrawLink(context, s.Second, middle.Right + LinkGap, right.X - LinkGap, cy);
		if (s.CableCaption is { } caption)
		{
			DrawCable(context, caption, left.Center.X, right.Center.X, top - LinkGap);
		}
		DrawNode(context, s.Left, left, HorizontalAlignment.Left, w);
		DrawNode(context, s.Middle, middle, HorizontalAlignment.Center, w);
		DrawNode(context, s.Right, right, HorizontalAlignment.Right, w);
	}

	private static IBrush Brush(string key) => LinkLine.Res(key);

	private static Pen PenFor(LinkLook look) => look switch
	{
		LinkLook.Solid => new Pen(Brush("AccentBrush"), 2, lineJoin: PenLineJoin.Round),
		LinkLook.Quiet => new Pen(Brush("TextSecondaryBrush"), 1, lineJoin: PenLineJoin.Round),
		_ => new Pen(Brush("MutedBrush"), 1, new DashStyle([4, 3], 0), lineJoin: PenLineJoin.Round)
	};

	// A 1 px line sits on the half pixel so it stays one crisp row; the 2 px line straddles the boundary.
	private static double Snap(LinkLook look) => look == LinkLook.Solid ? 0 : 0.5;

	private static string IconTone(LinkLook look) => look switch
	{
		LinkLook.Solid => "TextBrush",
		LinkLook.Quiet => "TextSecondaryBrush",
		_ => "MutedBrush"
	};

	private static void DrawLink(DrawingContext context, ChainLink link, double x0, double x1, double y)
	{
		double mid = Math.Round((x0 + x1) / 2);
		if (link.Look == LinkLook.Chevron)
		{
			DrawIcon(context, IconPaths.ChevronRight, new Point(mid, y), 16, Brush("MutedBrush"));
			return;
		}
		Pen pen = PenFor(link.Look);
		double ly = y + Snap(link.Look);
		if (link.Icon is { } icon)
		{
			// The link's icon sits on the line, in a gap of it.
			const double half = 13;
			context.DrawLine(pen, new Point(x0, ly), new Point(mid - half, ly));
			context.DrawLine(pen, new Point(mid + half, ly), new Point(x1, ly));
			DrawIcon(context, icon, new Point(mid, y), 16, Brush(IconTone(link.Look)));
		}
		else
		{
			context.DrawLine(pen, new Point(x0, ly), new Point(x1, ly));
		}
	}

	// The cable lane: up from the controller, across the top, down into the game, without touching the app's tile; its
	// icon and caption sit on the line.
	private void DrawCable(DrawingContext context, string caption, double x0, double x1, double bottom)
	{
		const double radius = 8, iconSize = 16, pad = 8;
		const LinkLook look = LinkLook.Quiet;
		double o = Snap(look);
		double y = CableY + o;
		double xa = x0 + o, xb = x1 + o;
		FormattedText text = Label(caption, Brush("TextSecondaryBrush"), FontWeight.Normal);
		double group = iconSize + 5 + text.Width;
		double gx = Math.Round((x0 + x1) / 2 - group / 2);
		var path = new StreamGeometry();
		using (StreamGeometryContext g = path.Open())
		{
			g.BeginFigure(new Point(xa, bottom), false);
			g.LineTo(new Point(xa, y + radius));
			g.ArcTo(new Point(xa + radius, y), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
			g.LineTo(new Point(gx - pad, y));
			g.EndFigure(false);
			g.BeginFigure(new Point(gx + group + pad, y), false);
			g.LineTo(new Point(xb - radius, y));
			g.ArcTo(new Point(xb, y + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
			g.LineTo(new Point(xb, bottom));
			g.EndFigure(false);
		}
		context.DrawGeometry(null, PenFor(look), path);
		DrawIcon(context, IconPaths.Cable, new Point(gx + iconSize / 2, CableY), iconSize, Brush(IconTone(look)));
		context.DrawText(text, new Point(gx + iconSize + 5, Math.Round(CableY - text.Height / 2)));
	}

	// A node: a tile (Surface2Brush, hairline edge, radius 10); hollow with a dim icon when off.
	private void DrawNode(DrawingContext context, ChainNode node, Rect tile, HorizontalAlignment align, double width)
	{
		bool on = node.Look == NodeLook.On;
		context.DrawRectangle(on ? Brush("Surface2Brush") : null, new Pen(Brush(on ? "BorderBrush" : "HairlineBrush"), 1), tile.Deflate(0.5), 10, 10);
		DrawIcon(context, node.Icon, tile.Center, 20, Brush(on ? "TextBrush" : "IndicatorOffBrush"));

		double y = tile.Bottom + LabelGap;
		FormattedText name = Label(node.Name, Brush(on ? "TextBrush" : "TextSecondaryBrush"), FontWeight.Medium);
		context.DrawText(name, new Point(TextX(name, tile, align, width), y));
		if (node.State is { } state)
		{
			FormattedText word = Label(state, Brush("MutedBrush"), FontWeight.Normal);
			context.DrawText(word, new Point(TextX(word, tile, align, width), y + NameLine));
		}
	}

	private static double TextX(FormattedText text, Rect tile, HorizontalAlignment align, double width) => align switch
	{
		HorizontalAlignment.Left => 0,
		HorizontalAlignment.Right => Math.Round(width - text.Width),
		_ => Math.Round(tile.Center.X - text.Width / 2)
	};

	private FormattedText Label(string text, IBrush brush, FontWeight weight) =>
		new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
			new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, weight), TextSize, brush);

	// An IconPaths geometry (24 grid, stroke) centred on a point, like IconSlot draws it.
	private static void DrawIcon(DrawingContext context, Geometry icon, Point centre, double size, IBrush brush)
	{
		double scale = size / IconSlot.Grid;
		var pen = new Pen(brush, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
		using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(centre.X - size / 2, centre.Y - size / 2)))
		{
			context.DrawGeometry(null, pen, icon);
		}
	}
}
