// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.ViewModels;
using Kopru.App.Views;

namespace Kopru.App.Tests;

// The window content follows the window by one factor (UiScale), and popups follow it; the layout never gets less than
// the 1100x700 design size, down to the window's minimum.
public sealed class UiScaleTests
{
	[Theory]
	[InlineData(960, 640, 0.85)]   // the minimum window: scaled down (960 / 1100 = 0.873 -> 0.85)
	[InlineData(1000, 700, 0.90)]  // limited by the width (0.909 -> 0.90)
	[InlineData(1100, 700, 1.0)]   // the design size
	[InlineData(1400, 800, 1.10)]  // limited by the height (800 / 700 = 1.14 -> 1.10)
	[InlineData(1680, 1050, 1.50)]
	[InlineData(2560, 1440, 1.75)] // capped; the rest stays centred
	[InlineData(1300, 1300, 1.15)] // limited by the width (1300 / 1100 = 1.18 -> 1.15)
	public void Factor_FollowsTheSmallerRatio_InSteps_BetweenMinAndMax(double width, double height, double expected) =>
		Assert.Equal(expected, UiScale.Factor(width, height), 3);

	[Fact]
	public void Factor_IsOne_ForNoSize() => Assert.Equal(1, UiScale.Factor(0, 0));

	// At every allowed window size (from the minimum up, every 10 DIPs to 2600x1600) the layout gets at least 1100x700, and
	// the factor never rounds up past the exact ratio.
	[Fact]
	public void LayoutCanvas_IsNeverSmallerThanTheDesignSize()
	{
		for (double w = UiScale.MinWindowWidth; w <= 2600; w += 10)
		{
			for (double h = UiScale.MinWindowHeight; h <= 1600; h += 10)
			{
				double s = UiScale.Factor(w, h);
				Assert.True(s <= Math.Min(w / UiScale.DesignWidth, h / UiScale.DesignHeight) + 1e-9 || s == UiScale.MinFactor, $"{w}x{h}: {s}");
				if (s < UiScale.Max)
				{
					Assert.True(w / s >= UiScale.DesignWidth - 1e-6 && h / s >= UiScale.DesignHeight - 1e-6, $"{w}x{h}: canvas {w / s:F1}x{h / s:F1}");
				}
			}
		}
		Assert.Equal(0.85, UiScale.MinFactor, 3);
	}

	[AvaloniaFact]
	public void MinimumWindowSize_MatchesUiScale()
	{
		var window = new MainWindow();
		Assert.Equal((UiScale.MinWindowWidth, UiScale.MinWindowHeight), (window.MinWidth, window.MinHeight));
	}

	[AvaloniaFact]
	public void Window_ScalesItsContent_AndKeepsTheCaptionInset()
	{
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(1, window.ContentScale);

		foreach ((double width, double height, double scale) in new[] { (1680.0, 1050.0, 1.5), (960.0, 640.0, 0.85) })
		{
			window.Width = width;
			window.Height = height;
			Dispatcher.UIThread.RunJobs();
			window.UpdateLayout();
			Assert.Equal(scale, window.ContentScale);

			// The sidebar is 272 wide in layout terms and 272 x scale on screen.
			Control sidebar = window.GetVisualDescendants().OfType<DockPanel>().First();
			Point right = sidebar.TranslatePoint(new Point(sidebar.Bounds.Width, 0), window)!.Value;
			Assert.InRange(right.X, 272 * scale - 1, 272 * scale + 1);

			// The page content still starts 32 on screen below the top (the caption buttons do not scale).
			PageHost pages = window.GetVisualDescendants().OfType<PageHost>().Single();
			Point top = pages.TranslatePoint(new Point(0, 0), window)!.Value;
			Assert.InRange(top.Y, 31, 33);
		}
		window.Close();
	}

