// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;

namespace Kopru.App.Views;

public partial class MainWindow : Window
{
	public MainWindow() => InitializeComponent();

	// The "glass" class switches to translucent surfaces when the system backdrop (acrylic or mica) is active;
	// without it the window keeps its opaque graphite background.
	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == ActualTransparencyLevelProperty)
		{
			Classes.Set("glass", ActualTransparencyLevel != WindowTransparencyLevel.None);
		}
	}

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
