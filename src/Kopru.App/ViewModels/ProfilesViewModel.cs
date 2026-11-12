// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.Core.Localization;
using Kopru.Core.Profiles;

namespace Kopru.App.ViewModels;

// A profile as a choice in a picker.
public sealed record ProfileOption(string Id, string Name)
{
	public override string ToString() => Name;
}

// One row of the profiles list: name (renamed in place), active mark or Activate, and a menu with the other actions.
public sealed partial class ProfileItemViewModel : ObservableObject
{
	private readonly ProfilesViewModel _owner;

	public ProfileItemViewModel(ProfilesViewModel owner, ControllerProfile profile, int index, bool isActive, int games)
	{
		_owner = owner;
		Id = profile.Id;
		IsDefault = profile.IsDefault;
		Name = SettingsState.DisplayName(profile);
		Index = index;
		IsActive = isActive;
		GamesText = IsDefault ? Text.ProfileDefaultNote() : games > 0 ? Text.ProfileGames(games) : Text.ProfileNoGames();
		_editName = Name;
	}

	public string Id { get; }
	public bool IsDefault { get; }
	public bool CanEdit => !IsDefault;
	public string Name { get; }
	public int Index { get; }
	// The number the player LEDs show for it (1-5, then again from 1).
	public string IndexText => ((Index - 1) % 5 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
	public bool IsActive { get; }
	public bool IsInactive => !IsActive;
	public string GamesText { get; }
	public string ActiveText => Text.ProfileActive();
	public string ActivateText => Text.ProfileActivate();
	public string RenameText => Text.ProfileRename();
	public string DuplicateText => Text.ProfileDuplicate();
	public string ExportText => Text.ProfileExport();
	public string DeleteText => Text.ProfileDelete();
	public string SaveText => Text.ProfileSave();
	public string CancelText => Text.ProfileCancel();
	public string MoreText => Text.ProfileMore();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsShowingName))]
	private bool _isRenaming;

	[ObservableProperty]
	private string _editName;

	public bool IsShowingName => !IsRenaming;

	[RelayCommand]
	private void Activate() => _owner.Activate(Id);

	[RelayCommand]
	private void BeginRename()
	{
		EditName = Name;
		IsRenaming = true;
	}

	[RelayCommand]
	private void CommitRename()
	{
		IsRenaming = false;
		_owner.Rename(Id, EditName);
	}

	[RelayCommand]
	private void CancelRename() => IsRenaming = false;

	[RelayCommand]
	private void Duplicate() => _owner.Duplicate(Id);

	[RelayCommand]
	private void Delete() => _owner.Delete(Id);

	[RelayCommand]
	private Task Export() => _owner.ExportAsync(Id);
}

// The profiles panel (Settings): list, new, import and the row actions. File dialogs come from the view.
public sealed partial class ProfilesViewModel : ViewModelBase
{
	private readonly SettingsState _settings;

	public ProfilesViewModel(SettingsState settings)
	{
		_settings = settings;
		Rebuild();
		settings.ProfilesChanged += Rebuild;
	}

	public ObservableCollection<ProfileItemViewModel> Items { get; } = [];

	public string Title => Text.ProfilesSection();
	public string Hint => Text.ProfilesHint();
	public string Short => Text.ProfilesShort();
	public string ProfileLabel => Text.ProfileLabel();
	public string NewText => Text.ProfileNew();
	public string ImportText => Text.ProfileImport();

