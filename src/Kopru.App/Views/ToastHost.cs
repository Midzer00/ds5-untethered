// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Kopru.App.Services;
using Kopru.App.ViewModels;

namespace Kopru.App.Views;

// Shows notifications as toast windows at the bottom right of the primary screen's working area (above the taskbar):
// borderless, topmost, not activated (a game keeps the focus), no taskbar entry. Up to MaxVisible stack upwards, the newest
// on top; each slides and fades in over 200 ms, closes itself after its duration (paused while the pointer is over it),
// and a click opens the main window on the related page. Toast windows never take the focus, not even when clicked
// (WS_EX_NOACTIVATE): a button on a toast works while the game keeps the keyboard and stays in front.
public sealed class ToastHost
{
	public const int MaxVisible = 3;
	public static readonly TimeSpan AnimationTime = TimeSpan.FromMilliseconds(200);
	private const double ScreenMargin = 16;
	private const double Gap = 10;
	private const double SlideDistance = 28;

	private readonly List<ToastEntry> _entries = [];
	private readonly Action<AppPage> _open;
	private readonly Func<bool> _fullScreen;

	public ToastHost(Action<AppPage> open, Func<bool>? fullScreen = null)
	{
		_open = open;
		_fullScreen = fullScreen ?? (() => false);
	}

	public int Count => _entries.Count;

	// Callable from any thread.
	public void Show(AppNotification notification) => Dispatcher.UIThread.Post(() => ShowOnUi(notification));

	private void ShowOnUi(AppNotification notification)
	{
		bool compact;
		try
		{
			compact = _fullScreen();
		}
		catch (Exception)
		{
			compact = false;
		}
		while (_entries.Count >= MaxVisible)
		{
			Close(_entries[0]);
		}
		var model = new ToastViewModel(notification, compact);
		var view = new ToastView { DataContext = model };
		var window = new Window
		{
			SystemDecorations = SystemDecorations.None,
			Topmost = true,
			ShowActivated = false,
			ShowInTaskbar = false,
			CanResize = false,
			Focusable = false,
			Background = Brushes.Transparent,
			TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
			SizeToContent = SizeToContent.WidthAndHeight,
			WindowStartupLocation = WindowStartupLocation.Manual,
			Title = notification.Title,
			Content = view,
		};
		view.Opacity = 0;
		view.RenderTransform = TransformOperations.Parse($"translateX({SlideDistance}px)");
		view.Transitions =
		[
			new DoubleTransition { Property = Visual.OpacityProperty, Duration = AnimationTime, Easing = new CubicEaseOut() },
			new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = AnimationTime, Easing = new CubicEaseOut() },
		];
		var entry = new ToastEntry(window, view, notification.Duration);
		view.Activated += (_, _) =>
		{
			Close(entry);
			_open(notification.Page);
		};
		view.Dismissed += (_, _) => Close(entry);
		model.ActionInvoked += () => Close(entry);
		view.PointerEntered += (_, _) => entry.Pause();
		view.PointerExited += (_, _) => entry.Resume();
		entry.Expired += () => Close(entry);
		_entries.Add(entry);
		// Shown fully transparent first, so the size is known before it is placed and faded in.
		window.Show();
		NoActivate(window);
		entry.Width = window.ClientSize.Width;
		entry.Height = window.ClientSize.Height;
		Layout();
		Dispatcher.UIThread.Post(() =>
		{
			view.Opacity = 1;
			view.RenderTransform = TransformOperations.Parse("translateX(0px)");
		}, DispatcherPriority.Background);
		entry.Start();
	}

	// Clicks on the toast don't activate it (and so don't take the focus from a game).
	private static void NoActivate(Window window)
	{
		if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero)
		{
			return;
		}
		long style = GetWindowLongPtr(handle.Handle, ExStyle).ToInt64();
		SetWindowLongPtr(handle.Handle, ExStyle, new IntPtr(style | NoActivateStyle | ToolWindowStyle));
	}

	private const int ExStyle = -20;
	private const long NoActivateStyle = 0x08000000;
	private const long ToolWindowStyle = 0x00000080;

	[System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

	[System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
	private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

	private void Close(ToastEntry entry)
	{
		if (!_entries.Remove(entry))
		{
			return;
		}
		entry.Stop();
		entry.View.Opacity = 0;
		entry.View.RenderTransform = TransformOperations.Parse($"translateX({SlideDistance}px)");
		DispatcherTimer.RunOnce(entry.Window.Close, AnimationTime);
		Layout();
	}

	// Oldest at the bottom (just above the taskbar), newer ones above it.
	private void Layout()
	{
		Screen? screen = _entries.Count > 0 ? _entries[0].Window.Screens.Primary : null;
		if (screen == null)
		{
			return;
		}
		double scale = screen.Scaling;
		PixelRect area = screen.WorkingArea;
		double bottom = area.Bottom - ScreenMargin * scale;
		foreach (ToastEntry e in _entries)
		{
			double height = e.Height * scale;
			double top = bottom - height;
			e.Window.Position = new PixelPoint((int)(area.Right - (e.Width + ScreenMargin) * scale), (int)top);
			bottom = top - Gap * scale;
		}
	}

	private sealed class ToastEntry(Window window, ToastView view, TimeSpan duration)
	{
		private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
		private TimeSpan _left = duration;

		public Window Window { get; } = window;
		public ToastView View { get; } = view;
		public double Width { get; set; }
		public double Height { get; set; }
		public bool Paused { get; private set; }

		public event Action? Expired;

		public void Start()
		{
			_timer.Tick += (_, _) =>
			{
				if (Paused)
				{
					return;
				}
				_left -= _timer.Interval;
				if (_left <= TimeSpan.Zero)
				{
					Stop();
					Expired?.Invoke();
				}
			};
			_timer.Start();
		}

		public void Stop() => _timer.Stop();

		public void Pause() => Paused = true;

		// After hovering, at least two more seconds to read or click.
		public void Resume()
		{
			Paused = false;
			if (_left < TimeSpan.FromSeconds(2))
			{
				_left = TimeSpan.FromSeconds(2);
			}
		}
	}
}
