// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Kopru.App.Services;

namespace Kopru.App.Controls;

// Small chart of a trigger effect: resistance (up) over trigger travel (right).
public sealed class TriggerCurve : Control
{
	public static readonly StyledProperty<TriggerTestEffect> TriggerEffectProperty = AvaloniaProperty.Register<TriggerCurve, TriggerTestEffect>(nameof(TriggerEffect));
	public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<TriggerCurve, IBrush?>(nameof(Stroke));

	static TriggerCurve()
	{
		AffectsRender<TriggerCurve>(TriggerEffectProperty, StrokeProperty);
	}

	public TriggerTestEffect TriggerEffect
	{
		get => GetValue(TriggerEffectProperty);
		set => SetValue(TriggerEffectProperty, value);
	}

	public IBrush? Stroke
	{
		get => GetValue(StrokeProperty);
		set => SetValue(StrokeProperty, value);
	}

	// Resistance 0..1 at travel 0..1 (normalized shape, not exact firmware values).
	public static IReadOnlyList<Point> Shape(TriggerTestEffect effect)
	{
		switch (effect)
		{
			case TriggerTestEffect.Feedback:
				return [new(0, 0.04), new(0.3, 0.04), new(0.38, 0.62), new(1, 0.62)];
			case TriggerTestEffect.Weapon:
				return [new(0, 0.04), new(0.22, 0.04), new(0.62, 0.92), new(0.66, 0.08), new(1, 0.08)];
			case TriggerTestEffect.Vibration:
				var points = new List<Point> { new(0, 0.04), new(0.22, 0.04) };
				for (int i = 0; i < 10; i++)
				{
					points.Add(new(0.26 + i * 0.075, i % 2 == 0 ? 0.72 : 0.3));
				}
				points.Add(new(1, 0.5));
				return points;
			default:
				return [new(0, 0.04), new(1, 0.04)];
		}
	}

	public override void Render(DrawingContext context)
	{
		double w = Bounds.Width, h = Bounds.Height;
		if (w <= 0 || h <= 0)
		{
			return;
		}
		const double pad = 3;
		var grid = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1);
		for (int i = 1; i <= 4; i += 3)
		{
			double y = pad + (h - 2 * pad) * i / 4;
			context.DrawLine(grid, new Point(0, y), new Point(w, y));
		}

		IBrush stroke = Stroke ?? Brushes.White;
		IReadOnlyList<Point> shape = Shape(TriggerEffect);
		Point Map(Point p) => new(pad + p.X * (w - 2 * pad), h - pad - p.Y * (h - 2 * pad));

		var line = new StreamGeometry();
		using (StreamGeometryContext g = line.Open())
		{
			g.BeginFigure(Map(shape[0]), false);
			foreach (Point p in shape.Skip(1))
			{
				g.LineTo(Map(p));
			}
			g.EndFigure(false);
		}
		// Area under the curve: the stroke colour fading out towards the baseline.
		if (stroke is ISolidColorBrush solid)
		{
			Color c = solid.Color;
			var area = new StreamGeometry();
			using (StreamGeometryContext g = area.Open())
			{
				g.BeginFigure(new Point(Map(shape[0]).X, h - pad), true);
				foreach (Point p in shape)
				{
					g.LineTo(Map(p));
				}
				g.LineTo(new Point(Map(shape[^1]).X, h - pad));
				g.EndFigure(true);
			}
			var fill = new LinearGradientBrush
			{
				StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
				EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
				GradientStops = { new GradientStop(Color.FromArgb(70, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1) },
			};
			context.DrawGeometry(fill, null, area);
		}
		context.DrawGeometry(null, new Pen(stroke, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
	}
}
