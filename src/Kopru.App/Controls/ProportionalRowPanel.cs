// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;

namespace Kopru.App.Controls;

// Lays out a single horizontal row of items (such as the Settings tab strip) sized to their content instead of equal
// UniformGrid cells, while still filling the row width:
// - When all items fit on one line, leftover horizontal space is distributed equally across items so the row fills
//   edge to edge without empty track space.
// - When the single-line total exceeds the available width, items receive proportional widths and are re-measured so
//   wrapping labels flow onto a second line cleanly.
public sealed class ProportionalRowPanel : Panel
{
	private double[] _naturalWidths = [];

	protected override Size MeasureOverride(Size availableSize)
	{
		List<Control> visible = VisibleChildren();
		if (visible.Count == 0)
		{
			_naturalWidths = [];
			return default;
		}

		double[] natural = new double[visible.Count];
		double sum = 0;
		double maxHeight = 0;
		for (int i = 0; i < visible.Count; i++)
		{
			visible[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
			natural[i] = visible[i].DesiredSize.Width;
			sum += natural[i];
			if (visible[i].DesiredSize.Height > maxHeight)
			{
				maxHeight = visible[i].DesiredSize.Height;
			}
		}
		_naturalWidths = natural;

		if (double.IsInfinity(availableSize.Width) || sum <= availableSize.Width + 0.5)
		{
			return new Size(sum, maxHeight);
		}

		// Row does not fit on one line: allocate proportional widths and re-measure so labels wrap cleanly.
		double wrappedHeight = 0;
		double[] widths = ComputeWidths(natural, sum, availableSize.Width);
		for (int i = 0; i < visible.Count; i++)
		{
			visible[i].Measure(new Size(widths[i], availableSize.Height));
			if (visible[i].DesiredSize.Height > wrappedHeight)
			{
				wrappedHeight = visible[i].DesiredSize.Height;
			}
		}
		return new Size(availableSize.Width, wrappedHeight);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		List<Control> visible = VisibleChildren();
		if (visible.Count == 0)
		{
			return finalSize;
		}

		double[] natural = _naturalWidths.Length == visible.Count
			? _naturalWidths
			: visible.Select(c => c.DesiredSize.Width).ToArray();
		double sum = natural.Sum();
		double[] widths = ComputeWidths(natural, sum, finalSize.Width);

		double x = 0;
		for (int i = 0; i < visible.Count; i++)
		{
			visible[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
			x += widths[i];
		}
		return finalSize;
	}

	private static double[] ComputeWidths(double[] natural, double sum, double targetWidth)
	{
		int count = natural.Length;
		double[] widths = new double[count];
		if (count == 0)
		{
			return widths;
		}

		if (sum <= 0.001)
		{
			double even = targetWidth / count;
			Array.Fill(widths, even);
			return widths;
		}

		if (sum <= targetWidth + 0.5)
		{
			double extra = Math.Max(0, targetWidth - sum) / count;
			double used = 0;
			double exact = 0;
			for (int i = 0; i < count; i++)
			{
				exact += natural[i] + extra;
				double nextEdge = i == count - 1 ? targetWidth : Math.Round(exact);
				widths[i] = Math.Max(0, nextEdge - used);
				used = nextEdge;
			}
			return widths;
		}

		double scale = targetWidth / sum;
		double assigned = 0;
		double exactScaled = 0;
		for (int i = 0; i < count; i++)
		{
			exactScaled += natural[i] * scale;
			double nextEdge = i == count - 1 ? targetWidth : Math.Round(exactScaled);
			widths[i] = Math.Max(0, nextEdge - assigned);
			assigned = nextEdge;
		}
		return widths;
	}

	private List<Control> VisibleChildren() => Children.Where(c => c.IsVisible).ToList();
}
