// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Kopru.App.Controls;

// The controller Test tab's cards, laid out to fill the height the tab has (TargetHeight) without scrolling. Children, in
// order: drift calibration, the controller stage, motion sensors, left stick, right stick, L2, R2.
//
// Two arrangements, chosen by the width alone:
// - Wide (room for three columns): left stick over L2 | controller over calibration and motion | right stick over R2.
// - Stacked: calibration | controller | motion in one row, then the two sticks, then the two triggers.
// A stick card with height to spare in the wide arrangement puts its circle above its sliders. Every card first gets its
// natural height. Height left over up to TargetHeight goes to the content, each part up to a
// cap: the gaps grow a little, the stick circles (sized here, StickSize) and the controller picture grow;
// beyond the caps the rest stays below the cards. With less height than the cards need, they keep their natural height
// and the page scrolls (a fallback: UiScale gives the layout at least 1100x700, where the cards fit).
public sealed class TesterLayout : Panel
{
	public static readonly StyledProperty<double> TargetHeightProperty =
		AvaloniaProperty.Register<TesterLayout, double>(nameof(TargetHeight), double.NaN);


	public const double GapMin = 12;
	public const double GapMax = 20;
	public const double StickMin = 96;
	public const double StickMax = 160;
	// Above its sliders a circle has the card's width to itself and may grow larger.
	public const double StickMaxAbove = 200;
	// A stick card's chrome (padding and border, both sides), the gap between its circle and sliders, and the narrowest
	// slider column that still shows "label ... value" on one line.
	public const double CardChrome = 38;
	public const double CircleGap = 18;
	public const double SliderMin = 160;
	// The narrowest middle column (wide arrangement) and side cards (stacked first row).
	public const double MiddleMin = 260;
	public const double SideCardMin = 270;
	public const double StageMinHeight = 120;
	// A trigger card narrower than this puts its two sliders under each other; a motion card narrower than this puts the
	// accelerometer under the gyroscope.
	public const double TriggerSideBySide = 362;
	public const double MotionSideBySide = 240;

	private const int Count = 7;
	private const int Calibration = 0, Stage = 1, Motion = 2, LeftStick = 3, RightStick = 4, LeftTrigger = 5, RightTrigger = 6;

	private readonly Rect[] _slots = new Rect[Count];

	static TesterLayout()
	{
		AffectsMeasure<TesterLayout>(TargetHeightProperty);
	}

	public double TargetHeight
	{
		get => GetValue(TargetHeightProperty);
		set => SetValue(TargetHeightProperty, value);
	}

	// The stick circles' size now (the StickPreviews in the stick cards are sized by the layout).
	public double StickSize { get; private set; } = StickMin;

	// The arrangement in use (tests).
	public bool IsWide { get; private set; }
	public bool IsTallSticks { get; private set; }

	public static double SideMin => CardChrome + StickMin + CircleGap + SliderMin;

	protected override Size MeasureOverride(Size availableSize)
	{
		if (Children.Count < Count)
		{
			return base.MeasureOverride(availableSize);
		}
		double width = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
		IsWide = width >= 2 * SideMin + MiddleMin + 2 * GapMin;
		// The gap grows with the spare height; the columns are then measured again at that gap so every card is measured at
		// the width it gets.
		double g = Plan(width, GapMin, out double gap);
		if (Math.Abs(gap - g) > 0.01)
		{
			Plan(width, gap, out _);
		}

		double bottom = 0;
		for (int i = 0; i < Count; i++)
		{
			Children[i].Measure(_slots[i].Size);
			bottom = Math.Max(bottom, _slots[i].Bottom);
		}
		for (int i = Count; i < Children.Count; i++)
		{
			Children[i].Measure(default);
		}
		return new Size(width, bottom);
	}

