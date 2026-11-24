// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Localization;

// User-facing texts of the DualSense Edge support (back paddles, presentation).
public static partial class Text
{
	public static string EdgeLeftPaddle(Language? l = null) => T(l, "Edge.LeftPaddle");

	public static string EdgeRightPaddle(Language? l = null) => T(l, "Edge.RightPaddle");

	public static string EdgeKeepPaddle(Language? l = null) => T(l, "Edge.KeepPaddle");

	public static string EdgePaddlesTitle(Language? l = null) => T(l, "Edge.PaddlesTitle");

	public static string EdgePaddlesHint(Language? l = null) => T(l, "Edge.PaddlesHint");

	public static string PresentAsEdge(Language? l = null) => T(l, "Present.AsEdge");

	public static string PresentAsEdgeHint(Language? l = null) => T(l, "Present.AsEdgeHint");
}
