// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kopru.App.Controls;

// A small line graph of recent values, oldest on the left: Values as the accent line over a faint fill, Secondary
// (optional) as a thin muted line, scaled to the larger of their maxima (never below MinScale), with a baseline.
public sealed class Sparkline : Control
{
	public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
		AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));
	public static readonly StyledProperty<IReadOnlyList<double>?> SecondaryProperty =
		AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Secondary));
	public static readonly StyledProperty<int> CapacityProperty = AvaloniaProperty.Register<Sparkline, int>(nameof(Capacity), 30);
	public static readonly StyledProperty<double> MinScaleProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(MinScale), 1);
	public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

	private static readonly IBrush Baseline = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
	private static readonly IBrush SecondaryStroke = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));

	static Sparkline()
	{
		AffectsRender<Sparkline>(ValuesProperty, SecondaryProperty, CapacityProperty, MinScaleProperty, StrokeProperty);
	}

	public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
	public IReadOnlyList<double>? Secondary { get => GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }
	// Samples across the full width (the graph fills from the right as samples arrive).
	public int Capacity { get => GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
	public double MinScale { get => GetValue(MinScaleProperty); set => SetValue(MinScaleProperty, value); }
	public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

	public override void Render(DrawingContext context)
	{
		double w = Bounds.Width, h = Bounds.Height;
		if (w < 4 || h < 4)
		{
			return;
		}
		context.DrawLine(new Pen(Baseline, 1), new Point(0, h - 0.5), new Point(w, h - 0.5));
		IReadOnlyList<double> main = Values ?? [];
		IReadOnlyList<double> second = Secondary ?? [];
		double max = Math.Max(MinScale, Math.Max(main.DefaultIfEmpty(0).Max(), second.DefaultIfEmpty(0).Max())) * 1.15;
		int capacity = Math.Max(2, Capacity);
		double step = w / (capacity - 1);
		Point At(IReadOnlyList<double> values, int i) =>
			new(w - (values.Count - 1 - i) * step, h - 1 - Math.Clamp(values[i] / max, 0, 1) * (h - 3));

		if (second.Count >= 2)
		{
			context.DrawGeometry(null, new Pen(SecondaryStroke, 1), Line(second.Count, i => At(second, i), false, h));
		}
		if (main.Count >= 2)
		{
			IBrush stroke = Stroke ?? Brushes.White;
			Color c = stroke is ISolidColorBrush s ? s.Color : Colors.White;
			var fill = new LinearGradientBrush
			{
				StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
				EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
				GradientStops = { new GradientStop(Color.FromArgb(70, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1) }
			};
			context.DrawGeometry(fill, null, Line(main.Count, i => At(main, i), true, h));
			context.DrawGeometry(null, new Pen(stroke, 1.6, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), Line(main.Count, i => At(main, i), false, h));
		}
	}

	private static StreamGeometry Line(int count, Func<int, Point> at, bool closed, double h)
	{
		var g = new StreamGeometry();
		using StreamGeometryContext ctx = g.Open();
		ctx.BeginFigure(at(0), closed);
		for (int i = 1; i < count; i++)
		{
			ctx.LineTo(at(i));
		}
		if (closed)
		{
			ctx.LineTo(new Point(at(count - 1).X, h));
			ctx.LineTo(new Point(at(0).X, h));
		}
		ctx.EndFigure(closed);
		return g;
	}
}
