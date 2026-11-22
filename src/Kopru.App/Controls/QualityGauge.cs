// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Kopru.App.Controls;

// The link's quality as a small half-ring meter: Segments rounded arc pieces over the top, the first Level of them in Fill,
// the rest a quiet track (Level 0: all track, while measuring). The word goes under it in the view.
public sealed class QualityGauge : Control
{
	public static readonly StyledProperty<int> LevelProperty = AvaloniaProperty.Register<QualityGauge, int>(nameof(Level));
	public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<QualityGauge, IBrush?>(nameof(Fill));
	public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<QualityGauge, IBrush?>(nameof(Track));

	public const int Segments = 3;
	public const double Thickness = 5;
	// Degrees between two pieces.
	public const double GapDegrees = 12;

	private IPen? _fillPen, _trackPen;
	private StreamGeometry?[] _arcs = new StreamGeometry?[Segments];
	private Size _builtSize;

	static QualityGauge()
	{
		AffectsRender<QualityGauge>(LevelProperty, FillProperty, TrackProperty);
	}

	public int Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
	public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
	public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

	protected override Size MeasureOverride(Size availableSize) => new(76, 42);

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == FillProperty)
		{
			_fillPen = null;
		}
		else if (change.Property == TrackProperty)
		{
			_trackPen = null;
		}
	}

	public override void Render(DrawingContext context)
	{
		if (Bounds.Width < 2 * Thickness || Bounds.Height < Thickness)
		{
			return;
		}
		if (_builtSize != Bounds.Size)
		{
			_builtSize = Bounds.Size;
			_arcs = new StreamGeometry?[Segments];
		}
		_fillPen ??= Pen(Fill ?? Brushes.White);
		_trackPen ??= Pen(Track ?? new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)));
		int level = Math.Clamp(Level, 0, Segments);
		for (int i = 0; i < Segments; i++)
		{
			context.DrawGeometry(null, i < level ? _fillPen : _trackPen, _arcs[i] ??= Arc(i));
		}
	}

	private static IPen Pen(IBrush brush) => new ImmutablePen(brush.ToImmutable(), Thickness, lineCap: PenLineCap.Round);

	// Piece i of the half ring, left to right over the top.
	private StreamGeometry Arc(int i)
	{
		double r = Math.Min(Bounds.Width / 2, Bounds.Height) - Thickness / 2 - 1;
		Point centre = new(Bounds.Width / 2, Bounds.Height - Thickness / 2 - 1);
		double span = (180 - GapDegrees * (Segments - 1)) / Segments;
		double from = 180 - i * (span + GapDegrees);
		double to = from - span;
		Point At(double degrees) => new(centre.X + r * Math.Cos(degrees * Math.PI / 180), centre.Y - r * Math.Sin(degrees * Math.PI / 180));
		var g = new StreamGeometry();
		using StreamGeometryContext ctx = g.Open();
		ctx.BeginFigure(At(from), false);
		ctx.ArcTo(At(to), new Size(r, r), 0, false, SweepDirection.Clockwise);
		ctx.EndFigure(false);
		return g;
	}
}
