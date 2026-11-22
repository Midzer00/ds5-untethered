// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kopru.App.ViewModels;
using Kopru.Core.Localization;

namespace Kopru.App.Views;

public partial class ControllerView : UserControl
{
	private ControllerLookViewModel? _look;

	public ControllerView()
	{
		InitializeComponent();
		// The colour tile's DualSense is a picture three times its size: scaled down smoothly.
		RenderOptions.SetBitmapInterpolationMode(LookIcon, BitmapInterpolationMode.HighQuality);
		// The look picker: a click on the backdrop closes it; Esc closes it wherever the keyboard is in the window (while this
		// page is shown).
		LookBackdrop.PointerPressed += (_, e) =>
		{
			_look?.CloseCommand.Execute(null);
			e.Handled = true;
		};
		AttachedToVisualTree += (_, _) => TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
		DetachedFromVisualTree += (_, e) => (e.Root as TopLevel)?.RemoveHandler(KeyDownEvent, OnKeyDown);
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (_look != null)
		{
			_look.PropertyChanged -= OnLookChanged;
			_look.PickImage = null;
		}
		_look = (DataContext as ControllerPageViewModel)?.Look;
		if (_look != null)
		{
			_look.PropertyChanged += OnLookChanged;
			_look.PickImage = PickImageAsync;
		}
	}

	// The keyboard goes to the chosen tile (scrolled into view) when the picker opens and back to the colour tile when it
	// closes.
	private void OnLookChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(ControllerLookViewModel.IsOpen))
		{
			return;
		}
		if (_look is { IsOpen: true })
		{
			Dispatcher.UIThread.Post(FocusChosenTile, DispatcherPriority.Loaded);
		}
		else
		{
			Dispatcher.UIThread.Post(() => LookTileButton.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
		}
	}

	public void FocusChosenTile()
	{
		Button? tile = LookDialog.GetVisualDescendants().OfType<Button>()
			.Where(b => b.Classes.Contains("lookTile"))
			.OrderByDescending(b => b.DataContext is PadLookTile { IsSelected: true })
			.FirstOrDefault();
		// The chosen tile is scrolled into view (far down a long pack) and gets the keyboard.
		tile?.BringIntoView();
		(tile ?? (Control)LookDoneButton).Focus(NavigationMethod.Tab);
	}

	private void OnKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape && _look is { IsOpen: true } && IsEffectivelyVisible)
		{
			_look.CloseCommand.Execute(null);
			e.Handled = true;
		}
	}

	// The system open dialog for a picture; its content, or null when cancelled.
	private async Task<Stream?> PickImageAsync()
	{
		if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
		{
			return null;
		}
		IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = Text.LookFileTitle(),
			AllowMultiple = false,
			FileTypeFilter =
			[
				new FilePickerFileType(Text.LookFileTypes())
				{
					Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"],
					MimeTypes = ["image/png", "image/jpeg", "image/webp"]
				}
			]
		});
		return files.Count > 0 ? await files[0].OpenReadAsync() : null;
	}
}
