// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.App.Views;

// The window content follows the window: every page is designed for 1100x700 (DIPs), and the window shows that design
// scaled by one factor, up for a larger window (the same design bigger instead of small controls with empty room around
// them) and down for a window below the design size (down to the window's minimum, MinWidth x MinHeight). The factor is
// the smaller of the width and height ratios, rounded down in Step increments (so resizing does not re-lay-out the whole
// window on every pixel, and the rounding never makes the factor larger than the exact ratio): the layout therefore always
// sees at least 1100x700, whatever the window size. Whatever is left over in the other direction goes to the pages' own
// rules (centred column, top-aligned cards). It stays between MinFactor (the window's minimum size, rounded down) and Max
// (beyond it the content stays centred). Sizes are DIPs, so the system's display scaling multiplies on top.
public static class UiScale
{
	public const double DesignWidth = 1100;
	public const double DesignHeight = 700;
	// MainWindow's MinWidth / MinHeight (a test keeps them in step).
	public const double MinWindowWidth = 960;
	public const double MinWindowHeight = 640;
	public const double Max = 1.75;
	public const double Step = 0.05;

	// The factor at the window's minimum size: 960x640 gives 0.85 (the exact ratio, 0.873, rounded down).
	public static double MinFactor { get; } = RoundDown(Math.Min(MinWindowWidth / DesignWidth, MinWindowHeight / DesignHeight));

	public static double Factor(double width, double height)
	{
		if (!(width > 0) || !(height > 0))
		{
			return 1;
		}
		return Math.Clamp(RoundDown(Math.Min(width / DesignWidth, height / DesignHeight)), MinFactor, Max);
	}

	// Down to a whole Step (with a little tolerance so exact multiples such as 1.5 stay themselves).
	private static double RoundDown(double raw) => Math.Round(Math.Floor(raw / Step + 1e-9) * Step, 2);
}
