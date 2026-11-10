// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Kopru.Core.Input;

// IInputSink on Windows: SendInput (winuser.h INPUT / MOUSEINPUT / KEYBDINPUT). Failures (e.g. UIPI blocking input to an
// elevated window) are ignored.
public sealed class WindowsInputSink : IInputSink
{
	private const uint InputMouse = 0, InputKeyboard = 1;
	private const uint MouseMove = 0x0001, LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010,
		MiddleDown = 0x0020, MiddleUp = 0x0040, WheelFlag = 0x0800, HWheelFlag = 0x1000;
	private const uint KeyExtended = 0x0001, KeyUp = 0x0002;

	public void MoveMouse(int dx, int dy)
	{
		if (dx != 0 || dy != 0)
		{
			SendMouse(dx, dy, 0, MouseMove);
		}
	}

	public void MouseButton(MouseButton button, bool down)
	{
		uint flags = button switch
		{
			Input.MouseButton.Right => down ? RightDown : RightUp,
			Input.MouseButton.Middle => down ? MiddleDown : MiddleUp,
			_ => down ? LeftDown : LeftUp
		};
		SendMouse(0, 0, 0, flags);
	}

	public void Wheel(int delta, bool horizontal = false)
	{
		if (delta != 0)
		{
			SendMouse(0, 0, unchecked((uint)delta), horizontal ? HWheelFlag : WheelFlag);
		}
	}

	public void Key(ushort virtualKey, bool down)
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}
		uint flags = (down ? 0 : KeyUp) | (VirtualKeys.IsExtended(virtualKey) ? KeyExtended : 0);
		var input = new INPUT { Type = InputKeyboard, U = new InputUnion { Keyboard = new KEYBDINPUT { Vk = virtualKey, Flags = flags } } };
		_ = SendInput(1, [input], Marshal.SizeOf<INPUT>());
	}

	private static void SendMouse(int dx, int dy, uint data, uint flags)
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}
		var input = new INPUT { Type = InputMouse, U = new InputUnion { Mouse = new MOUSEINPUT { Dx = dx, Dy = dy, MouseData = data, Flags = flags } } };
		_ = SendInput(1, [input], Marshal.SizeOf<INPUT>());
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct INPUT
	{
		public uint Type;
		public InputUnion U;
	}

	[StructLayout(LayoutKind.Explicit)]
	private struct InputUnion
	{
		[FieldOffset(0)] public MOUSEINPUT Mouse;
		[FieldOffset(0)] public KEYBDINPUT Keyboard;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MOUSEINPUT
	{
		public int Dx;
		public int Dy;
		public uint MouseData;
		public uint Flags;
		public uint Time;
		public IntPtr ExtraInfo;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct KEYBDINPUT
	{
		public ushort Vk;
		public ushort Scan;
		public uint Flags;
		public uint Time;
		public IntPtr ExtraInfo;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
