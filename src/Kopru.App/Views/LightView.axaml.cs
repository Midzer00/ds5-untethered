// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Kopru.App.ViewModels;

namespace Kopru.App.Views;

// Ticks the light preview about 30 times a second while the page is shown in a shown window (the view model skips static
// modes; MainWindowViewModel.IsWindowShown says whether the window is on screen), and opens the custom colour's pop-up (the
// colour disc and the hex code) when the Custom swatch is clicked, also while it is already chosen, or Enter or Space is
// pressed on it.
public partial class LightView : UserControl
{
	private readonly DispatcherTimer _preview = new() { Interval = TimeSpan.FromSeconds(1.0 / 30) };
	private bool _attached;
	private MainWindowViewModel? _main;

	public LightView()
	{
		InitializeComponent();
		_preview.Tick += (_, _) => (DataContext as LightViewModel)?.AdvancePreview();
		// The custom colour's hex code applies when the box loses focus (Enter applies it too; anything invalid goes back).
		CustomHexBox.LostFocus += (_, _) => (DataContext as LightViewModel)?.CommitHex();
		CustomColourFlyout.Opening += (_, _) => CustomColourPanel.DataContext = DataContext;
		CustomColourFlyout.Closed += (_, _) => (DataContext as LightViewModel)?.CommitHex();
		ColourSwatches.Tapped += (_, e) =>
		{
			if (e.Source is Visual v && v.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: CustomColourSwatch } item)
			{
				OpenCustomColour(item);
			}
		};
		ColourSwatches.KeyDown += (_, e) =>
		{
			if (e.Key is Key.Enter or Key.Space && DataContext is LightViewModel { IsCustomSelected: true })
			{
				OpenCustomColour();
				e.Handled = true;
			}
		};
	}

	// The pop-up with the colour disc and the hex code (a resource: its content is found through it).
	public Flyout CustomColourFlyout => (Flyout)Resources["CustomColourFlyout"]!;
	public Control CustomColourPanel => (Control)CustomColourFlyout.Content!;
	public TextBox CustomHexBox => CustomColourPanel.GetLogicalDescendants().OfType<TextBox>().Single();
	public Controls.ColourWheel ColourWheel => CustomColourPanel.GetLogicalDescendants().OfType<Controls.ColourWheel>().Single();
	public bool IsCustomColourOpen => CustomColourFlyout.IsOpen;

	// Opens the custom colour's pop-up under the Custom swatch (anchor: its swatch; null: found in the list).
	public void OpenCustomColour(Control? anchor = null)
	{
		if (DataContext is not LightViewModel vm)
		{
			return;
		}
		anchor ??= ColourSwatches.ContainerFromItem(vm.Custom);
		if (anchor == null)
		{
			return;
		}
		CustomColourPanel.DataContext = vm;
		CustomColourFlyout.ShowAt(anchor);
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		_attached = true;
		Follow(TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel);
		UpdatePreview();
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		_attached = false;
		Follow(null);
		UpdatePreview();
		base.OnDetachedFromVisualTree(e);
	}

	// The page view stays attached but hidden while another page is shown (PageHost): no ticks then.
	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == IsVisibleProperty)
		{
			UpdatePreview();
		}
	}

	public bool IsPreviewRunning => _preview.IsEnabled;

	private void Follow(MainWindowViewModel? main)
	{
		if (_main != null)
		{
			_main.PropertyChanged -= OnMainChanged;
		}
		_main = main;
		if (_main != null)
		{
			_main.PropertyChanged += OnMainChanged;
		}
	}

	private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(MainWindowViewModel.IsWindowShown) or null or "")
		{
			UpdatePreview();
		}
	}

	private void UpdatePreview() => _preview.IsEnabled = _attached && IsVisible && (_main?.IsWindowShown ?? true);
}
