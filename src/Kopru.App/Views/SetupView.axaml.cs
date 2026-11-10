// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Threading;
using Kopru.App.ViewModels;

namespace Kopru.App.Views;

public partial class SetupView : UserControl
{
	public SetupView()
	{
		InitializeComponent();
		// Opened from a health fix: scroll to that step once laid out.
		AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(ScrollToFocus, DispatcherPriority.Loaded);
	}

	private void ScrollToFocus()
	{
		if (DataContext is not SetupViewModel { FocusStep: { } focus } vm)
		{
			return;
		}
		int index = vm.Steps.ToList().FindIndex(s => s.Id == focus);
		if (index >= 0 && StepList.ContainerFromIndex(index) is { } container)
		{
			container.BringIntoView();
		}
	}
}
