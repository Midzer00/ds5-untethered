// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Kopru.Core.Localization;

namespace Kopru.App.Controls;

// How a LevelPlot's sample runs across the plot.
public enum LevelPlotKind
{
	// The game's own haptics: a waveform (bars about a centre line).
	Haptics,
	// The classic rumble, Soft: long smooth swells rising from a base line.
	Soft,
	// The classic rumble, Punchy: short sharp hits.
	Punchy
}

// A strength as a drawn level (an illustration, not live data): a fixed sample of what the game sends, as tall as the
// level makes it (Level against Maximum, the slider's range, which is the plot's full height), against a dashed
// reference line at 100 %. Flat lines only: no gradients, no glow.
public sealed class LevelPlot : Control
{
	public static readonly StyledProperty<LevelPlotKind> KindProperty = AvaloniaProperty.Register<LevelPlot, LevelPlotKind>(nameof(Kind));
	public static readonly StyledProperty<double> LevelProperty = AvaloniaProperty.Register<LevelPlot, double>(nameof(Level));
	public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<LevelPlot, double>(nameof(Maximum), 200);
	public static readonly StyledProperty<IBrush?> MeterBrushProperty = AvaloniaProperty.Register<LevelPlot, IBrush?>(nameof(MeterBrush));
	public static readonly StyledProperty<IBrush?> TintBrushProperty = AvaloniaProperty.Register<LevelPlot, IBrush?>(nameof(TintBrush));
	public static readonly StyledProperty<IBrush?> ReferenceBrushProperty = AvaloniaProperty.Register<LevelPlot, IBrush?>(nameof(ReferenceBrush));
	public static readonly StyledProperty<IBrush?> AxisBrushProperty = AvaloniaProperty.Register<LevelPlot, IBrush?>(nameof(AxisBrush));
	public static readonly StyledProperty<IBrush?> HairlineBrushProperty = AvaloniaProperty.Register<LevelPlot, IBrush?>(nameof(HairlineBrush));
	public static readonly StyledProperty<IBrush?> WordBrushProperty = AvaloniaProperty.Register<LevelPlot, IBrush?>(nameof(WordBrush));

	static LevelPlot()
	{
		AffectsRender<LevelPlot>(KindProperty, LevelProperty, MaximumProperty,
			MeterBrushProperty, TintBrushProperty, ReferenceBrushProperty, AxisBrushProperty, HairlineBrushProperty, WordBrushProperty);
	}

	public LevelPlotKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
	public double Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
	public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
	public IBrush? MeterBrush { get => GetValue(MeterBrushProperty); set => SetValue(MeterBrushProperty, value); }
	public IBrush? TintBrush { get => GetValue(TintBrushProperty); set => SetValue(TintBrushProperty, value); }
	public IBrush? ReferenceBrush { get => GetValue(ReferenceBrushProperty); set => SetValue(ReferenceBrushProperty, value); }
	public IBrush? AxisBrush { get => GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }
	public IBrush? HairlineBrush { get => GetValue(HairlineBrushProperty); set => SetValue(HairlineBrushProperty, value); }
	public IBrush? WordBrush { get => GetValue(WordBrushProperty); set => SetValue(WordBrushProperty, value); }

	protected override Size MeasureOverride(Size availableSize) =>
		new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, 64);

	public override void Render(DrawingContext context)
	{
		double w = Bounds.Width, h = Bounds.Height;
		double max = Maximum > 0 ? Maximum : 200;
		var mark = new FormattedText(Text.PercentValue(100), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
			new Typeface(TextElement.GetFontFamily(this)), 10, WordBrush ?? Brushes.Gray);
		double right = Math.Floor(w - mark.Width - 6);
		IBrush meter = MeterBrush ?? Brushes.Gray;
		var dashed = new Pen(ReferenceBrush ?? Brushes.Gray, 1, new DashStyle([4, 3], 0));
		if (Kind == LevelPlotKind.Haptics)
		{
			double mid = Math.Round(h / 2), half = h / 2 - 1;
			double reference = Math.Round(half * 100 / max);
			// The 100 % reference on both sides of the centre line (a plain hairline where the plot is small: dashes
			// between the bars read as noise there).
			Pen level = h < 50 ? new Pen(HairlineBrush ?? Brushes.Gray, 1) : dashed;
			context.DrawLine(level, new Point(0, Half(mid - reference)), new Point(right, Half(mid - reference)));
			context.DrawLine(level, new Point(0, Half(mid + reference)), new Point(right, Half(mid + reference)));
			context.DrawText(mark, new Point(w - mark.Width, mid - reference - mark.Height / 2));
			var pen = new Pen(meter, 2, lineCap: PenLineCap.Round);
			int i = 0;
			for (double x = 2; x <= right - 2; x += 5, i++)
			{
				double bar = Math.Max(1, half * Wave(i) * Math.Clamp(Level, 0, max) / max);
				context.DrawLine(pen, new Point(x, mid - bar), new Point(x, mid + bar));
			}
			return;
		}

		double bottom = Math.Floor(h - 1), full = bottom - 2;
		double line = Half(bottom - full * 100 / max);
		context.DrawLine(dashed, new Point(0, line), new Point(right, line));
		context.DrawText(mark, new Point(w - mark.Width, line - mark.Height / 2));
		double peak = full * Math.Clamp(Level, 0, max) / max;
		var shape = new StreamGeometry();
		using (StreamGeometryContext g = shape.Open())
		{
			g.BeginFigure(new Point(0, bottom), true);
			const int hits = 5;
			double span = right / hits;
			for (int n = 0; n < hits; n++)
			{
				double a = n * span, size = peak * (n % 2 == 0 ? 1 : 0.62);
				if (Kind == LevelPlotKind.Soft)
				{
					// A long swell: up and down over most of the beat.
					for (int s = 0; s <= 24; s++)
					{
						double t = s / 24.0;
						g.LineTo(new Point(a + span * (0.06 + 0.88 * t), bottom - size * Math.Pow(Math.Sin(Math.PI * t), 1.4)));
					}
				}
				else
				{
					// A short hit: straight up, a brief hold, straight down.
					g.LineTo(new Point(a + span * 0.18, bottom));
					g.LineTo(new Point(a + span * 0.20, bottom - size));
					g.LineTo(new Point(a + span * 0.36, bottom - size));
					g.LineTo(new Point(a + span * 0.40, bottom));
				}
				g.LineTo(new Point(a + span, bottom));
			}
			g.EndFigure(true);
		}
		context.DrawGeometry(TintBrush, new Pen(meter, 1.5, lineJoin: PenLineJoin.Round), shape);
		context.DrawLine(new Pen(AxisBrush ?? Brushes.Gray, 1), new Point(0, Half(bottom)), new Point(right, Half(bottom)));
	}

	// A fixed sample waveform, 0..1: bursts of different sizes with quiet stretches between them.
	internal static double Wave(int i)
	{
		double envelope = Math.Pow(Math.Abs(Math.Sin(i * 0.17 + 0.4)), 1.5) * (0.55 + 0.45 * Math.Abs(Math.Sin(i * 0.045 + 1.1)));
		double grain = 0.62 + 0.38 * Math.Abs(Math.Sin(i * 1.9 + 0.3));
		return Math.Clamp(Math.Pow(envelope * grain, 0.6) * 1.08, 0.06, 1);
	}

	// A 1 px line on the half pixel, so it stays one crisp row or column.
	private static double Half(double v) => Math.Floor(v) + 0.5;
}
