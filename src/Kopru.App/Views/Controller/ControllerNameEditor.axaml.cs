// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kopru.App.ViewModels;

namespace Kopru.App.Views;

// A controller's name with a pencil; the pencil turns it into a text box: Enter (or leaving the box) saves, Esc cancels.
public partial class ControllerNameEditor : UserControl
{
	private ControllerNameViewModel? _vm;

	public ControllerNameEditor()
	{
		InitializeComponent();
		TextBox box = this.FindControl<TextBox>("NameBox")!;
		box.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);
		box.LostFocus += (_, _) => _vm?.Save();
		DataContextChanged += (_, _) =>
		{
			if (_vm != null)
			{
				_vm.PropertyChanged -= OnVmChanged;
			}
			_vm = DataContext as ControllerNameViewModel;
			if (_vm != null)
			{
				_vm.PropertyChanged += OnVmChanged;
			}
		};
	}

	private void OnBoxKeyDown(object? sender, KeyEventArgs e)
	{
		if (_vm == null)
		{
			return;
		}
		if (e.Key == Key.Enter)
		{
			_vm.Save();
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			_vm.Cancel();
			e.Handled = true;
		}
	}

	// Editing started: the box takes the focus with the whole name selected.
	private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ControllerNameViewModel.IsEditing) && _vm?.IsEditing == true)
		{
			Dispatcher.UIThread.Post(() =>
			{
				TextBox box = this.FindControl<TextBox>("NameBox")!;
				box.Focus();
				box.SelectAll();
			}, DispatcherPriority.Loaded);
		}
	}
}
