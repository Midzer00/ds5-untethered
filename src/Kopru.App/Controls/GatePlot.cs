// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Kopru.Core.Audio;

namespace Kopru.App.Controls;

// Sound to haptics in one picture (an illustration: a fixed sample of sound, not live data), in two bands. Top: the
// game's sound level over a moment (the thin trace) and the threshold the Sensitivity setting puts on it (the dashed
// line, from the real gate: AudioHapticsDsp.GateOpenDbFor on the sensitivity, mapped onto the plot between -76 and
// -16 dBFS); what rises over the threshold is tinted. Bottom: the haptics made from it (bars, as tall as Strength
// makes them). Bass: a slow trace; Full Sound: every detail of the sound. Off or unavailable: the trace only.
public sealed class GatePlot : Control
{
	public static readonly StyledProperty<bool> IsOnProperty = AvaloniaProperty.Register<GatePlot, bool>(nameof(IsOn));
	public static readonly StyledProperty<double> SensitivityProperty = AvaloniaProperty.Register<GatePlot, double>(nameof(Sensitivity));
	public static readonly StyledProperty<double> StrengthProperty = AvaloniaProperty.Register<GatePlot, double>(nameof(Strength));
	public static readonly StyledProperty<bool> IsFullSoundProperty = AvaloniaProperty.Register<GatePlot, bool>(nameof(IsFullSound));
	public static readonly StyledProperty<string?> SoundWordProperty = AvaloniaProperty.Register<GatePlot, string?>(nameof(SoundWord));
	public static readonly StyledProperty<string?> ThresholdWordProperty = AvaloniaProperty.Register<GatePlot, string?>(nameof(ThresholdWord));
	public static readonly StyledProperty<string?> HapticsWordProperty = AvaloniaProperty.Register<GatePlot, string?>(nameof(HapticsWord));
	public static readonly StyledProperty<IBrush?> TraceBrushProperty = AvaloniaProperty.Register<GatePlot, IBrush?>(nameof(TraceBrush));
	public static readonly StyledProperty<IBrush?> ThresholdBrushProperty = AvaloniaProperty.Register<GatePlot, IBrush?>(nameof(ThresholdBrush));
	public static readonly StyledProperty<IBrush?> TintBrushProperty = AvaloniaProperty.Register<GatePlot, IBrush?>(nameof(TintBrush));
	public static readonly StyledProperty<IBrush?> MeterBrushProperty = AvaloniaProperty.Register<GatePlot, IBrush?>(nameof(MeterBrush));
	public static readonly StyledProperty<IBrush?> AxisBrushProperty = AvaloniaProperty.Register<GatePlot, IBrush?>(nameof(AxisBrush));
	public static readonly StyledProperty<IBrush?> WordBrushProperty = AvaloniaProperty.Register<GatePlot, IBrush?>(nameof(WordBrush));
	public static readonly StyledProperty<IBrush?> MutedWordBrushProperty = AvaloniaProperty.Register<GatePlot, IBrush?>(nameof(MutedWordBrush));

	// The plot's sound range maps linearly onto the drawing between -76 dBFS (the bottom) and -16 (the top).
	private const double BottomDb = -76, TopDb = -16;

	static GatePlot()
	{
		AffectsRender<GatePlot>(IsOnProperty, SensitivityProperty, StrengthProperty, IsFullSoundProperty,
			SoundWordProperty, ThresholdWordProperty, HapticsWordProperty,
			TraceBrushProperty, ThresholdBrushProperty, TintBrushProperty, MeterBrushProperty, AxisBrushProperty,
			WordBrushProperty, MutedWordBrushProperty);
	}

	public bool IsOn { get => GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }
	public double Sensitivity { get => GetValue(SensitivityProperty); set => SetValue(SensitivityProperty, value); }
	public double Strength { get => GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }
	public bool IsFullSound { get => GetValue(IsFullSoundProperty); set => SetValue(IsFullSoundProperty, value); }
	public string? SoundWord { get => GetValue(SoundWordProperty); set => SetValue(SoundWordProperty, value); }
	public string? ThresholdWord { get => GetValue(ThresholdWordProperty); set => SetValue(ThresholdWordProperty, value); }
	public string? HapticsWord { get => GetValue(HapticsWordProperty); set => SetValue(HapticsWordProperty, value); }
	public IBrush? TraceBrush { get => GetValue(TraceBrushProperty); set => SetValue(TraceBrushProperty, value); }
	public IBrush? ThresholdBrush { get => GetValue(ThresholdBrushProperty); set => SetValue(ThresholdBrushProperty, value); }
	public IBrush? TintBrush { get => GetValue(TintBrushProperty); set => SetValue(TintBrushProperty, value); }
	public IBrush? MeterBrush { get => GetValue(MeterBrushProperty); set => SetValue(MeterBrushProperty, value); }
	public IBrush? AxisBrush { get => GetValue(AxisBrushProperty); set => SetValue(AxisBrushProperty, value); }
	public IBrush? WordBrush { get => GetValue(WordBrushProperty); set => SetValue(WordBrushProperty, value); }
	public IBrush? MutedWordBrush { get => GetValue(MutedWordBrushProperty); set => SetValue(MutedWordBrushProperty, value); }

