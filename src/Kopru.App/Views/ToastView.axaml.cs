// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Kopru.App.Views;

// A toast card. Clicking the card raises Activated, the close button Dismissed.
public partial class ToastView : UserControl
{
	public ToastView()
	{
		InitializeComponent();
		CloseButton.Click += OnClose;
		CompactCloseButton.Click += OnClose;
		Card.PointerReleased += (_, e) =>
		{
			if (e.InitialPressMouseButton == MouseButton.Left)
			{
				Activated?.Invoke(this, EventArgs.Empty);
			}
		};
	}

	public event EventHandler? Activated;

	public event EventHandler? Dismissed;

	private void OnClose(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		Dismissed?.Invoke(this, EventArgs.Empty);
	}
}
