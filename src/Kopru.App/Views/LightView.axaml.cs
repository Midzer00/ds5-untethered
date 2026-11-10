// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Kopru.App.ViewModels;

namespace Kopru.App.Views;

// Ticks the light preview about 30 times a second while the page is shown (the view model skips static modes).
public partial class LightView : UserControl
{
	private readonly DispatcherTimer _preview = new() { Interval = TimeSpan.FromSeconds(1.0 / 30) };
	private bool _attached;

	public LightView()
	{
		InitializeComponent();
		_preview.Tick += (_, _) => (DataContext as LightViewModel)?.AdvancePreview();
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		_attached = true;
		UpdatePreview();
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		_attached = false;
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

	private void UpdatePreview() => _preview.IsEnabled = _attached && IsVisible;
}
