// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Kopru.App.Views;

public partial class TesterView : UserControl
{
	public TesterView()
	{
		InitializeComponent();
		LayoutUpdated += (_, _) => UpdateTargetHeight();
	}

	// The cards fill what the page's viewport has left below them (above the page's bottom margin), so the tab fits the
	// window without scrolling; TesterLayout falls back to the cards' natural height when that is too little.
	private void UpdateTargetHeight()
	{
		if (!IsEffectivelyVisible || this.FindAncestorOfType<ScrollViewer>() is not { } scroll || scroll.Content is not Layoutable column
			|| Cards.TranslatePoint(default, column) is not { } top)
		{
			return;
		}
		double target = Math.Floor(scroll.Viewport.Height - column.Margin.Top - column.Margin.Bottom - top.Y);
		if (target > 0 && (double.IsNaN(Cards.TargetHeight) || Math.Abs(target - Cards.TargetHeight) >= 1))
		{
			Cards.TargetHeight = target;
		}
	}
}
