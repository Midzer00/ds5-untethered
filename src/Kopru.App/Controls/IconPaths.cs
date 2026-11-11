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
	public static Geometry Check { get; } = Geometry.Parse("M4.5,12.5 L9.5,17.5 L19.5,6.5");
	public static Geometry Leaf { get; } = Geometry.Parse("M5,19 C5,10.5 10,5 19,5 C19,13.5 13.5,19 5,19 Z M5,19 L12.5,11.5");
	public static Geometry Mouse { get; } = Geometry.Parse("M12,3 C15.6,3 18,5.4 18,9 V15 C18,18.6 15.6,21 12,21 C8.4,21 6,18.6 6,15 V9 C6,5.4 8.4,3 12,3 Z M12,6.5 V9.5");
	public static Geometry Speaker { get; } = Geometry.Parse("M4,9.5 H7.5 L12,5.5 V18.5 L7.5,14.5 H4 Z M15.5,9.2 C16.6,10.8 16.6,13.2 15.5,14.8 M18.5,6.5 C20.8,9.6 20.8,14.4 18.5,17.5");
	public static Geometry Microphone { get; } = Geometry.Parse("M12,3 C13.7,3 15,4.3 15,6 V11.5 C15,13.2 13.7,14.5 12,14.5 C10.3,14.5 9,13.2 9,11.5 V6 C9,4.3 10.3,3 12,3 Z M5.5,11 C5.5,14.8 8.4,17.5 12,17.5 C15.6,17.5 18.5,14.8 18.5,11 M12,17.5 V21 M9,21 H15");
	// The controller's mic button: a pill-shaped button with its light line.
	public static Geometry MicButton { get; } = Geometry.Parse("M7,8 H17 C19.2,8 21,9.8 21,12 C21,14.2 19.2,16 17,16 H7 C4.8,16 3,14.2 3,12 C3,9.8 4.8,8 7,8 Z M9.5,12 H14.5");
	public static Geometry Profile { get; } = Geometry.Parse("M12,4 A4,4 0 1 1 11.99,4 Z M4.5,20.5 C5.3,16.6 8.3,14.5 12,14.5 C15.7,14.5 18.7,16.6 19.5,20.5");
	public static Geometry Pencil { get; } = Geometry.Parse("M4.5,19.5 L5.3,15.7 L15.6,5.4 C16.4,4.6 17.8,4.6 18.6,5.4 C19.4,6.2 19.4,7.6 18.6,8.4 L8.3,18.7 Z M13.8,7.2 L16.8,10.2");
	// Command-key loops: button-combo shortcuts.
	public static Geometry Shortcut { get; } = Geometry.Parse("M9,9 H15 V15 H9 Z M9,9 H6.5 A2.5,2.5 0 1 1 9,6.5 Z M15,9 V6.5 A2.5,2.5 0 1 1 17.5,9 Z M15,15 H17.5 A2.5,2.5 0 1 1 15,17.5 Z M9,15 V17.5 A2.5,2.5 0 1 1 6.5,15 Z");
	public static Geometry ChevronRight { get; } = Geometry.Parse("M9.5,6 L15.5,12 L9.5,18");
	public static Geometry Battery { get; } = Geometry.Parse("M4,7.5 H17 C18.1,7.5 19,8.4 19,9.5 V14.5 C19,15.6 18.1,16.5 17,16.5 H4 C2.9,16.5 2,15.6 2,14.5 V9.5 C2,8.4 2.9,7.5 4,7.5 Z M22,10.5 V13.5 M5.5,10.5 V13.5 M9,10.5 V13.5");
}
