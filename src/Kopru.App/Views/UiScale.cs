// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.App.Views;

// The window content grows with the window: every page is designed for 1100x700 (DIPs), and a larger window shows the same
// design scaled up by one factor instead of the same small controls with empty room around them. The factor is the
// smaller of the width and height ratios, so the layout always sees at least 1100x700; whatever is left over in the other
// direction goes to the pages' own rules (centred column, top-aligned cards). It never goes below 1 (small windows look
// exactly as before) and stops at Max (beyond it the content stays centred), and moves in Step increments so resizing does
// not re-lay-out the whole window on every pixel. Sizes are DIPs, so the system's display scaling multiplies on top.
public static class UiScale
{
	public const double DesignWidth = 1100;
	public const double DesignHeight = 700;
	public const double Max = 1.75;
	public const double Step = 0.05;

	public static double Factor(double width, double height)
	{
		if (!(width > 0) || !(height > 0))
		{
			return 1;
		}
		double raw = Math.Min(width / DesignWidth, height / DesignHeight);
		double stepped = Math.Floor(raw / Step + 1e-9) * Step;
		return Math.Round(Math.Clamp(stepped, 1, Max), 2);
	}
}
