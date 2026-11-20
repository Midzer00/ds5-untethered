// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Kopru.App.ViewModels;

namespace Kopru.App.Views;

public partial class ControllerInfoCard : UserControl
{
	public ControllerInfoCard() => InitializeComponent();

	// "Copy Details" writes through this window's clipboard.
	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		ConnectClipboard();
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		ConnectClipboard();
	}

	private void ConnectClipboard()
	{
		if (DataContext is ControllerInfoViewModel vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
		{
			vm.Clipboard = text => clipboard.SetTextAsync(text);
		}
	}
}
