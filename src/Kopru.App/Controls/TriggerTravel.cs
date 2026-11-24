// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Kopru.Core.Localization;

namespace Kopru.App.Controls;

// What a trigger's travel diagram draws.
public enum TriggerTravelEffect
{
	// Nothing of ours on the track: whatever the game sends.
	Game,
	// From Start to the end of the travel.
	Resistance,
	// A block between Start and End that gives way after it.
	Weapon,
	// From Start to the end, its frequency as ticks.
	Vibration
}

// One trigger's travel in a game's panel: a track from released (0) to fully pressed (100 %), ten zones (the ticks),
// and on it the trigger's custom effect as a flat shape in the accent tint with a 2 px accent outline: how far along
// the travel it acts (x) and how hard (its height = Force). A dashed "100 %" reference runs along the top. The words
// come from the view model (the language's); percent values follow the UI language like the sliders' value texts.
public sealed class TriggerTravel : Control
{
	public static readonly StyledProperty<TriggerTravelEffect> EffectKindProperty =
		AvaloniaProperty.Register<TriggerTravel, TriggerTravelEffect>(nameof(EffectKind));
	public static readonly StyledProperty<double> StartProperty = AvaloniaProperty.Register<TriggerTravel, double>(nameof(Start));
	public static readonly StyledProperty<double> EndProperty = AvaloniaProperty.Register<TriggerTravel, double>(nameof(End), 100);
	public static readonly StyledProperty<double> ForceProperty = AvaloniaProperty.Register<TriggerTravel, double>(nameof(Force), 50);
	public static readonly StyledProperty<double> FrequencyProperty = AvaloniaProperty.Register<TriggerTravel, double>(nameof(Frequency), 30);
	public static readonly StyledProperty<string?> StartWordProperty = AvaloniaProperty.Register<TriggerTravel, string?>(nameof(StartWord));
	public static readonly StyledProperty<string?> EndWordProperty = AvaloniaProperty.Register<TriggerTravel, string?>(nameof(EndWord));
	public static readonly StyledProperty<string?> ForceWordProperty = AvaloniaProperty.Register<TriggerTravel, string?>(nameof(ForceWord));
	public static readonly StyledProperty<string?> GameWordProperty = AvaloniaProperty.Register<TriggerTravel, string?>(nameof(GameWord));
	public static readonly StyledProperty<IBrush?> TintBrushProperty = AvaloniaProperty.Register<TriggerTravel, IBrush?>(nameof(TintBrush));
	public static readonly StyledProperty<IBrush?> EffectBrushProperty = AvaloniaProperty.Register<TriggerTravel, IBrush?>(nameof(EffectBrush));
	public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<TriggerTravel, IBrush?>(nameof(TrackBrush));
	public static readonly StyledProperty<IBrush?> ReferenceBrushProperty = AvaloniaProperty.Register<TriggerTravel, IBrush?>(nameof(ReferenceBrush));
	public static readonly StyledProperty<IBrush?> WordBrushProperty = AvaloniaProperty.Register<TriggerTravel, IBrush?>(nameof(WordBrush));
	public static readonly StyledProperty<IBrush?> MutedWordBrushProperty = AvaloniaProperty.Register<TriggerTravel, IBrush?>(nameof(MutedWordBrush));
	public static readonly StyledProperty<IBrush?> ForceWordBrushProperty = AvaloniaProperty.Register<TriggerTravel, IBrush?>(nameof(ForceWordBrush));

	static TriggerTravel()
	{
		AffectsRender<TriggerTravel>(EffectKindProperty, StartProperty, EndProperty, ForceProperty, FrequencyProperty,
			StartWordProperty, EndWordProperty, ForceWordProperty, GameWordProperty,
			TintBrushProperty, EffectBrushProperty, TrackBrushProperty, ReferenceBrushProperty,
			WordBrushProperty, MutedWordBrushProperty, ForceWordBrushProperty);
	}

