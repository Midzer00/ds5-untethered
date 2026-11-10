// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Kopru.App.Views;

public partial class MainWindow : Window
{
	private bool _systemBackdrop;

	public MainWindow()
	{
		InitializeComponent();
		// A transparent surface for the system acrylic where Windows can draw it; opaque graphite elsewhere.
		TransparencyLevelHint = SystemBackdrop.IsSupported ? [WindowTransparencyLevel.Transparent] : [WindowTransparencyLevel.None];
		DragBand.PointerPressed += OnDragBandPressed;
	}

	// The top band stands in for the title bar: a drag moves the window (Windows snaps it as usual), a double click
	// maximizes or restores it. The caption buttons above it are Windows' own and take their clicks first.
	private void OnDragBandPressed(object? sender, PointerPressedEventArgs e)
	{
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
		{
			return;
		}
		if (e.ClickCount == 2)
		{
			if (CanResize)
			{
				WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
			}
		}
		else
		{
			BeginMoveDrag(e);
		}
		e.Handled = true;
	}

	protected override void OnOpened(EventArgs e)
	{
		base.OnOpened(e);
		_systemBackdrop = TryGetPlatformHandle() is { } handle && SystemBackdrop.TryApply(handle.Handle);
		if (!_systemBackdrop)
		{
			TransparencyLevelHint = [WindowTransparencyLevel.None];
		}
		UpdateGlass();
	}

	// The "glass" class switches to a translucent tint when the system acrylic is behind the window; without it the
	// window keeps its opaque graphite background.
	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == ActualTransparencyLevelProperty)
		{
			UpdateGlass();
		}
		else if (change.Property == OffScreenMarginProperty)
		{
			// Maximized, Windows places the window's edges off the screen; the layout stays inside the visible part.
			Root.Margin = OffScreenMargin;
		}
		else if (change.Property == WindowStateProperty && _systemBackdrop)
		{
			// After the window has applied its own frame for the new state.
			Dispatcher.UIThread.Post(() =>
			{
				if (TryGetPlatformHandle() is { } handle)
				{
					SystemBackdrop.ExtendFrame(handle.Handle);
				}
			}, DispatcherPriority.Background);
		}
	}

	private void UpdateGlass() => Classes.Set("glass", _systemBackdrop && ActualTransparencyLevel == WindowTransparencyLevel.Transparent);

	// Sizes the window to a share of the screen it opens on, so larger and high-resolution screens get a
	// larger window instead of a small one surrounded by empty desktop.
	public void FitToScreen()
	{
		if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen)
		{
			return;
		}
		// Called before the window is shown; WindowStartupLocation centres it.
		double scaling = screen.Scaling;
		PixelRect area = screen.WorkingArea;
		Width = Math.Clamp(area.Width / scaling * 0.72, MinWidth, 1680);
		Height = Math.Clamp(area.Height / scaling * 0.8, MinHeight, 1050);
	}
}
