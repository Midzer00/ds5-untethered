// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Kopru.App.Controls;

// Keeps a control's popup from scrolling the list the control sits in. When a ComboBox opens, its drop-down focuses the
// selected item and asks for it to be brought into view; that request bubbles out of the popup through the ComboBox (the
// popup's logical parent) to every ScrollViewer around it, which then scrolls by the item's position in the popup (a
// different visual tree): a list scrolled down jumps back to the top. With Contain set, such requests stop at the control;
// requests for the control itself or anything inside it still reach the ScrollViewer.
public static class PopupScrollGuard
{
	public static readonly AttachedProperty<bool> ContainProperty =
		AvaloniaProperty.RegisterAttached<Control, bool>("Contain", typeof(PopupScrollGuard));

	static PopupScrollGuard()
	{
		ContainProperty.Changed.AddClassHandler<Control>((control, e) =>
		{
			control.RemoveHandler(Control.RequestBringIntoViewEvent, OnRequestBringIntoView);
			if (e.NewValue is true)
			{
				control.AddHandler(Control.RequestBringIntoViewEvent, OnRequestBringIntoView);
			}
		});
	}

	public static bool GetContain(Control control) => control.GetValue(ContainProperty);

	public static void SetContain(Control control, bool value) => control.SetValue(ContainProperty, value);

	private static void OnRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
	{
		if (sender is Visual owner && e.TargetObject is { } target && !ReferenceEquals(target, owner) && !owner.IsVisualAncestorOf(target))
		{
			e.Handled = true;
		}
	}
}
