// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Media;

namespace Kopru.App.Controls;

// Line icons on a 24x24 grid, drawn with a stroke (see the Path.icon and Border.iconTile styles).
public static class IconPaths
{
	public static Geometry Pulse { get; } = Geometry.Parse("M2,12 H6 L9,5 L13,19 L16,10 L18,12 H22");
	public static Geometry Bridge { get; } = Geometry.Parse("M9,15 L15,9 M10.5,6.5 L12.5,4.5 C14.4,2.6 17.6,2.6 19.5,4.5 C21.4,6.4 21.4,9.6 19.5,11.5 L17.5,13.5 M13.5,17.5 L11.5,19.5 C9.6,21.4 6.4,21.4 4.5,19.5 C2.6,17.6 2.6,14.4 4.5,12.5 L6.5,10.5");
	public static Geometry Haptics { get; } = Geometry.Parse("M2,12 H4.5 M7,8 V16 M10,4.5 V19.5 M13,8 V16 M16,6 V18 M19,10 V14 M21.5,12 H22");
	public static Geometry Gamepad { get; } = Geometry.Parse("M7.5,6 H16.5 C19,6 20.4,7.6 21,10.2 L22,15.5 C22.5,18 20.6,19.6 18.8,18.4 L16.2,15.8 H7.8 L5.2,18.4 C3.4,19.6 1.5,18 2,15.5 L3,10.2 C3.6,7.6 5,6 7.5,6 Z M7,9.5 V12.5 M5.5,11 H8.5 M16,10 L16.01,10 M17.8,12 L17.81,12");
	public static Geometry Bolt { get; } = Geometry.Parse("M13,2.5 L4.5,13.5 H11.5 L10.5,21.5 L19.5,10 H12.5 Z");
	public static Geometry Sliders { get; } = Geometry.Parse("M4,7 H20 M4,17 H20 M9,4.5 V9.5 M15,14.5 V19.5");
	public static Geometry Trigger { get; } = Geometry.Parse("M8,21 V11 C8,6.5 9.8,4 12,4 C14.2,4 16,6.5 16,11 V21 M8,13 C10.5,11.8 13.5,11.8 16,13");
	public static Geometry Light { get; } = Geometry.Parse("M12,9 A3,3 0 1 1 11.99,9 Z M12,2.5 V5 M12,19 V21.5 M2.5,12 H5 M19,12 H21.5 M5.3,5.3 L7,7 M17,17 L18.7,18.7 M5.3,18.7 L7,17 M17,7 L18.7,5.3");
	public static Geometry Left { get; } = Geometry.Parse("M10,5 L3,12 L10,19 M3,12 H14 M17,8 V16 M20.5,10 V14");
	public static Geometry Right { get; } = Geometry.Parse("M14,5 L21,12 L14,19 M21,12 H10 M7,8 V16 M3.5,10 V14");
	public static Geometry Power { get; } = Geometry.Parse("M12,3 V11 M6.6,6.6 A7.5,7.5 0 1 0 17.4,6.6");
	public static Geometry Battery { get; } = Geometry.Parse("M4,7.5 H17 C18.1,7.5 19,8.4 19,9.5 V14.5 C19,15.6 18.1,16.5 17,16.5 H4 C2.9,16.5 2,15.6 2,14.5 V9.5 C2,8.4 2.9,7.5 4,7.5 Z M22,10.5 V13.5 M5.5,10.5 V13.5 M9,10.5 V13.5");
}
