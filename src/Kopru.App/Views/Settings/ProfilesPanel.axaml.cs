// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Kopru.App.ViewModels;
using Kopru.Core.Localization;

namespace Kopru.App.Views;

public partial class ProfilesPanel : UserControl
{
	private static readonly FilePickerFileType JsonType = new("JSON") { Patterns = ["*.json"] };

	public ProfilesPanel()
	{
		InitializeComponent();
		DataContextChanged += (_, _) =>
		{
			if (DataContext is ProfilesViewModel vm)
			{
				vm.PickImportFile = PickImportAsync;
				vm.PickExportFile = PickExportAsync;
			}
		};
	}

	private async Task<string?> PickImportAsync()
	{
		if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
		{
			return null;
		}
		IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = Text.ProfileImport(),
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType(Text.ProfileFileType()) { Patterns = ["*.json"] }, JsonType]
		});
		return files.Count > 0 ? files[0].TryGetLocalPath() : null;
	}

	private async Task<string?> PickExportAsync(string suggested)
	{
		if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
		{
			return null;
		}
		IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = Text.ProfileExport(),
			SuggestedFileName = suggested,
			DefaultExtension = "json",
			FileTypeChoices = [new FilePickerFileType(Text.ProfileFileType()) { Patterns = ["*.json"] }]
		});
		return file?.TryGetLocalPath();
	}
}