	// Lays the cards out at gap `g` into _slots (and sizes the circles); returns g and, in `next`, the gap the spare height
	// asks for.
	private double Plan(double width, double g, out double next)
	{
		double target = TargetHeight;
		double side, middle, stageWidth, flank;
		if (IsWide)
		{
			side = Math.Max(SideMin, Math.Floor((width - 2 * GapMin) * 0.35));
			middle = width - 2 * side - 2 * g;
			stageWidth = middle;
			flank = middle;
		}
		else
		{
			side = Math.Floor((width - g) / 2);
			flank = Math.Min(SideCardMin, Math.Floor((width - 2 * GapMin) / 3));
			stageWidth = width - 2 * flank - 2 * g;
			middle = stageWidth;
		}
		SetClasses(side < TriggerSideBySide, flank < MotionSideBySide, false);
		double stickCap = Math.Clamp(side - CardChrome - CircleGap - SliderMin, StickMin, StickMax);
		double tallCap = Math.Min(StickMaxAbove, side - CardChrome);

		// Natural heights (circles at their smallest, the picture at its smallest).
		SetStick(StickMin);
		double cal = Natural(Calibration, flank);
		double motion = Natural(Motion, flank);
		double sticks = Math.Max(Natural(LeftStick, side), Natural(RightStick, side));
		double triggers = Math.Max(Natural(LeftTrigger, side), Natural(RightTrigger, side));
		double stageMax = Natural(Stage, stageWidth);
		double stageMin = Math.Min(stageMax, StageMinHeight);
		SetStick(stickCap);
		double sticksAtCap = Math.Max(Natural(LeftStick, side), Natural(RightStick, side));
		// The same card with its circle above the sliders.
		SetClasses(side < TriggerSideBySide, flank < MotionSideBySide, true);
		SetStick(StickMin);
		double tallSticks = Math.Max(Natural(LeftStick, side), Natural(RightStick, side));

		double baseTop = Math.Max(Math.Max(cal, motion), stageMin);
		double total = IsWide
			? Math.Max(sticks + g + triggers, stageMin + cal + motion + 2 * g)
			: baseTop + sticks + triggers + 2 * g;
		double spare = double.IsNaN(target) ? 0 : Math.Max(0, target - (total - 2 * (g - GapMin)));
		next = Math.Clamp(GapMin + spare / 16, GapMin, GapMax);
		double height = double.IsNaN(target) ? total : Math.Max(total, target);

		// The stick circle for a card of height `room`: the card's height grows one for one with the circle once the circle is
		// taller than the sliders beside it (measured at the cap), so this inverts that line. Above the sliders it grows one
		// for one from the smallest circle.
		double Beside(double room) => Math.Clamp(stickCap - (sticksAtCap - room), StickMin, stickCap);
		double Above(double room) => Math.Clamp(StickMin + room - tallSticks, StickMin, tallCap);

		double stickRow, triggerRow = triggers, stageHeight, topRow = 0;
		if (IsWide)
		{
			stickRow = height - g - triggerRow;
			stageHeight = Math.Clamp(height - cal - motion - 2 * g, stageMin, stageMax);
		}
		else
		{
			double extra = height - total;
			double topRoom = Math.Max(0, Math.Max(stageMax, baseTop) - baseTop);
			// Half of the extra to the picture row, half to the stick row; what one cannot use goes to the other. The stick row
			// can take more with the circles above the sliders.
			double stickRoom = Math.Max(sticksAtCap, tallSticks + tallCap - StickMin) - sticks;
			double toTop = Math.Min(topRoom, extra / 2);
			double toSticks = Math.Min(stickRoom, extra - toTop);
			toTop = Math.Min(topRoom, extra - toSticks);
			topRow = baseTop + toTop;
			stickRow = sticks + toSticks;
			stageHeight = Math.Min(topRow, stageMax);
		}
		bool tall = Above(stickRow) >= Beside(stickRow) + 8;
		SetClasses(side < TriggerSideBySide, flank < MotionSideBySide, tall);
		IsTallSticks = tall;
		SetStick(tall ? Above(stickRow) : Beside(stickRow));

		double y;
		if (IsWide)
		{
			double right = side + g + middle + g;
			_slots[LeftStick] = new Rect(0, 0, side, stickRow);
			_slots[RightStick] = new Rect(right, 0, side, stickRow);
			_slots[LeftTrigger] = new Rect(0, stickRow + g, side, triggerRow);
			_slots[RightTrigger] = new Rect(right, stickRow + g, side, triggerRow);
			y = 0;
			_slots[Stage] = new Rect(side + g, y, middle, stageHeight);
			y += stageHeight + g;
			_slots[Calibration] = new Rect(side + g, y, middle, cal);
			y += cal + g;
			// Motion takes what is left down to the bottom, so the middle column ends with the sides.
			_slots[Motion] = new Rect(side + g, y, middle, Math.Max(motion, height - y));
		}
		else
		{
			_slots[Calibration] = new Rect(0, 0, flank, topRow);
			_slots[Stage] = new Rect(flank + g, 0, stageWidth, topRow);
			_slots[Motion] = new Rect(flank + g + stageWidth + g, 0, width - flank - g - stageWidth - g, topRow);
			y = topRow + g;
			_slots[LeftStick] = new Rect(0, y, side, stickRow);
			_slots[RightStick] = new Rect(width - side, y, side, stickRow);
			y += stickRow + g;
			_slots[LeftTrigger] = new Rect(0, y, side, triggerRow);
			_slots[RightTrigger] = new Rect(width - side, y, side, triggerRow);
		}
		return g;
	}

	// The card variants (see the view's styles); a change re-measures the cards' insides.
	private void SetClasses(bool stackedTriggers, bool stackedMotion, bool tallSticks)
	{
		bool changed = Classes.Contains("stackedTriggers") != stackedTriggers || Classes.Contains("stackedMotion") != stackedMotion
			|| Classes.Contains("tallSticks") != tallSticks;
		if (!changed)
		{
			return;
		}
		Classes.Set("stackedTriggers", stackedTriggers);
		Classes.Set("stackedMotion", stackedMotion);
		Classes.Set("tallSticks", tallSticks);
		for (int i = 0; i < Count; i++)
		{
			foreach (Visual v in Children[i].GetVisualDescendants())
			{
				(v as Layoutable)?.InvalidateMeasure();
			}
			Children[i].InvalidateMeasure();
		}
	}

	// Sizes the stick circles and invalidates the way up to this panel, so measuring the stick cards again sees the new size.
	private void SetStick(double size)
	{
		StickSize = size;
		foreach (Control card in new[] { Children[LeftStick], Children[RightStick] })
		{
			foreach (StickPreview preview in card.GetVisualDescendants().OfType<StickPreview>())
			{
				if (preview.Width == size && preview.Height == size)
				{
					continue;
				}
				preview.Width = size;
				preview.Height = size;
				for (Visual? v = preview; v != null && v != this; v = v.GetVisualParent())
				{
					(v as Layoutable)?.InvalidateMeasure();
				}
			}
		}
	}

	private double Natural(int index, double width)
	{
		Control child = Children[index];
		child.Measure(new Size(width, double.PositiveInfinity));
		return child.DesiredSize.Height;
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		if (Children.Count < Count)
		{
			return base.ArrangeOverride(finalSize);
		}
		for (int i = 0; i < Count; i++)
		{
			Children[i].Arrange(_slots[i]);
		}
		return finalSize;
	}
}