	// The outcome of the last import or export, or empty.
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasStatus))]
	private string _statusText = string.Empty;

	[ObservableProperty]
	private bool _statusIsError;

	public bool HasStatus => StatusText.Length > 0;

	// Open dialog for a profile file: the path, or null when cancelled.
	public Func<Task<string?>>? PickImportFile { get; set; }

	// Save dialog for a profile file (suggested file name): the path, or null when cancelled.
	public Func<string, Task<string?>>? PickExportFile { get; set; }

	// A new profile from the current settings, active at once.
	[RelayCommand]
	private void New()
	{
		ProfileBook book = _settings.Profiles;
		int n = book.Profiles.Count;
		string name;
		do
		{
			name = Text.ProfileNewName(n++);
		}
		while (book.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)));
		if (book.Create(name, _settings.CaptureValues()) is { } created)
		{
			_settings.ActivateProfile(created.Id);
		}
	}

	[RelayCommand]
	private async Task Import()
	{
		if (PickImportFile is { } pick && await pick() is { } path)
		{
			ImportFile(path);
		}
	}

	public ControllerProfile? ImportFile(string path)
	{
		string json;
		try
		{
			json = File.ReadAllText(path);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			SetStatus(Text.ProfileImportFailed(), true);
			return null;
		}
		ControllerProfile? added = _settings.Profiles.Import(json, Path.GetFileNameWithoutExtension(path));
		SetStatus(added == null ? Text.ProfileImportFailed() : Text.ProfileImported(added.Name), added == null);
		return added;
	}

	public async Task ExportAsync(string id)
	{
		ControllerProfile? profile = _settings.Profiles.Find(id);
		if (profile == null || PickExportFile is not { } pick)
		{
			return;
		}
		string name = SettingsState.DisplayName(profile);
		if (await pick(SafeFileName(name) + ".json") is { } path)
		{
			ExportFile(id, path);
		}
	}

	public bool ExportFile(string id, string path)
	{
		ControllerProfile? profile = _settings.Profiles.Find(id);
		if (profile == null)
		{
			return false;
		}
		try
		{
			File.WriteAllText(path, _settings.Profiles.Export(id));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			SetStatus(e.Message, true);
			return false;
		}
		SetStatus(Text.ProfileExported(SettingsState.DisplayName(profile)), false);
		return true;
	}

	public void Activate(string id) => _settings.ActivateProfile(id);

	public void Rename(string id, string name) => _settings.Profiles.Rename(id, name);

	public void Duplicate(string id)
	{
		if (_settings.Profiles.Find(id) is { } p)
		{
			_settings.Profiles.Duplicate(id, Text.ProfileCopyName(SettingsState.DisplayName(p)));
		}
	}

	public void Delete(string id)
	{
		bool wasActive = _settings.Profiles.ActiveId == id;
		if (_settings.Profiles.Delete(id) && wasActive)
		{
			_settings.ActivateProfile(ProfileBook.DefaultId);
		}
	}

	protected override void OnLanguageChanged()
	{
		base.OnLanguageChanged();
		Rebuild();
	}

	private void Rebuild()
	{
		ProfileBook book = _settings.Profiles;
		IReadOnlyDictionary<string, string> games = book.GameProfiles;
		string active = book.ActiveId;
		Items.Clear();
		int i = 1;
		foreach (ControllerProfile p in book.Profiles)
		{
			Items.Add(new ProfileItemViewModel(this, p, i++, p.Id == active, games.Values.Count(v => v == p.Id)));
		}
	}

	private void SetStatus(string text, bool error)
	{
		StatusIsError = error;
		StatusText = text;
	}

	private static string SafeFileName(string name)
	{
		char[] bad = Path.GetInvalidFileNameChars();
		string s = new(name.Select(c => bad.Contains(c) ? '_' : c).ToArray());
		return s.Length == 0 ? "profile" : s;
	}
}

// The profile picker on a supported games row: Default or one of the profiles.
public sealed class GameProfilePicker : ObservableObject
{
	private readonly SettingsState _settings;
	private readonly string _key;

	public GameProfilePicker(SettingsState settings, string key)
	{
		_settings = settings;
		_key = key;
	}

	public IReadOnlyList<ProfileOption> Options => _settings.Profiles.Profiles
		.Select(p => new ProfileOption(p.Id, SettingsState.DisplayName(p))).ToList();

	public string Tip => Text.GameProfileTip();

	public ProfileOption? Selected
	{
		get
		{
			string id = _settings.Profiles.ResolveForGame(_key);
			return Options.FirstOrDefault(o => o.Id == id);
		}
		set
		{
			if (value != null)
			{
				_settings.Profiles.AssignGame(_key, value.Id);
			}
		}
	}

	public bool HasProfile => _settings.Profiles.ProfileForGame(_key) != null;

	public void Refresh()
	{
		OnPropertyChanged(nameof(Options));
		OnPropertyChanged(nameof(Selected));
		OnPropertyChanged(nameof(HasProfile));
		OnPropertyChanged(nameof(Tip));
	}
}

// One line of the shortcuts legend.
public sealed record ShortcutLegendItem(string Keys, string Does);

// The controller shortcuts group (Settings): the switch and the legend.
public sealed class ShortcutsViewModel : ViewModelBase
{
	public ShortcutsViewModel(SettingsState settings)
	{
		Settings = settings;
	}

	public SettingsState Settings { get; }
	public string Title => Text.ShortcutsSection();
	public string Hint => Text.ShortcutsHint();
	public string Short => Text.ShortcutsShort();
	public string OpenAppShort => Text.PsOpenAppShort();
	public string OpenAppLabel => Text.PsOpenApp();
	public string OpenAppHint => Text.PsOpenAppHint();
	public string InGamesLabel => Text.PsOpenAppInGames();
	public string InGamesShort => Text.PsOpenAppInGamesShort();
	public string InGamesHint => Text.PsOpenAppInGamesHint();

	public IReadOnlyList<ShortcutLegendItem> Legend =>
	[
		new(Text.ShortcutKeysProfile(), Text.ShortcutDoesProfile()),
		new(Text.ShortcutKeysHaptics(), Text.ShortcutDoesHaptics()),
		new(Text.ShortcutKeysMode(), Text.ShortcutDoesMode()),
		new(Text.ShortcutKeysPower(), Text.ShortcutDoesPower()),
		new(Text.ShortcutKeysDesktop(), Text.ShortcutDoesDesktop()),
	];
}
