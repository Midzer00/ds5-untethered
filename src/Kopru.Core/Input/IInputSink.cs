// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Input;

public enum MouseButton
{
	Left,
	Right,
	Middle
}

// Where desktop mode and gyro mouse send synthesized mouse and keyboard input (Windows: SendInput; tests: a recorder).
// Called on the input thread; implementations must be quick and must not throw.
public interface IInputSink
{
	// Relative pointer motion in pixels.
	void MoveMouse(int dx, int dy);

	void MouseButton(MouseButton button, bool down);

	// Wheel motion in WHEEL_DELTA units (120 = one notch); positive scrolls up (vertical) or right (horizontal).
	void Wheel(int delta, bool horizontal = false);

	// A Windows virtual key code (VirtualKeys) going down or up.
	void Key(ushort virtualKey, bool down);
}

// Drops everything (the default until the app puts the Windows sink in).
public sealed class NullInputSink : IInputSink
{
	public static NullInputSink Instance { get; } = new();

	public void MoveMouse(int dx, int dy) { }

	public void MouseButton(MouseButton button, bool down) { }

	public void Wheel(int delta, bool horizontal = false) { }

	public void Key(ushort virtualKey, bool down) { }
}

// Windows virtual key codes used by desktop mode (winuser.h).
public static class VirtualKeys
{
	public const ushort Backspace = 0x08, Tab = 0x09, Enter = 0x0D, Shift = 0x10, Control = 0x11, Alt = 0x12, Escape = 0x1B, Space = 0x20;
	public const ushort PageUp = 0x21, PageDown = 0x22, Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, Delete = 0x2E;
	public const ushort C = 0x43, O = 0x4F, V = 0x56, LeftWindows = 0x5B;
	public const ushort VolumeMute = 0xAD, VolumeDown = 0xAE, VolumeUp = 0xAF;
	public const ushort MediaNextTrack = 0xB0, MediaPreviousTrack = 0xB1, MediaPlayPause = 0xB3;

	// Keys that need KEYEVENTF_EXTENDEDKEY (the navigation block, not the numpad ones; Windows; the media keys).
	public static bool IsExtended(ushort vk) => vk is PageUp or PageDown or Left or Up or Right or Down or Delete or LeftWindows
		or VolumeMute or VolumeDown or VolumeUp or MediaNextTrack or MediaPreviousTrack or MediaPlayPause;
}
