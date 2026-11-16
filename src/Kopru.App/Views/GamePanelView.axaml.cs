// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;

namespace Kopru.App.Views;

// A game's settings in the Games page's pop-up (see GamePanelView.axaml).
public partial class GamePanelView : UserControl
{
	// From this width the settings sit in a column of their own beside the game (LeftWidth wide).
	public const double TwoColumnWidth = 720;
	public const double LeftWidth = 320;
	public const double ColumnGap = 32;

	public GamePanelView()
	{
		InitializeComponent();
		SizeChanged += (_, e) => Arrange(e.NewSize.Width >= TwoColumnWidth);
		Arrange(false);
	}

	public bool IsTwoColumns { get; private set; }

	// Puts the keyboard on Close, so Esc reaches the page and Tab starts in the panel.
	public void FocusClose() => ClosePanelButton.Focus();

	// Two columns (the game left, its settings right) or one (the settings under the game), and the picture's size.
	private void Arrange(bool wide)
	{
		if (IsTwoColumns == wide && PanelColumns.ColumnDefinitions.Count > 0)
		{
			return;
		}
		IsTwoColumns = wide;
		PanelColumns.ColumnDefinitions.Clear();
		PanelColumns.RowDefinitions.Clear();
		if (wide)
		{
			PanelColumns.ColumnDefinitions.Add(new ColumnDefinition(LeftWidth, GridUnitType.Pixel));
			PanelColumns.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
			PanelColumns.ColumnSpacing = ColumnGap;
			PanelColumns.RowSpacing = 0;
		}
		else
		{
			PanelColumns.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
			PanelColumns.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
			PanelColumns.ColumnSpacing = 0;
			PanelColumns.RowSpacing = 14;
		}
		Grid.SetColumn(PanelRight, wide ? 1 : 0);
		Grid.SetRow(PanelRight, wide ? 0 : 1);
		PanelPicture.Width = wide ? 160 : 88;
		PanelPicture.Height = wide ? 75 : 41;
	}
}