	// On Windows the base Window constructor sets ClientSize (and the off-screen margin can change) before InitializeComponent
	// has created the named parts. Headless does not do that, so this reproduces the order: the named parts are taken away
	// (as before InitializeComponent) and the size and off-screen margin change; nothing may throw, and once the parts are
	// back the next change applies the scale.
	[AvaloniaFact]
	public void SizeChanges_BeforeTheNamedPartsExist_AreSkippedNotFatal()
	{
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
		var window = new MainWindow { DataContext = vm, Width = 1100, Height = 700 };
		window.Show();
		Dispatcher.UIThread.RunJobs();

		string[] parts = ["ScaleHost", "Pages", "Root"];
		var fields = parts.Select(p => typeof(MainWindow).GetField(p, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!).ToList();
		Assert.All(fields, Assert.NotNull);
		var saved = fields.Select(f => f.GetValue(window)).ToList();
		fields.ForEach(f => f.SetValue(window, null));

		window.Width = 1680;
		window.Height = 1050;
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(1, window.ContentScale); // skipped, not thrown

		for (int i = 0; i < fields.Count; i++)
		{
			fields[i].SetValue(window, saved[i]);
		}
		window.Width = 1690;
		Dispatcher.UIThread.RunJobs();
		Assert.Equal(1.5, window.ContentScale);
		window.Close();
	}

	// The opened drop-down comes out at the same scale as its ComboBox: as wide, left edges equal, same text size on screen.
	[AvaloniaTheory]
	[InlineData(960, 640)]
	[InlineData(1100, 700)]
	[InlineData(1680, 1050)]
	public void DropDown_MatchesItsComboBox_OnScreen(double width, double height)
	{
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
		var window = new MainWindow { DataContext = vm, Width = width, Height = height };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();

		ComboBox combo = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && ReferenceEquals(c.ItemsSource, vm.Overview.Profiles));
		combo.IsDropDownOpen = true;
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
		Dispatcher.UIThread.RunJobs();

		Rect comboRect = ScreenRect(combo, window);
		Popup popup = combo.GetVisualDescendants().OfType<Popup>().First();
		Control child = (Control)popup.Child!;
		Rect popupRect = ScreenRect(child, window);
		Assert.True(Math.Abs(popupRect.Width - comboRect.Width) <= 2, $"drop-down {popupRect.Width} vs combo {comboRect.Width} (scale {window.ContentScale})");
		Assert.True(Math.Abs(popupRect.Left - comboRect.Left) <= 2, $"drop-down left {popupRect.Left} vs combo {comboRect.Left}");
		TextBlock comboText = combo.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible);
		TextBlock itemText = child.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible);
		Assert.True(Math.Abs(ScreenRect(itemText, window).Height / ScreenRect(comboText, window).Height - 1) < 0.15,
			$"item text {ScreenRect(itemText, window).Height} vs combo text {ScreenRect(comboText, window).Height}");
		combo.IsDropDownOpen = false;
		window.Close();
	}

	// A tooltip comes out at the window's content scale, starting near its control.
	[AvaloniaTheory]
	[InlineData(960, 640, 0.85)]
	[InlineData(1100, 700, 1.0)]
	[InlineData(1680, 1050, 1.5)]
	public void ToolTip_IsDrawnAtTheContentScale(double width, double height, double scale)
	{
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
		var window = new MainWindow { DataContext = vm, Width = width, Height = height };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();

		Control owner = window.GetVisualDescendants().OfType<Control>().First(c => c.IsEffectivelyVisible && ToolTip.GetTip(c) is string { Length: > 0 });
		ToolTip.SetIsOpen(owner, true);
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
		Dispatcher.UIThread.RunJobs();

		TextBlock tipText = window.GetVisualDescendants().OfType<OverlayPopupHost>().SelectMany(h => h.GetVisualDescendants()).OfType<TextBlock>()
			.First(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text));
		double drawn = ScreenRect(tipText, window).Height / tipText.Bounds.Height;
		Assert.InRange(drawn, scale - 0.02, scale + 0.02);
		ToolTip.SetIsOpen(owner, false);
		window.Close();
	}

	private static Rect ScreenRect(Visual v, Visual root) =>
		v.TransformToVisual(root) is { } m ? new Rect(v.Bounds.Size).TransformToAABB(m) : default;

	[AvaloniaFact]
	public void Popups_OpenInTheScaledLayer()
	{
		var vm = new MainWindowViewModel(new FakeControllerService(animate: false));
		var window = new MainWindow { DataContext = vm, Width = 1680, Height = 1050 };
		window.Show();
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();

		ComboBox combo = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && c.ItemCount > 0);
		combo.IsDropDownOpen = true;
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();

		Popup popup = combo.GetVisualDescendants().OfType<Popup>().First();
		Assert.True(popup.IsOpen);
		Visual? host = popup.Host as Visual;
		Assert.NotNull(host);
		// The drop-down opens in the window's own overlay layer (outside the scale host) and scales its own content.
		Assert.DoesNotContain(host!.GetVisualAncestors(), v => v is LayoutTransformControl { Name: "ScaleHost" });
		Assert.Equal(1.5, PopupScale.Instance.Factor);
		combo.IsDropDownOpen = false;
		window.Close();
	}
}
