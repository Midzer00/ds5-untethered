// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.App.ViewModels;

// What the Linux app does not have yet; the views hide it ({x:Static vm:AppFeatures.X}). Each one turns on when its Linux
// part exists.
public static class AppFeatures
{
	// The system's sound device pickers and the default-device guard (needs a PipeWire/PulseAudio device access).
	public static bool SystemAudio => false;

	// "Turn sound into haptics" (needs a PipeWire capture of the game's sound).
	public static bool AudioHaptics => false;

	// Mouse Mode and gyro mouse (needs a uinput input sink).
	public static bool MouseMode => false;

	// The Xbox Series pad type (not in this build; Xbox Series is played as Xbox 360).
	public static bool XboxSeries => false;
}
