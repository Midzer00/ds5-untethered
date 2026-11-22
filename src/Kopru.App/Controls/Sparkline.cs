// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Kopru.App.Controls;

// A small area chart of recent values, oldest on the left, filling from the right as samples arrive: Values as a line in
// Stroke over a flat translucent fill of the same colour down to the baseline, Secondary (optional) as a thin faint line.
// A NaN value leaves a gap. Marks (optional, aligned with Values from the right) are small muted ticks under the baseline.
// Scale: with Maximum set, a 0..Maximum axis with its value small inside the plot, top right ("Maximum Unit"); without
// it, the larger of the series' maxima (never below MinScale) and no labels.
// Limit (optional): a faint dashed line in LimitBrush; nothing is drawn for a limit above Maximum.
// Pens and brushes are made when their colour changes, not on every frame; each series is one StreamGeometry.
public sealed class Sparkline : Control
{
	public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
		AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));
	public static readonly StyledProperty<IReadOnlyList<double>?> SecondaryProperty =
		AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Secondary));
	public static readonly StyledProperty<IReadOnlyList<bool>?> MarksProperty =
		AvaloniaProperty.Register<Sparkline, IReadOnlyList<bool>?>(nameof(Marks));
	public static readonly StyledProperty<int> CapacityProperty = AvaloniaProperty.Register<Sparkline, int>(nameof(Capacity), 60);
	public static readonly StyledProperty<double> MinScaleProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(MinScale), 1);
	public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum));
	public static readonly StyledProperty<double> LimitProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Limit));
	public static readonly StyledProperty<string?> UnitProperty = AvaloniaProperty.Register<Sparkline, string?>(nameof(Unit));
	public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));
	public static readonly StyledProperty<IBrush?> LimitBrushProperty = AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(LimitBrush));
	public static readonly StyledProperty<IBrush?> LabelBrushProperty = AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(LabelBrush));

	// The axis label: small, inside the plot at its top right (no gutter: the plot uses the full width).
	public const double LabelSize = 10.5;
	public const double LabelGutter = 0;
	// The fill's opacity over the line's colour, and the loss ticks' size.
	public const byte FillAlpha = 46;
	public const double MarkHeight = 4;

	private static readonly IBrush Baseline = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));
	private static readonly IBrush SecondaryStroke = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255));
	private static readonly IBrush MarkBrush = new SolidColorBrush(Color.FromArgb(150, 0x8A, 0x90, 0x9A));
	private static readonly IPen BaselinePen = new ImmutablePen(Baseline.ToImmutable(), 1);
	private static readonly IPen SecondaryPen = new ImmutablePen(SecondaryStroke.ToImmutable(), 1, lineJoin: PenLineJoin.Round);
	private static readonly IPen MarkPen = new ImmutablePen(MarkBrush.ToImmutable(), 1.5);

	private IPen? _linePen;
	private IBrush? _fill;
	private IPen? _limitPen;
	private FormattedText? _topLabel;

	static Sparkline()
	{
		AffectsRender<Sparkline>(ValuesProperty, SecondaryProperty, MarksProperty, CapacityProperty, MinScaleProperty, MaximumProperty, LimitProperty,
			UnitProperty, StrokeProperty, LimitBrushProperty, LabelBrushProperty);
	}

	public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
	public IReadOnlyList<double>? Secondary { get => GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }
	public IReadOnlyList<bool>? Marks { get => GetValue(MarksProperty); set => SetValue(MarksProperty, value); }
	// Samples across the full width.
	public int Capacity { get => GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
	public double MinScale { get => GetValue(MinScaleProperty); set => SetValue(MinScaleProperty, value); }
	// A fixed top of the scale (0: scale to the data).
	public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
	// The "you can feel it" line (0: none).
	public double Limit { get => GetValue(LimitProperty); set => SetValue(LimitProperty, value); }
	public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
	public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
	public IBrush? LimitBrush { get => GetValue(LimitBrushProperty); set => SetValue(LimitBrushProperty, value); }
	public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

	public bool HasAxis => Maximum > 0;

	// Where the plot area ends on the right (LabelGutter less than the width when the axis is labelled).
	public double PlotWidth => Math.Max(0, Bounds.Width - (HasAxis ? LabelGutter : 0));

	// The vertical position of a value in the plot area (top padding leaves room for the top label).
	public double YOf(double value)
	{
		(double top, double bottom) = PlotRows();
		double max = HasAxis ? Maximum : ScaleTop(MinScale, Values ?? [], Secondary ?? []);
		return bottom - Math.Clamp(value / max, 0, 1) * (bottom - top);
	}

	// The x of sample i of a series of count samples (the newest at the right edge).
	public double XOf(int i, int count) => PlotWidth - (count - 1 - i) * PlotWidth / (Math.Max(2, Capacity) - 1);

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == StrokeProperty)
		{
			_linePen = null;
			_fill = null;
		}
		else if (change.Property == LimitBrushProperty)
		{
			_limitPen = null;
		}
		else if (change.Property == MaximumProperty || change.Property == UnitProperty || change.Property == LabelBrushProperty)
		{
			_topLabel = null;
		}
	}

	public override void Render(DrawingContext context)
	{
		double w = PlotWidth, h = Bounds.Height;
		if (w < 4 || h < 4)
		{
			return;
		}
		(double top, double bottom) = PlotRows();
		IReadOnlyList<double> main = Values ?? [];
		IReadOnlyList<double> second = Secondary ?? [];

		context.DrawLine(BaselinePen, new Point(0, bottom + 0.5), new Point(w, bottom + 0.5));
		if (Limit > 0 && (!HasAxis || Limit <= Maximum))
		{
			double y = Math.Round(YOf(Limit)) + 0.5;
			_limitPen ??= new ImmutablePen((LimitBrush ?? SecondaryStroke).ToImmutable(), 1, new ImmutableDashStyle([2, 4], 0));
			context.DrawLine(_limitPen, new Point(0, y), new Point(w, y));
		}

		if (main.Count >= 2)
		{
			Color c = Stroke is ISolidColorBrush s ? s.Color : Colors.White;
			_fill ??= new ImmutableSolidColorBrush(Color.FromArgb(FillAlpha, c.R, c.G, c.B));
			_linePen ??= new ImmutablePen((Stroke ?? Brushes.White).ToImmutable(), 1.6, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
			context.DrawGeometry(_fill, null, Area(main, bottom));
			context.DrawGeometry(null, _linePen, Line(main));
		}
		if (second.Count >= 2)
		{
			context.DrawGeometry(null, SecondaryPen, Line(second));
		}
		if (Marks is { Count: > 0 } marks)
		{
			for (int i = 0; i < marks.Count; i++)
			{
				if (marks[i])
				{
					double x = Math.Round(Math.Clamp(XOf(i, marks.Count), 1, w - 1)) + 0.5;
					context.DrawLine(MarkPen, new Point(x, bottom + 2), new Point(x, bottom + 2 + MarkHeight));
				}
			}
		}

		if (HasAxis)
		{
			_topLabel ??= new FormattedText(Format(Maximum) + (string.IsNullOrEmpty(Unit) ? string.Empty : " " + Unit), CultureInfo.CurrentCulture,
				FlowDirection.LeftToRight, Typeface.Default, LabelSize, LabelBrush ?? SecondaryStroke);
			context.DrawText(_topLabel, new Point(Math.Max(0, w - _topLabel.Width), Math.Clamp(top + 3, 0, Math.Max(0, h - _topLabel.Height))));
		}
	}

	// The value drawn at the top edge without a fixed Maximum: the highest sample with 15 % headroom, but never below
	// minScale, so small jitter on a low line stays a flat line instead of being stretched over the whole height.
	public static double ScaleTop(double minScale, params IReadOnlyList<double>[] series)
	{
		double peak = series.Select(s => s.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max()).DefaultIfEmpty(0).Max();
		return Math.Max(Math.Max(minScale, 0.001), peak * 1.15);
	}

	private (double Top, double Bottom) PlotRows()
	{
		double h = Bounds.Height;
		// Half a label of room above the top; room for the ticks under the baseline.
		double topPad = HasAxis ? Math.Ceiling(LabelSize * 0.7) : 2;
		return (topPad, h - (MarkHeight + 3));
	}

	private static string Format(double v) => v.ToString("0.#", CultureInfo.CurrentCulture);

	// The line, one figure per run of real values (a NaN breaks it).
	private StreamGeometry Line(IReadOnlyList<double> values)
	{
		var g = new StreamGeometry();
		using StreamGeometryContext ctx = g.Open();
		bool open = false;
		for (int i = 0; i < values.Count; i++)
		{
			if (double.IsNaN(values[i]))
			{
				if (open)
				{
					ctx.EndFigure(false);
					open = false;
				}
				continue;
			}
			Point p = new(XOf(i, values.Count), YOf(values[i]));
			if (!open)
			{
				ctx.BeginFigure(p, false);
				open = true;
			}
			else
			{
				ctx.LineTo(p);
			}
		}
		if (open)
		{
			ctx.EndFigure(false);
		}
		return g;
	}

	// The fill under the line, down to the baseline, one closed figure per run of real values.
	private StreamGeometry Area(IReadOnlyList<double> values, double bottom)
	{
		var g = new StreamGeometry();
		using StreamGeometryContext ctx = g.Open();
		int i = 0;
		while (i < values.Count)
		{
			while (i < values.Count && double.IsNaN(values[i]))
			{
				i++;
			}
			if (i >= values.Count)
			{
				break;
			}
			ctx.BeginFigure(new Point(XOf(i, values.Count), bottom), true);
			while (i < values.Count && !double.IsNaN(values[i]))
			{
				ctx.LineTo(new Point(XOf(i, values.Count), YOf(values[i])));
				i++;
			}
			ctx.LineTo(new Point(XOf(i - 1, values.Count), bottom));
			ctx.EndFigure(true);
		}
		return g;
	}
}
