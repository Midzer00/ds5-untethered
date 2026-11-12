// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Kopru.App.Services;

// Brings a window to the front past Windows' foreground lock, in steps that stop as soon as one works:
//  1. SetForegroundWindow (works when this process may take the foreground, e.g. right after a tray click);
//  2. the same with this thread's input attached to the foreground window's thread (AttachThreadInput), so Windows sees
//     the request as coming from the foreground's own input queue;
//  3. a synthetic Alt key press and release first: Windows lets the process that received the last input event take the
//     foreground. The Alt release is sent before the window comes up, so no menu opens in it.
// The window is restored (if minimised) and raised to the top of the z-order first.
public sealed class Win32ForegroundWindows : IForegroundWindows
{
	public IntPtr Foreground => OperatingSystem.IsWindows() ? GetForegroundWindow() : IntPtr.Zero;

	public string? BringToFront(IntPtr window)
	{
		if (!OperatingSystem.IsWindows() || window == IntPtr.Zero)
		{
			return null;
		}
		ShowWindow(window, IsIconic(window) ? SwRestore : SwShow);
		if (GetForegroundWindow() == window)
		{
			return "already";
		}
		BringWindowToTop(window);
		if (SetForegroundWindow(window) && GetForegroundWindow() == window)
		{
			return "set";
		}
		IntPtr front = GetForegroundWindow();
		uint frontThread = front == IntPtr.Zero ? 0 : GetWindowThreadProcessId(front, out _);
		uint me = GetCurrentThreadId();
		if (frontThread != 0 && frontThread != me && AttachThreadInput(me, frontThread, true))
		{
			try
			{
				BringWindowToTop(window);
				SetForegroundWindow(window);
				SetFocus(window);
			}
			finally
			{
				AttachThreadInput(me, frontThread, false);
			}
			if (GetForegroundWindow() == window)
			{
				return "attach input";
			}
		}
		keybd_event(VkMenu, 0, KeyEventExtendedKey, UIntPtr.Zero);
		keybd_event(VkMenu, 0, KeyEventExtendedKey | KeyEventKeyUp, UIntPtr.Zero);
		BringWindowToTop(window);
		SetForegroundWindow(window);
		return GetForegroundWindow() == window ? "alt key" : null;
	}

	public bool Exists(IntPtr window) => OperatingSystem.IsWindows() && window != IntPtr.Zero && IsWindow(window);

	private const int SwShow = 5;
	private const int SwRestore = 9;
	private const byte VkMenu = 0x12;
	private const uint KeyEventExtendedKey = 0x0001;
	private const uint KeyEventKeyUp = 0x0002;

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetForegroundWindow(IntPtr window);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool BringWindowToTop(IntPtr window);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ShowWindow(IntPtr window, int command);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsIconic(IntPtr window);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindow(IntPtr window);

	[DllImport("user32.dll")]
	private static extern IntPtr SetFocus(IntPtr window);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

	[DllImport("user32.dll")]
	private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
}
