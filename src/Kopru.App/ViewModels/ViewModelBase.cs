// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// Base for view models that expose localized text: on a language switch every property is re-read by the bindings.
public abstract class ViewModelBase : ObservableObject
{
	protected ViewModelBase()
	{
		Localizer.LanguageChanged += _ => RunOnUi(OnLanguageChanged);
	}

	protected virtual void OnLanguageChanged() => OnPropertyChanged(string.Empty);

	protected static void RunOnUi(Action action)
	{
		if (Dispatcher.UIThread.CheckAccess())
		{
			action();
		}
		else
		{
			Dispatcher.UIThread.Post(action);
		}
	}
}

// One page of the main window (shown in the content area, selected from the sidebar).
public abstract class PageViewModel : ViewModelBase
{
	public abstract string Title { get; }
}