	public TriggerTravelEffect EffectKind { get => GetValue(EffectKindProperty); set => SetValue(EffectKindProperty, value); }
	public double Start { get => GetValue(StartProperty); set => SetValue(StartProperty, value); }
	public double End { get => GetValue(EndProperty); set => SetValue(EndProperty, value); }
	public double Force { get => GetValue(ForceProperty); set => SetValue(ForceProperty, value); }
	public double Frequency { get => GetValue(FrequencyProperty); set => SetValue(FrequencyProperty, value); }
	public string? StartWord { get => GetValue(StartWordProperty); set => SetValue(StartWordProperty, value); }
	public string? EndWord { get => GetValue(EndWordProperty); set => SetValue(EndWordProperty, value); }
	public string? ForceWord { get => GetValue(ForceWordProperty); set => SetValue(ForceWordProperty, value); }
	public string? GameWord { get => GetValue(GameWordProperty); set => SetValue(GameWordProperty, value); }
	public IBrush? TintBrush { get => GetValue(TintBrushProperty); set => SetValue(TintBrushProperty, value); }
	public IBrush? EffectBrush { get => GetValue(EffectBrushProperty); set => SetValue(EffectBrushProperty, value); }
	public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
	public IBrush? ReferenceBrush { get => GetValue(ReferenceBrushProperty); set => SetValue(ReferenceBrushProperty, value); }
	public IBrush? WordBrush { get => GetValue(WordBrushProperty); set => SetValue(WordBrushProperty, value); }
	public IBrush? MutedWordBrush { get => GetValue(MutedWordBrushProperty); set => SetValue(MutedWordBrushProperty, value); }
	public IBrush? ForceWordBrush { get => GetValue(ForceWordBrushProperty); set => SetValue(ForceWordBrushProperty, value); }