	protected override Size MeasureOverride(Size availableSize) =>
		new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, 112);

	// Where the volume gate's threshold sits in the sound band (0 bottom, 1 top): the real gate's open level on the
	// sensitivity (about -22 dBFS at 0 %, -46 at 50 %, -70 at 100 %) mapped linearly between the plot's -76 (0) and -16
	// (1), clamped so the dashed line never hugs the top or the bottom of the drawing.
	public static double ThresholdFraction(double sensitivity)
	{
		double db = AudioHapticsDsp.GateOpenDbFor(Math.Clamp(sensitivity, 0, 100));
		return Math.Clamp((db - BottomDb) / (TopDb - BottomDb), 0.08, 0.92);
	}

	public override void Render(DrawingContext context)
	{
		double w = Bounds.Width, h = Bounds.Height;
		bool on = IsOn;
		double outHeight = Math.Round(h * 0.3);
		double top = 2, soundBottom = Math.Floor(h - outHeight - 8), bottom = Math.Floor(h - 1);
		double plot = soundBottom - top;
		double threshold = ThresholdFraction(Sensitivity);
		double ty = Half(soundBottom - plot * threshold);
		double Y(double level) => soundBottom - plot * level;

		// What is over the threshold: a flat tint between the trace and the threshold line.
		if (on)
		{
			var over = new StreamGeometry();
			using (StreamGeometryContext g = over.Open())
			{
				bool open = false;
				for (double x = 0; x <= w; x += 1)
				{
					double level = Sound(x / w);
					if (level > threshold)
					{
						if (!open)
						{
							g.BeginFigure(new Point(x, ty), true);
							open = true;
						}
						g.LineTo(new Point(x, Y(level)));
					}
					else if (open)
					{
						g.LineTo(new Point(x, ty));
						g.EndFigure(true);
						open = false;
					}
				}
				if (open)
				{
					g.LineTo(new Point(w, ty));
					g.EndFigure(true);
				}
			}
			context.DrawGeometry(TintBrush, null, over);
		}

		// The sound's level.
		var trace = new StreamGeometry();
		using (StreamGeometryContext g = trace.Open())
		{
			g.BeginFigure(new Point(0, Y(Sound(0))), false);
			for (double x = 1; x <= w; x += 1)
			{
				g.LineTo(new Point(x, Y(Sound(x / w))));
			}
			g.EndFigure(false);
		}
		context.DrawGeometry(null, new Pen(TraceBrush ?? Brushes.Gray, 1, lineJoin: PenLineJoin.Round), trace);

		// The threshold, with its name under the line at the left (the sample is over the threshold there, so that room
		// is free of the trace).
		context.DrawLine(new Pen(ThresholdBrush ?? Brushes.Gray, 1, new DashStyle([4, 3], 0)), new Point(0, ty), new Point(w, ty));
		context.DrawText(Word(ThresholdWord, WordBrush), new Point(0, Math.Round(ty + 3)));
		if (SoundWord is { Length: > 0 })
		{
			context.DrawText(Word(SoundWord, MutedWordBrush), new Point(0, top - 2));
		}

		// The haptics made from it: bars where the sound is over the threshold.
		context.DrawLine(new Pen(AxisBrush ?? Brushes.Gray, 1), new Point(0, Half(bottom)), new Point(w, Half(bottom)));
		if (on)
		{
			var pen = new Pen(MeterBrush ?? Brushes.Gray, 2, lineCap: PenLineCap.Round);
			double full = outHeight - 3;
			for (double x = 2; x <= w - 2; x += 5)
			{
				double level = Sound(x / w);
				if (level > threshold)
				{
					double bar = full * Math.Clamp(Strength / 200, 0, 1) * (0.45 + 0.55 * (level - threshold) / (1 - threshold));
					context.DrawLine(pen, new Point(x, bottom - 2), new Point(x, bottom - 2 - Math.Max(1, bar)));
				}
			}
		}
		if (HapticsWord is { Length: > 0 })
		{
			context.DrawText(Word(HapticsWord, MutedWordBrush), new Point(0, bottom - outHeight));
		}
	}

	// A fixed sample of a game's sound level, 0..1 over t = 0..1: two loud passages, a quiet one, a short hit.
	private double Sound(double t)
	{
		double slow = 0.47 + 0.30 * Math.Sin(t * 9.2 + 0.6) + 0.13 * Math.Sin(t * 21 + 2.0);
		double detail = IsFullSound ? 0.07 * Math.Sin(t * 96) + 0.05 * Math.Sin(t * 171 + 1) : 0;
		return Math.Clamp(slow + detail, 0.04, 0.97);
	}

	private FormattedText Word(string? text, IBrush? brush) =>
		new(text ?? string.Empty, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
			new Typeface(TextElement.GetFontFamily(this)), 11, brush ?? Brushes.Gray);

	// A 1 px line on the half pixel, so it stays one crisp row or column.
	private static double Half(double v) => Math.Floor(v) + 0.5;
}
