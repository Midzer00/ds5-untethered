// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Kopru.App.ViewModels;
using Kopru.Core.Localization;

namespace Kopru.App.Views;

public partial class SettingsView : UserControl
{
	public SettingsView()
	{
		InitializeComponent();
		DataContextChanged += (_, _) =>
		{
			if (DataContext is SettingsViewModel vm)
			{
				vm.PickSavePath = PickSavePathAsync;
			}
		};
	}

	// The system save dialog for a zip file; null when cancelled or when there is no local path.
	private async Task<string?> PickSavePathAsync(string suggestedName)
	{
		if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
		{
			return null;
		}
		IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = Text.ExportDiagnostics(),
			SuggestedFileName = suggestedName,
			DefaultExtension = "zip",
			ShowOverwritePrompt = true,
			FileTypeChoices = [new FilePickerFileType("Zip") { Patterns = ["*.zip"] }]
		});
		return file?.TryGetLocalPath();
	}
}
