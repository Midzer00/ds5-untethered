// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Kopru.App.ViewModels;

namespace Kopru.App.Views;

// Ticks the light preview about 30 times a second while the page is shown in a shown window (the view model skips static
// modes; MainWindowViewModel.IsWindowShown says whether the window is on screen).
public partial class LightView : UserControl
{
	private readonly DispatcherTimer _preview = new() { Interval = TimeSpan.FromSeconds(1.0 / 30) };
	private bool _attached;
	private MainWindowViewModel? _main;

	public LightView()
	{
		InitializeComponent();
		_preview.Tick += (_, _) => (DataContext as LightViewModel)?.AdvancePreview();
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
