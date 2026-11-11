// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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
		// On Windows the base constructor already sets ClientSize (and may set other window properties) before
		// InitializeComponent has created the named parts, so the property handlers skip until now; apply what they
		// skipped once here, and again when the window opens with its real size.
		ApplyLayoutState(force: true);
	}

	// The current content scale (see UiScale).
	public double ContentScale { get; private set; } = 1;

	// True once the named parts exist (InitializeComponent has run).
	private bool PartsReady => ScaleHost != null && Pages != null && Root != null;

	// Scale for the current client size, and the maximized off-screen margin. Safe to call at any time.
	private void ApplyLayoutState(bool force = false)
	{
		if (!PartsReady)
		{
			return;
		}
		double factor = UiScale.Factor(ClientSize.Width, ClientSize.Height);
		if (force || Math.Abs(factor - ContentScale) > 0.001)
		{
			ContentScale = factor;
			ScaleHost.LayoutTransform = new ScaleTransform(factor, factor);
			PopupScale.Instance.Set(factor);
			// The top inset clears Windows' caption buttons, which do not scale: keep it 32 DIPs on screen.
			Pages.Margin = new Thickness(0, CaptionHeight / factor, 0, 0);
		}
		Root.Margin = OffScreenMargin;
	}

	private const double CaptionHeight = 32;

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
		ApplyLayoutState(force: true);
	}

	// The "glass" class switches to a translucent tint when the system acrylic is behind the window; without it the
	// window keeps its opaque graphite background.
	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == ClientSizeProperty)
		{
			ApplyLayoutState();
		}
		if (change.Property == ActualTransparencyLevelProperty)
		{
			UpdateGlass();
		}
		else if (change.Property == OffScreenMarginProperty)
		{
			// Maximized, Windows places the window's edges off the screen; the layout stays inside the visible part.
			ApplyLayoutState();
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
