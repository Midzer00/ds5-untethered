// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
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
			}
		};
		// The running apps are listed when the "Add game" menu opens.
		if (AddGameButton.Flyout is PopupFlyoutBase flyout)
		{
			flyout.Opening += (_, _) => (DataContext as SupportedGamesViewModel)?.RefreshRunningApps();
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
}
