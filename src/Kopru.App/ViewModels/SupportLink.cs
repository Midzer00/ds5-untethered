// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.Input;
using Kopru.Core.Diagnostics;
using Kopru.Core.Localization;
using Kopru.Core.Setup;

namespace Kopru.App.ViewModels;

// Support (GitHub Sponsors): the pink "Destek Ol" pill in the sidebar, above the controller card, and the link in
// Settings > General > Updates. The app is free and stays free: a click opens the page in the browser, nothing else (no
// pop-up, no reminder, nothing counted).
public sealed partial class SupportLinkViewModel(IUrlLauncher launcher) : ViewModelBase
{
	public const string Url = "https://github.com/sponsors/Midzer00";

	// The project's GitHub page (the star button beside the pill).
	public const string RepoUrl = "https://github.com/Midzer00/ds5-untethered";

	public string StarTip => Text.StarOnGitHub();
	public string StarLabel => Text.StarLabel();

	[RelayCommand]
	private void OpenRepo()
	{
		try
		{
			launcher.Open(RepoUrl);
		}
		catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			AppLog.Write($"support: could not open the project page ({e.GetType().Name})");
		}
	}

	public string Label => Text.SupportLabel();
	public string Tip => Text.SupportTip();
	// The service's own name, the same in every language (like the product name).
	public string LinkText => "GitHub Sponsors";

	[RelayCommand]
	private void Open()
	{
		try
		{
			launcher.Open(Url);
		}
		catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			AppLog.Write($"support: could not open the sponsors page ({e.GetType().Name})");
		}
	}
}
