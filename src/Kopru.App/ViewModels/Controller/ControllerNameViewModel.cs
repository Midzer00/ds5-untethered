// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// A controller by its id (the per-controller profile key) and model.
public sealed record ControllerRef(string Id, bool IsEdge);

// A controller's name with in-place renaming (the pencil next to it): Edit starts with the current name, Save keeps the
// draft (trimmed, at most ControllerRegistry.MaxNameLength characters, empty = the model name), Cancel leaves it.
// Target null (no controller): the fallback text shows and nothing can be edited.
public sealed partial class ControllerNameViewModel(SettingsState settings, Func<ControllerRef?> target, Func<string> fallback) : ViewModelBase
{
	private string _shownName = string.Empty;

	public ControllerNameViewModel(SettingsState settings, ControllerRef target) : this(settings, () => target, () => string.Empty)
	{
	}

	public string Name => target() is { } t ? settings.ControllerName(t.Id, t.IsEdge) : fallback();

	public bool CanEdit => target() != null;

	public int MaxLength => ControllerRegistry.MaxNameLength;

	public string EditTip => Text.ControllerRename();

	public string DraftHint => Text.ControllerNameHint();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsShowing))]
	private bool _isEditing;

	public bool IsShowing => !IsEditing;

	[ObservableProperty]
	private string _draft = string.Empty;

	[RelayCommand]
	private void Edit()
	{
		if (!CanEdit)
		{
			return;
		}
		Draft = Name;
		IsEditing = true;
	}

	public void Save()
	{
		if (!IsEditing)
		{
			return;
		}
		IsEditing = false;
		if (target() is { } t)
		{
			settings.RenameController(t.Id, Draft);
		}
		Refresh();
	}

	public void Cancel()
	{
		IsEditing = false;
		Draft = string.Empty;
	}

	// Re-reads the name and the target (raises PropertyChanged only when they changed).
	public void Refresh()
	{
		string now = Name + "\n" + CanEdit;
		if (now == _shownName)
		{
			return;
		}
		_shownName = now;
		OnPropertyChanged(nameof(Name));
		OnPropertyChanged(nameof(CanEdit));
		if (!CanEdit && IsEditing)
		{
			Cancel();
		}
	}

	protected override void OnLanguageChanged()
	{
		_shownName = string.Empty;
		base.OnLanguageChanged();
	}
}
