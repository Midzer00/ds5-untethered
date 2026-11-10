// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
				vm.SectionRequested += _ => Dispatcher.UIThread.Post(ShowPendingSection, DispatcherPriority.Loaded);
				Dispatcher.UIThread.Post(ShowPendingSection, DispatcherPriority.Loaded);
			}
		};
	}

	// Scrolls the requested group (with its title above it) to the top of the page.
	private void ShowPendingSection()
	{
		if (DataContext is not SettingsViewModel vm || !vm.TryGetPendingSection(out string name) || !IsVisible)
		{
			return;
		}
		Control? panel = this.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
		ScrollViewer? scroll = this.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
		if (panel == null || scroll == null || panel.Bounds.Height <= 0 || scroll.Viewport.Height <= 0)
		{
			// Not laid out yet (the page was just built or shown): again after the next layout pass.
			if (!_waitingForLayout)
			{
				_waitingForLayout = true;
				LayoutUpdated += OnLayoutUpdated;
			}
			return;
		}
		vm.ClearPendingSection();
		panel.BringIntoView(new Rect(0, -40, panel.Bounds.Width, Math.Max(panel.Bounds.Height, scroll.Viewport.Height)));
	}

	private bool _waitingForLayout;

	private void OnLayoutUpdated(object? sender, EventArgs e)
	{
		LayoutUpdated -= OnLayoutUpdated;
		_waitingForLayout = false;
		Dispatcher.UIThread.Post(ShowPendingSection, DispatcherPriority.Background);
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == IsVisibleProperty && IsVisible)
		{
			Dispatcher.UIThread.Post(ShowPendingSection, DispatcherPriority.Loaded);
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
