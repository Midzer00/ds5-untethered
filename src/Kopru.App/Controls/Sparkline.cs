// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kopru.App.Controls;

// A small line graph of recent values, oldest on the left: Values as the accent line over a faint flat fill, Secondary
// (optional) as a thin muted line, with a baseline.
// Scale: with Maximum set, a fixed 0..Maximum axis labelled on the right ("0", the limit, "Maximum Unit"), so the height of
// the line means the same thing at every moment; without it, the larger of the series' maxima (never below MinScale).
// Limit (optional): a dashed line with LimitLabel above it; every sample of Values above it gets a bar in LimitBrush from
// the line up to the value (to the top edge when the value is off the scale), so spikes stand out without reading numbers.
public sealed class Sparkline : Control
{
	public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
		AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));
	public static readonly StyledProperty<IReadOnlyList<double>?> SecondaryProperty =
		AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Secondary));
	public static readonly StyledProperty<int> CapacityProperty = AvaloniaProperty.Register<Sparkline, int>(nameof(Capacity), 30);
	public static readonly StyledProperty<double> MinScaleProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(MinScale), 1);
	public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum));
	public static readonly StyledProperty<double> LimitProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Limit));
	public static readonly StyledProperty<string?> LimitLabelProperty = AvaloniaProperty.Register<Sparkline, string?>(nameof(LimitLabel));
	public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<Sparkline, string?>(nameof(Unit));
	public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));
	public static readonly StyledProperty<IBrush?> LimitBrushProperty = AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(LimitBrush));
	public static readonly StyledProperty<IBrush?> LabelBrushProperty = AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(LabelBrush));

	// Axis and limit labels: small, in the label gutter on the right.
	public const double LabelSize = 10.5;
	public const double LabelGutter = 30;

	private static readonly IBrush Baseline = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
	private static readonly IBrush SecondaryStroke = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
	private static readonly IBrush LabelPlate = new SolidColorBrush(Color.FromArgb(200, 0x1F, 0x21, 0x25));

	static Sparkline()
	{
		AffectsRender<Sparkline>(ValuesProperty, SecondaryProperty, CapacityProperty, MinScaleProperty, MaximumProperty, LimitProperty,
			LimitLabelProperty, UnitProperty, StrokeProperty, LimitBrushProperty, LabelBrushProperty);
	}

	public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
	public IReadOnlyList<double>? Secondary { get => GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }
	// Samples across the full width (the graph fills from the right as samples arrive).
	public int Capacity { get => GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
	public double MinScale { get => GetValue(MinScaleProperty); set => SetValue(MinScaleProperty, value); }
	// A fixed top of the scale (0: scale to the data).
	public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
	// The "you can feel it" line (0: none).
	public double Limit { get => GetValue(LimitProperty); set => SetValue(LimitProperty, value); }
	public string? LimitLabel { get => GetValue(LimitLabelProperty); set => SetValue(LimitLabelProperty, value); }
	public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
	public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
	public IBrush? LimitBrush { get => GetValue(LimitBrushProperty); set => SetValue(LimitBrushProperty, value); }
	public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

	public bool HasAxis => Maximum > 0;

	// Where the plot area ends on the right (the label gutter follows when the axis is labelled).
	public double PlotWidth => Math.Max(0, Bounds.Width - (HasAxis ? LabelGutter : 0));

	// The vertical position of a value in the plot area (top padding leaves room for the top label).
	public double YOf(double value)
	{
		(double top, double bottom) = PlotRows();
		double max = HasAxis ? Maximum : ScaleTop(MinScale, Values ?? [], Secondary ?? []);
		return bottom - Math.Clamp(value / max, 0, 1) * (bottom - top);
	}

	// The samples drawn above the limit (spike bars): their indices in Values.
	public static IReadOnlyList<int> SpikesAbove(IReadOnlyList<double> values, double limit) =>
		limit <= 0 ? [] : Enumerable.Range(0, values.Count).Where(i => values[i] > limit).ToList();

	public override void Render(DrawingContext context)
	{
		double w = PlotWidth, h = Bounds.Height;
		if (w < 4 || h < 4)
		{
			return;
		}
		(double top, double bottom) = PlotRows();
		context.DrawLine(new Pen(Baseline, 1), new Point(0, bottom + 0.5), new Point(w, bottom + 0.5));
		IReadOnlyList<double> main = Values ?? [];
		IReadOnlyList<double> second = Secondary ?? [];
		int capacity = Math.Max(2, Capacity);
		double step = w / (capacity - 1);
		Point At(IReadOnlyList<double> values, int i) => new(w - (values.Count - 1 - i) * step, YOf(values[i]));
		IBrush limitBrush = LimitBrush ?? Brushes.IndianRed;

		if (Limit > 0)
		{
			double y = Math.Round(YOf(Limit)) + 0.5;
			var dashed = new Pen(limitBrush, 1, new DashStyle([3, 3], 0));
			context.DrawLine(dashed, new Point(0, y), new Point(w, y));
			// Spikes: a bar from the line up to the value (or the top edge).
			Color lc = limitBrush is ISolidColorBrush ls ? ls.Color : Colors.IndianRed;
			IBrush bar = new SolidColorBrush(Color.FromArgb(150, lc.R, lc.G, lc.B));
			double width = Math.Clamp(step * 0.5, 2, 6);
			foreach (int i in SpikesAbove(main, Limit))
			{
				double x = At(main, i).X;
				double yTop = YOf(main[i]);
				context.FillRectangle(bar, new Rect(Math.Clamp(x - width / 2, 0, w - width), yTop, width, Math.Max(1, y - yTop)));
			}
		}

		if (second.Count >= 2)
		{
			context.DrawGeometry(null, new Pen(SecondaryStroke, 1), Line(second.Count, i => At(second, i), false, bottom));
		}
		if (main.Count >= 2)
		{
			IBrush stroke = Stroke ?? Brushes.White;
			Color c = stroke is ISolidColorBrush s ? s.Color : Colors.White;
			var fill = new SolidColorBrush(Color.FromArgb(36, c.R, c.G, c.B));
			context.DrawGeometry(fill, null, Line(main.Count, i => At(main, i), true, bottom));
			context.DrawGeometry(null, new Pen(stroke, 1.6, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), Line(main.Count, i => At(main, i), false, bottom));
		}

		// The limit's name last, on a small dark plate, so the line, its fill and the spike bars never cover it.
		if (Limit > 0 && !string.IsNullOrEmpty(LimitLabel))
		{
			FormattedText text = Label(LimitLabel!, limitBrush);
			var at = new Point(Math.Max(0, w - text.Width - 4), Math.Max(0, Math.Round(YOf(Limit)) - text.Height - 2));
			context.FillRectangle(LabelPlate, new Rect(at.X - 3, at.Y, text.Width + 6, text.Height + 1), 3);
			context.DrawText(text, at);
		}

		if (HasAxis)
		{
			IBrush labels = LabelBrush ?? SecondaryStroke;
			void AxisLabel(string s, double y)
			{
				FormattedText t = Label(s, labels);
				context.DrawText(t, new Point(w + 6, Math.Clamp(y - t.Height / 2, 0, h - t.Height)));
			}
			AxisLabel(Format(Maximum) + (string.IsNullOrEmpty(Unit) ? string.Empty : " " + Unit), top);
			if (Limit > 0 && Limit < Maximum)
			{
				AxisLabel(Format(Limit), YOf(Limit));
			}
			AxisLabel("0", bottom);
		}
	}

	// The value drawn at the top edge without a fixed Maximum: the highest sample with 15 % headroom, but never below
	// minScale, so small jitter on a low line stays a flat line instead of being stretched over the whole height.
	public static double ScaleTop(double minScale, params IReadOnlyList<double>[] series)
	{
		double peak = series.Select(s => s.DefaultIfEmpty(0).Max()).DefaultIfEmpty(0).Max();
		return Math.Max(Math.Max(minScale, 0.001), peak * 1.15);
	}

	private (double Top, double Bottom) PlotRows()
	{
		double h = Bounds.Height;
		// With labels, half a label of room above the top and below the baseline so they are not cut off.
		double pad = HasAxis ? Math.Ceiling(LabelSize * 0.7) : 1;
		return (HasAxis ? pad : 2, h - pad);
	}

	private static string Format(double v) => v.ToString("0.#", CultureInfo.CurrentCulture);

	private static FormattedText Label(string text, IBrush brush) =>
		new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, LabelSize, brush);

	private static StreamGeometry Line(int count, Func<int, Point> at, bool closed, double bottom)
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
			ctx.LineTo(new Point(at(count - 1).X, bottom));
			ctx.LineTo(new Point(at(0).X, bottom));
		}
		ctx.EndFigure(closed);
		return g;
	}
}
