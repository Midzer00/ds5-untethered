// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Kopru.Core.Diagnostics;

namespace Kopru.App.Services;

// The operating system's foreground window, behind an interface (tests use a fake). Windows keeps a background process
// from taking the foreground (SetForegroundWindow then only flashes the taskbar button), which is exactly the case of a
// PS tap: the input comes from the controller, not from a click on our window.
public interface IForegroundWindows
{
	// The current foreground window (0: none). Cheap; safe on any thread.
	IntPtr Foreground { get; }

	// Makes the window the foreground window. Returns how it got there ("already", "set", "attach input", "alt key"), or
	// null when Windows kept another window in front.
	string? BringToFront(IntPtr window);

	// Whether the window still exists.
	bool Exists(IntPtr window);
}

// The main window as the presenter needs it (Avalonia's Window in the app, a fake in tests). UI thread only.
public interface IPresentedWindow
{
	// The native window handle, 0 while there is none yet.
	IntPtr Handle { get; }

	bool IsMinimized { get; }

	void Show();

	void Restore();

	void Activate();

	void Hide();
}

// Shows the main window in front of everything (also from the background: a PS tap, a second start, a toast) and hides
// it again like closing it does (to the icon next to the clock). IsInFront is read on the controller's input thread.
// Show remembers the window that was in front (a game, when "Also In Games" opened the app over it); Hide puts that window
// back in front when it still exists, so a second PS tap returns to the game.
public sealed class MainWindowPresenter
{
	private readonly IPresentedWindow window;
	private readonly IForegroundWindows foreground;
	private readonly Action<string> log;
	private IntPtr handle;
	private IntPtr returnTo;

	public MainWindowPresenter(IPresentedWindow window, IForegroundWindows foreground, Action<string>? log = null)
	{
		this.window = window;
		this.foreground = foreground;
		this.log = log ?? AppLog.Write;
	}

	// Whether the main window is the foreground window (a hidden or minimised window never is). Any thread.
	public bool IsInFront
	{
		get
		{
			IntPtr h = Volatile.Read(ref handle);
			return h != IntPtr.Zero && foreground.Foreground == h;
		}
	}

	// Shows, restores and focuses the window; reason goes to the log.
	public void Show(string reason)
	{
		IntPtr before = foreground.Foreground;
		IntPtr own = window.Handle;
		if (before != IntPtr.Zero && before != own)
		{
			returnTo = before;
		}
		window.Show();
		if (window.IsMinimized)
		{
			window.Restore();
		}
		window.Activate();
		IntPtr h = window.Handle;
		Volatile.Write(ref handle, h);
		string? how = h == IntPtr.Zero ? null : foreground.BringToFront(h);
		log($"app window: shown ({reason}), {(how != null ? "in front (" + how + ")" : "not in front: Windows kept another window in front")}");
	}

	public void Hide(string reason)
	{
		window.Hide();
		IntPtr back = returnTo;
		returnTo = IntPtr.Zero;
		if (back == IntPtr.Zero || !foreground.Exists(back))
		{
			log($"app window: hidden ({reason})");
			return;
		}
		string? how = foreground.BringToFront(back);
		log($"app window: hidden ({reason}), previous window {(how != null ? "back in front (" + how + ")" : "not in front: Windows kept another window in front")}");
	}
}

// IPresentedWindow over the app's Avalonia window.
public sealed class AvaloniaPresentedWindow(Window window) : IPresentedWindow
{
	public IntPtr Handle => window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

	public bool IsMinimized => window.WindowState == WindowState.Minimized;

	public void Show() => window.Show();

	public void Restore() => window.WindowState = WindowState.Normal;

	public void Activate() => window.Activate();

	public void Hide() => window.Hide();
}
