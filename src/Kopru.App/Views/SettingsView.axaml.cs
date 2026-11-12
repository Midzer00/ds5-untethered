// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Kopru.App.Controls;
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
		LayoutUpdated += (_, _) => UpdateTargetHeight();
	}

	// Every tab's cards fit what the page's viewport has left below the tab switch (above the page's bottom margin), so a
	// tab never scrolls the page; all tabs sit at the same place, so they all get the same height.
	private void UpdateTargetHeight()
	{
		ScrollViewer scroll = (ScrollViewer)Content!;
		if (!IsEffectivelyVisible || scroll.Content is not Layoutable column || TabHost.TranslatePoint(default, column) is not { } top)
		{
			return;
		}
		double target = Math.Floor(scroll.Viewport.Height - column.Margin.Top - column.Margin.Bottom - top.Y);
		if (target <= 0)
		{
			return;
		}
		foreach (SettingsLayout tab in TabHost.Children.OfType<SettingsLayout>())
		{
			if (double.IsNaN(tab.TargetHeight) || Math.Abs(target - tab.TargetHeight) >= 1)
			{
				tab.TargetHeight = target;
			}
		}
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
