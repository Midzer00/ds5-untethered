// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;

namespace Kopru.App.Controls;

// Gives its child its natural height (measured as if unlimited), plus up to MaxExtra of the height its parent has to spare.
// Around a page grid whose content row is a star row, this lets the content take a little of a tall window's spare height
// (its own star rows, with their caps, spread it) instead of leaving it all as an empty band under the cards. It never gets
// shorter than its content: in a window too short for it the child keeps its natural height and is cut off at the bottom.
// With unlimited height (in a ScrollViewer or an Auto row) it is exactly its content.
public sealed class ElasticHeight : Decorator
{
	public static readonly StyledProperty<double> MaxExtraProperty = AvaloniaProperty.Register<ElasticHeight, double>(nameof(MaxExtra));

	static ElasticHeight()
	{
		AffectsMeasure<ElasticHeight>(MaxExtraProperty);
	}

	public double MaxExtra
	{
		get => GetValue(MaxExtraProperty);
		set => SetValue(MaxExtraProperty, value);
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		if (Child == null)
		{
			return default;
		}
		Child.Measure(availableSize.WithHeight(double.PositiveInfinity));
		Size natural = Child.DesiredSize;
		double height = natural.Height;
		if (!double.IsInfinity(availableSize.Height) && availableSize.Height > height)
		{
			height = Math.Min(availableSize.Height, height + Math.Max(0, MaxExtra));
		}
		return new Size(natural.Width, height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		// Never squeezed below its content: in a window too short for the page it overflows as an Auto row would.
		Child?.Arrange(new Rect(0, 0, finalSize.Width, Math.Max(finalSize.Height, Child.DesiredSize.Height)));
		return finalSize;
	}
}
