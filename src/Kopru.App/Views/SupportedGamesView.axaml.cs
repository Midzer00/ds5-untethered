// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Kopru.App.ViewModels;
using Kopru.Core.Localization;

namespace Kopru.App.Views;

public partial class SupportedGamesView : UserControl
{
	public SupportedGamesView()
	{
		InitializeComponent();
		DataContextChanged += (_, _) =>
		{
			if (DataContext is SupportedGamesViewModel vm)
			{
				vm.PickExe = PickExeAsync;
				vm.PickFolder = PickFolderAsync;
			}
		};
		// The running apps are listed when the "Add game" menu opens.
		if (AddGameButton.Flyout is PopupFlyoutBase flyout)
		{
			flyout.Opening += (_, _) => (DataContext as SupportedGamesViewModel)?.RefreshRunningApps();
		}
		// The game's panel: beside the list where the card is wide, the whole card otherwise; it takes the keyboard when it
		// opens, and Esc closes it wherever the keyboard is in the window (while this page is shown).
		GamesArea.SizeChanged += (_, e) =>
		{
			_narrow = e.NewSize.Width < BesideWidth;
			PlacePanel();
		};
		AttachedToVisualTree += (_, _) => TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
		DetachedFromVisualTree += (_, e) => (e.Root as TopLevel)?.RemoveHandler(KeyDownEvent, OnKeyDown);
	}

	// The game's panel beside the list: PanelWidth wide, the list keeps at least ListMinWidth (its rows' pickers stack, its
	// header keeps the views only). Below that the panel takes the list's place. Layout units, as GamesArea's size.
	public const double PanelWidth = 340;
	public const double ListMinWidth = 400;
	public const double BesideWidth = PanelWidth + 12 + ListMinWidth;

	private bool _narrow;

	// Beside the list (PanelWidth wide), or in the list's place on a narrow page; as tall as its content either way (it scrolls
	// inside when that is taller than the page).
	private void PlacePanel()
	{
		bool open = _panel is { IsOpen: true };
		bool instead = _narrow && open;
		ListCard.IsVisible = !instead;
		ListCard.Classes.Set("compact", open && !instead);
		Grid.SetColumn(GamePanelCard, instead ? 0 : 1);
		Grid.SetColumnSpan(GamePanelCard, instead ? 2 : 1);
		GamePanelCard.Width = instead ? double.NaN : PanelWidth;
	}

	private GamePanelViewModel? _panel;

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (_panel != null)
		{
			_panel.PropertyChanged -= OnPanelChanged;
		}
		_panel = (DataContext as SupportedGamesViewModel)?.Panel;
		if (_panel != null)
		{
			_panel.PropertyChanged += OnPanelChanged;
			_panel.PickStartFile = PickStartFileAsync;
		}
		PlacePanel();
	}

	private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(GamePanelViewModel.IsOpen))
		{
			return;
		}
		PlacePanel();
		if (_panel is { IsOpen: true })
		{
			Dispatcher.UIThread.Post(GamePanel.FocusClose, DispatcherPriority.Loaded);
		}
	}

	private void OnKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape && _panel is { IsOpen: true } && IsEffectivelyVisible)
		{
			_panel.CloseCommand.Execute(null);
			e.Handled = true;
		}
	}

	// The system open dialog for an exe; null when cancelled or when there is no local path.
	private async Task<string?> PickExeAsync()
	{
		if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
		{
			return null;
		}
		IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = Text.AddGame(),
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType("exe") { Patterns = ["*.exe"] }]
		});
		return files.Count > 0 ? files[0].TryGetLocalPath() : null;
	}

	// The system open dialog for a game's start file (an exe or a shortcut); null when cancelled or without a local path.
	private async Task<string?> PickStartFileAsync()
	{
		if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
		{
			return null;
		}
		IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = Text.StartFileChoose().TrimEnd('…'),
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType(Text.StartFileTypes()) { Patterns = ["*.exe", "*.lnk", "*.url"] }]
		});
		return files.Count > 0 ? files[0].TryGetLocalPath() : null;
	}

	// The system folder dialog for a folder to look for games in; null when cancelled or when there is no local path.
	private async Task<string?> PickFolderAsync()
	{
		if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
		{
			return null;
		}
		IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
		{
			Title = Text.ChooseGameFolder(),
			AllowMultiple = false
		});
		return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
	}
}