	protected override Size MeasureOverride(Size availableSize) =>
		new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, 118);

	// The effect's shape inside `plot` (the track's rectangle): from Start to End percent of the width (Resistance and
	// Vibration run to the end of the track) and Force percent of the height up from the baseline.
	public static Rect EffectBounds(TriggerTravelEffect effect, double start, double end, double force, Rect plot)
	{
		double xs = plot.Left + plot.Width * Math.Clamp(start, 0, 100) / 100;
		double xe = effect == TriggerTravelEffect.Weapon ? plot.Left + plot.Width * Math.Clamp(end, 0, 100) / 100 : plot.Right;
		double top = plot.Bottom - plot.Height * Math.Clamp(force, 0, 100) / 100;
		return new Rect(xs, top, Math.Max(0, xe - xs), Math.Max(0, plot.Bottom - top));
	}

	public override void Render(DrawingContext context)
	{
		double w = Bounds.Width, h = Bounds.Height;
		double x0 = 4, x1 = Math.Floor(w - 4);
		double bottom = Math.Floor(h - 20), top = 20;
		var plot = new Rect(x0, top, x1 - x0, bottom - top);
		double X(double percent) => Math.Round(plot.Left + plot.Width * Math.Clamp(percent, 0, 100) / 100);
		double xs = X(Start), xe = X(End);
		double yf = Math.Round(plot.Bottom - plot.Height * Math.Clamp(Force, 0, 100) / 100);
		bool on = EffectKind != TriggerTravelEffect.Game;
		IBrush tint = TintBrush ?? Brushes.Transparent;
		var live = new Pen(EffectBrush ?? Brushes.Transparent, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
		var axis = new Pen(TrackBrush ?? Brushes.Gray, 1);
		var dashed = new Pen(ReferenceBrush ?? Brushes.Gray, 1, new DashStyle([4, 3], 0));

		// How hard, over the shape's top edge (placed first: the "100 %" reference line makes room for it when they meet).
		FormattedText? force = on ? Word(ForceWord + " " + Pct(Force), ForceWordBrush) : null;
		var forceAt = new Point(force == null ? 0 : Math.Min(xs + 2, w - force.Width), force == null ? 0 : yf - force.Height - 3);
		{
			FormattedText most = Word(ForceWord + " " + Pct(100), MutedWordBrush, 10);
			double line = Half(top), end = x1 - most.Width - 6;
			if (force != null && forceAt.Y < line + 2 && forceAt.Y + force.Height > line - 2)
			{
				context.DrawLine(dashed, new Point(x0, line), new Point(Math.Max(x0, forceAt.X - 5), line));
				context.DrawLine(dashed, new Point(Math.Min(end, forceAt.X + force.Width + 5), line), new Point(end, line));
			}
			else
			{
				context.DrawLine(dashed, new Point(x0, line), new Point(end, line));
			}
			context.DrawText(most, new Point(x1 - most.Width, Math.Round(top - most.Height / 2)));
		}

		switch (EffectKind)
		{
			case TriggerTravelEffect.Resistance:
				context.DrawRectangle(tint, null, EffectBounds(EffectKind, Start, End, Force, plot));
				Line(context, live, new Point(xs, bottom), new Point(xs, yf), new Point(x1, yf));
				break;
			case TriggerTravelEffect.Weapon:
				context.DrawRectangle(tint, null, EffectBounds(EffectKind, Start, End, Force, plot));
				Line(context, live, new Point(xs, bottom), new Point(xs, yf), new Point(xe, yf), new Point(xe, bottom));
				break;
			case TriggerTravelEffect.Vibration:
				context.DrawRectangle(tint, null, EffectBounds(EffectKind, Start, End, Force, plot));
				// The frequency as ticks: more of them the faster it pulses.
				int count = (int)Math.Clamp(Math.Round(Frequency / 2.5), 3, Math.Max(3, (x1 - xs) / 5));
				double step = (x1 - xs) / count;
				for (int i = 0; i < count; i++)
				{
					double x = Math.Round(xs + i * step);
					context.DrawLine(live, new Point(x, bottom), new Point(x, yf));
				}
				break;
			default:
				// Nothing of ours: whatever the game sends.
				double y = Half(bottom - plot.Height * 0.5);
				FormattedText word = Word(GameWord, MutedWordBrush, 12);
				double gap = word.Width > 0 ? word.Width / 2 + 8 : 0;
				double mid = Math.Round((x0 + x1) / 2);
				context.DrawLine(dashed, new Point(x0, y), new Point(mid - gap, y));
				context.DrawLine(dashed, new Point(mid + gap, y), new Point(x1, y));
				context.DrawText(word, new Point(mid - word.Width / 2, Math.Round(y - word.Height / 2)));
				break;
		}

		// The track with its ten zones.
		for (int zone = 0; zone <= 10; zone++)
		{
			double x = Half(X(zone * 10) - (zone == 10 ? 1 : 0));
			context.DrawLine(axis, new Point(x, bottom), new Point(x, bottom + 4));
		}
		context.DrawLine(axis, new Point(x0, Half(bottom)), new Point(x1, Half(bottom)));

		// Under the track: where the effect starts (and ends), and the ends of the travel where they do not collide.
		double ty = bottom + 6;
		List<Rect> taken = [];
		if (on)
		{
			taken.Add(Mark(context, Label(StartWord, Start), xs, ty, w));
			if (EffectKind == TriggerTravelEffect.Weapon)
			{
				taken.Add(Mark(context, Label(EndWord, End), xe, ty, w, after: taken[0]));
			}
		}
		FormattedText zero = Word("0", MutedWordBrush);
		var zeroRect = new Rect(x0, ty, zero.Width, zero.Height);
		if (!taken.Any(t => t.Inflate(6).Intersects(zeroRect)))
		{
			context.DrawText(zero, zeroRect.Position);
		}
		FormattedText full = Word(Pct(100), MutedWordBrush);
		var fullRect = new Rect(x1 - full.Width, ty, full.Width, full.Height);
		if (!taken.Any(t => t.Inflate(6).Intersects(fullRect)))
		{
			context.DrawText(full, fullRect.Position);
		}
		if (force != null)
		{
			context.DrawText(force, forceAt);
		}
	}

	private FormattedText Word(string? text, IBrush? brush, double size = 11) =>
		new(text ?? string.Empty, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
			new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.Normal), size, brush ?? Brushes.Gray);

	private static string Pct(double value) => Text.PercentValue(value);

	private static string Label(string? word, double value) => word is { Length: > 0 } ? word + " " + Pct(value) : Pct(value);

	// A value under the track, centred on x (kept inside the control, and right of the label before it).
	private Rect Mark(DrawingContext context, string text, double x, double y, double width, Rect? after = null)
	{
		FormattedText label = Word(text, WordBrush);
		double left = Math.Clamp(Math.Round(x - label.Width / 2), 0, Math.Max(0, width - label.Width));
		if (after is { } before && left < before.Right + 10)
		{
			left = Math.Min(before.Right + 10, Math.Max(0, width - label.Width));
		}
		context.DrawText(label, new Point(left, y));
		return new Rect(left, y, label.Width, label.Height);
	}

	// A 1 px line on the half pixel, so it stays one crisp row or column.
	private static double Half(double v) => Math.Floor(v) + 0.5;

	private static void Line(DrawingContext context, Pen pen, params Point[] points)
	{
		var path = new StreamGeometry();
		using (StreamGeometryContext g = path.Open())
		{
			g.BeginFigure(points[0], false);
			foreach (Point p in points.Skip(1))
			{
				g.LineTo(p);
			}
			g.EndFigure(false);
		}
		context.DrawGeometry(null, pen, path);
	}
}
