// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kopru.App.Controls;

// Stick position preview: the stick's circle, the inner and outer deadzones, the raw position (muted dot) and the
// position the game receives (accent dot). Positions are -1..1 (Y down, as reported); deadzones are percent.
public sealed class StickPreview : Control
{
	public static readonly StyledProperty<double> RawXProperty = AvaloniaProperty.Register<StickPreview, double>(nameof(RawX));
	public static readonly StyledProperty<double> RawYProperty = AvaloniaProperty.Register<StickPreview, double>(nameof(RawY));
	public static readonly StyledProperty<double> TunedXProperty = AvaloniaProperty.Register<StickPreview, double>(nameof(TunedX));
	public static readonly StyledProperty<double> TunedYProperty = AvaloniaProperty.Register<StickPreview, double>(nameof(TunedY));
	public static readonly StyledProperty<double> InnerDeadzoneProperty = AvaloniaProperty.Register<StickPreview, double>(nameof(InnerDeadzone));
	public static readonly StyledProperty<double> OuterDeadzoneProperty = AvaloniaProperty.Register<StickPreview, double>(nameof(OuterDeadzone));
	public static readonly StyledProperty<bool> IsLiveProperty = AvaloniaProperty.Register<StickPreview, bool>(nameof(IsLive));
	public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<StickPreview, IBrush?>(nameof(Accent));

	static StickPreview()
	{
		AffectsRender<StickPreview>(RawXProperty, RawYProperty, TunedXProperty, TunedYProperty, InnerDeadzoneProperty,
			OuterDeadzoneProperty, IsLiveProperty, AccentProperty);
	}

	public double RawX { get => GetValue(RawXProperty); set => SetValue(RawXProperty, value); }
	public double RawY { get => GetValue(RawYProperty); set => SetValue(RawYProperty, value); }
	public double TunedX { get => GetValue(TunedXProperty); set => SetValue(TunedXProperty, value); }
	public double TunedY { get => GetValue(TunedYProperty); set => SetValue(TunedYProperty, value); }
	public double InnerDeadzone { get => GetValue(InnerDeadzoneProperty); set => SetValue(InnerDeadzoneProperty, value); }
	public double OuterDeadzone { get => GetValue(OuterDeadzoneProperty); set => SetValue(OuterDeadzoneProperty, value); }
	public bool IsLive { get => GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }
	public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

	public override void Render(DrawingContext context)
	{
		double size = Math.Min(Bounds.Width, Bounds.Height);
		if (size <= 8)
		{
			return;
		}
		Point center = new(Bounds.Width / 2, Bounds.Height / 2);
		double radius = size / 2 - 5;
		var faint = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
		var edge = new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1);
		context.DrawEllipse(new SolidColorBrush(Color.FromArgb(10, 255, 255, 255)), edge, center, radius, radius);
		context.DrawLine(faint, new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
		context.DrawLine(faint, new Point(center.X, center.Y - radius), new Point(center.X, center.Y + radius));

		double inner = Math.Clamp(InnerDeadzone, 0, 95) / 100 * radius;
		if (inner > 0.5)
		{
			context.DrawEllipse(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), faint, center, inner, inner);
		}
		double outer = (1 - Math.Clamp(OuterDeadzone, 0, 95) / 100) * radius;
		if (outer < radius - 0.5)
		{
			var dashed = new Pen(new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)), 1, new DashStyle([3, 3], 0));
			context.DrawEllipse(null, dashed, center, outer, outer);
		}
		if (!IsLive)
		{
			return;
		}
		Point Map(double x, double y) => new(center.X + Math.Clamp(x, -1, 1) * radius, center.Y + Math.Clamp(y, -1, 1) * radius);
		context.DrawEllipse(new SolidColorBrush(Color.FromArgb(150, 163, 168, 176)), null, Map(RawX, RawY), 4, 4);
		context.DrawEllipse(Accent ?? Brushes.DodgerBlue, null, Map(TunedX, TunedY), 5, 5);
	}
}
