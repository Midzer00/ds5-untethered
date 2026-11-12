// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Kopru.App.Controls;

// One Settings tab's cards, laid out to fit the height the page has below the tabs (TargetHeight) without scrolling.
//
// One column while the page column is no wider than OneColumnMax and the cards fit the height; otherwise two columns, each
// card in the column its Column says (0 left, 1 right; document order within a column). A card marked Fill holds a list
// that can grow without bound: when its column would be too tall it gets the height the other cards leave (at least
// FillMin) and its list scrolls inside the card. Columns narrower than NarrowBelow get the "narrow" class: setting rows then
// put pickers, sliders and segmented choices under their text instead of beside it. Cards keep their natural height; what
// is left of the height stays below them. With less height than the cards need even so, the page scrolls (a fallback:
// UiScale gives the layout at least 1100x700, where every tab fits).
public sealed class SettingsLayout : Panel
{
	public static readonly AttachedProperty<int> ColumnProperty =
		AvaloniaProperty.RegisterAttached<SettingsLayout, Control, int>("Column");

	public static readonly AttachedProperty<bool> FillProperty =
		AvaloniaProperty.RegisterAttached<SettingsLayout, Control, bool>("Fill");

	public static readonly StyledProperty<double> TargetHeightProperty =
		AvaloniaProperty.Register<SettingsLayout, double>(nameof(TargetHeight), double.NaN);

	public const double Gap = 16;
	// Wider than this, one column of rows reads as label far left, control far right: two columns instead.
	public const double OneColumnMax = 900;
	// A column narrower than this stacks wide controls under their text.
	public const double NarrowBelow = 500;
	// The least height a Fill card's list keeps (about three rows).
	public const double FillMin = 200;

	private Rect[] _slots = [];

	static SettingsLayout()
	{
		AffectsMeasure<SettingsLayout>(TargetHeightProperty);
		AffectsParentMeasure<SettingsLayout>(ColumnProperty, FillProperty);
	}

	public static int GetColumn(Control c) => c.GetValue(ColumnProperty);
	public static void SetColumn(Control c, int value) => c.SetValue(ColumnProperty, value);
	public static bool GetFill(Control c) => c.GetValue(FillProperty);
	public static void SetFill(Control c, bool value) => c.SetValue(FillProperty, value);

	public double TargetHeight
	{
		get => GetValue(TargetHeightProperty);
		set => SetValue(TargetHeightProperty, value);
	}

	// The arrangement in use (tests).
	public bool IsTwoColumns { get; private set; }
	public bool IsNarrow { get; private set; }

	protected override Size MeasureOverride(Size availableSize)
	{
		double width = double.IsInfinity(availableSize.Width) ? OneColumnMax : availableSize.Width;
		double target = double.IsNaN(TargetHeight) || TargetHeight <= 0 ? double.PositiveInfinity : TargetHeight;
		List<Control> shown = Children.Where(c => c.IsVisible).ToList();
		if (_slots.Length != Children.Count)
		{
			_slots = new Rect[Children.Count];
		}

		double height;
		bool hasRight = shown.Any(c => GetColumn(c) == 1);
		if (width <= OneColumnMax || !hasRight)
		{
			SetLayout(false, width < NarrowBelow);
			height = PlanColumn(shown, 0, width, target);
			if (height <= target + 0.5 || !hasRight)
			{
				MeasureHidden();
				return new Size(width, height);
			}
		}
		double column = Math.Floor((width - Gap) / 2);
		SetLayout(true, column < NarrowBelow);
		height = Math.Max(PlanColumn(shown.Where(c => GetColumn(c) != 1).ToList(), 0, column, target),
			PlanColumn(shown.Where(c => GetColumn(c) == 1).ToList(), column + Gap, column, target));
		MeasureHidden();
		return new Size(width, height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		for (int i = 0; i < Children.Count; i++)
		{
			Children[i].Arrange(i < _slots.Length ? _slots[i] : default);
		}
		return finalSize;
	}

	private void MeasureHidden()
	{
		foreach (Control c in Children.Where(c => !c.IsVisible))
		{
			c.Measure(default);
			_slots[Children.IndexOf(c)] = default;
		}
	}

	// Stacks `cards` at x `left`, `width` wide, into _slots; a Fill card gives up height down to FillMin when the column is
	// taller than `target`. Returns the column's height.
	private double PlanColumn(List<Control> cards, double left, double width, double target)
	{
		var heights = new double[cards.Count];
		for (int i = 0; i < cards.Count; i++)
		{
			cards[i].Measure(new Size(width, double.PositiveInfinity));
			heights[i] = cards[i].DesiredSize.Height;
		}
		double total = heights.Sum() + Gap * Math.Max(0, cards.Count - 1);
		for (int i = 0; i < cards.Count && total > target + 0.5; i++)
		{
			if (!GetFill(cards[i]))
			{
				continue;
			}
			double room = Math.Max(FillMin, heights[i] - (total - target));
			if (room < heights[i])
			{
				cards[i].Measure(new Size(width, room));
				total -= heights[i] - cards[i].DesiredSize.Height;
				heights[i] = cards[i].DesiredSize.Height;
			}
		}
		double y = 0;
		for (int i = 0; i < cards.Count; i++)
		{
			_slots[Children.IndexOf(cards[i])] = new Rect(left, y, width, heights[i]);
			y += heights[i] + Gap;
		}
		return Math.Max(0, y - Gap);
	}

	private void SetLayout(bool two, bool narrow)
	{
		IsTwoColumns = two;
		IsNarrow = narrow;
		Classes.Set("twoColumns", two);
		Classes.Set("narrow", narrow);
	}
}
