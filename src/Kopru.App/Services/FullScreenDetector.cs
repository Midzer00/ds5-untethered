// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Kopru.App.Services;

// Whether the foreground window covers its whole monitor (a full-screen game or video), not counting the desktop, a
// maximised ordinary window (it covers the whole monitor too on a screen without a taskbar or with an auto-hiding
// taskbar) or this app's own windows.
public static class FullScreenDetector
{
	public static bool IsForegroundFullScreen() => ForegroundFullScreenApp() != null;

	// The exe name of the full-screen foreground program ("unknown" when it cannot be read), or null when none.
	public static string? ForegroundFullScreenApp()
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}
		IntPtr window = GetForegroundWindow();
		if (window == IntPtr.Zero || window == GetShellWindow() || window == GetDesktopWindow() || IsZoomed(window))
		{
			return null;
		}
		var name = new System.Text.StringBuilder(64);
		if (GetClassName(window, name, name.Capacity) > 0 && name.ToString() is "Progman" or "WorkerW")
		{
			return null;
		}
		GetWindowThreadProcessId(window, out uint processId);
		if (processId == (uint)Environment.ProcessId)
		{
			return null;
		}
		if (!GetWindowRect(window, out Rect rect))
		{
			return null;
		}
		IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
		var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
		if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
		{
			return null;
		}
		bool covers = rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
		return covers ? ProcessName(processId) : null;
	}

	private static string ProcessName(uint processId)
	{
		try
		{
			using var process = System.Diagnostics.Process.GetProcessById((int)processId);
			return process.ProcessName + ".exe";
		}
		catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			return "unknown";
		}
	}

	private const uint MonitorDefaultToNearest = 2;

	[StructLayout(LayoutKind.Sequential)]
	private struct Rect
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MonitorInfo
	{
		public int Size;
		public Rect Monitor;
		public Rect Work;
		public uint Flags;
	}

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern IntPtr GetShellWindow();

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsZoomed(IntPtr window);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

	[DllImport("user32.dll")]
	private static extern IntPtr GetDesktopWindow();

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(IntPtr window, out Rect rect);

	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
