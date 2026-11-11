// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kopru.App.Controls;

// The one icon slot for row and tile icons: draws an IconPaths geometry on its own 24x24 grid, scaled to Size, so every
// icon keeps its designed size and position (a wide, flat icon is not blown up or lifted the way Stretch=Uniform does) and
// all icons share one box, one stroke weight and one centre line. Use it instead of a stretched Path for icons in rows,
// tiles and buttons.
public sealed class IconSlot : Control
{
	public const double Grid = 24;

	public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<IconSlot, Geometry?>(nameof(Data));
	public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<IconSlot, double>(nameof(Size), 20);
	public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<IconSlot, IBrush?>(nameof(Stroke));
	// Stroke weight on the 24 grid (1.6 = the design weight).
	public static readonly StyledProperty<double> WeightProperty = AvaloniaProperty.Register<IconSlot, double>(nameof(Weight), 1.6);

	static IconSlot()
	{
		AffectsRender<IconSlot>(DataProperty, SizeProperty, StrokeProperty, WeightProperty);
		AffectsMeasure<IconSlot>(SizeProperty);
	}

	public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
	public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
	public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
	public double Weight { get => GetValue(WeightProperty); set => SetValue(WeightProperty, value); }

	protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

	public override void Render(DrawingContext context)
	{
		if (Data is not { } data)
		{
			return;
		}
		double scale = Size / Grid;
		var origin = new Point((Bounds.Width - Size) / 2, (Bounds.Height - Size) / 2);
		var pen = new Pen(Stroke ?? Brushes.White, Weight, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
		using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(origin.X, origin.Y)))
		{
			context.DrawGeometry(null, pen, data);
		}
	}
}

// Tag (object) to the icon geometry, for templates that carry the icon in Tag.
public static class GeometryTag
{
	public static Avalonia.Data.Converters.IValueConverter Converter { get; } =
		new Avalonia.Data.Converters.FuncValueConverter<object?, Geometry?>(o => o as Geometry);
}
