// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;

namespace Kopru.App.Views;

// A game's panel on the Games page (see GamePanelView.axaml).
public partial class GamePanelView : UserControl
{
	public GamePanelView() => InitializeComponent();

	// Puts the keyboard on Close, so Esc reaches the page and Tab starts in the panel.
	public void FocusClose() => ClosePanelButton.Focus();
}
