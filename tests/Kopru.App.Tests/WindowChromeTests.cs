// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.ViewModels;
using Kopru.App.Views;

namespace Kopru.App.Tests;

// The main window draws into the title bar area and keeps exactly one set of caption buttons: Windows' own. Avalonia's
// default chrome hint (PreferSystemChrome) shows a managed set (TitleBar with CaptionButtons, including a full-screen
// button) and hides the native one, but the system acrylic's frame brings the native buttons back, so both would show.
public class WindowChromeTests
{
	[AvaloniaFact]
	public void MainWindow_UsesTheSystemCaptionButtonsOnly()
	{
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };

		Assert.True(window.ExtendClientAreaToDecorationsHint);
		Assert.Equal(ExtendClientAreaChromeHints.SystemChrome, window.ExtendClientAreaChromeHints);
		Assert.False(window.ExtendClientAreaChromeHints.HasFlag(ExtendClientAreaChromeHints.PreferSystemChrome));

		window.Show();
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
		// No managed caption buttons are shown or take clicks (the headless platform never asks for managed decorations,
		// so this guards the tree; the hint above is what keeps them off on Windows).
		Assert.DoesNotContain(window.GetVisualDescendants(), v => v is TitleBar or CaptionButtons && v.IsEffectivelyVisible);

		// The drag band spans the top edge above both the sidebar and the content.
		Border band = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DragBand");
		Assert.True(band.IsEffectivelyVisible && band.IsHitTestVisible);
		Assert.Equal(window.Bounds.Width, band.Bounds.Width);
		Assert.Equal(0, band.Bounds.Top);
		window.Close();
	}
}
