// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Kopru.App.Views;

// The native Windows 11 acrylic backdrop (the blurred desktop behind the window), drawn by the desktop window manager at
// the screen's full resolution. Avalonia's own AcrylicBlur renders a low-resolution blur that looks blocky at high DPI,
// so the window asks for a transparent surface and lets the system draw the backdrop instead.
internal static class SystemBackdrop
{
	private const int DwmwaUseImmersiveDarkMode = 20;
	private const int DwmwaSystemBackdropType = 38;
	private const int DwmsbtTransientWindow = 3; // acrylic

	// The backdrop type attribute exists from Windows 11 22H2 (build 22621).
	public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

	public static bool TryApply(IntPtr hwnd)
	{
		if (!IsSupported || hwnd == IntPtr.Zero)
		{
			return false;
		}
		try
		{
			int dark = 1;
			DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
			ExtendFrame(hwnd);
			int type = DwmsbtTransientWindow;
			return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref type, sizeof(int)) == 0;
		}
		catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
		{
			return false;
		}
	}

	// The acrylic shows behind the whole client area only while the frame covers all of it. The window's extended title bar
	// sets a frame of its own (the caption height) when it changes state, so the window extends it again afterwards.
	public static void ExtendFrame(IntPtr hwnd)
	{
		if (!IsSupported || hwnd == IntPtr.Zero)
		{
			return;
		}
		try
		{
			var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
			DwmExtendFrameIntoClientArea(hwnd, ref margins);
		}
		catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
		{
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Margins
	{
		public int Left, Right, Top, Bottom;
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

	[DllImport("dwmapi.dll")]
	private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
