// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// One controller in the sidebar list and the page header selector.
public sealed class ControllerChipViewModel(ControllerEntry entry, bool selected, Action<string> select, string? name = null) : ViewModelBase
{
	public ControllerEntry Entry { get; } = entry;
	public string Id => Entry.Id;
	public int Slot => Entry.Slot;
	public string SlotText => Text.ControllerSlotName(Entry.Slot);
	public string SlotNumber => Entry.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
	// The user's name for it, or the model name.
	public string Name { get; } = name ?? entry.Name;
	public bool IsEdge => Entry.IsEdge;
	public bool HasBattery => Entry.HasBattery;
	public double BatteryPercent => Entry.BatteryPercent;
	public bool IsCharging => Entry.IsCharging;
	public string BatteryShort => Entry.HasBattery ? Text.BatteryValue(Math.Round(Entry.BatteryPercent)) : Text.BatteryUnknown();
	public bool IsSelected { get; } = selected;
	public IRelayCommand SelectCommand { get; } = new RelayCommand(() => select(entry.Id));
}

// A profile a controller can run; Id null is the global settings.
public sealed record ControllerProfileOption(string? Id, string Name)
{
	public override string ToString() => Name;
}

// Several controllers: the list (sidebar) and the selector chips with the selected controller's profile (page headers).
// Hidden with one controller, so the pages look as they always did.
public sealed partial class ControllerPickerViewModel : ViewModelBase
{
	private readonly IControllerService _service;
	private readonly SettingsState _settings;
	private string _signature = string.Empty;
	private bool _settingProfile;

	public ControllerPickerViewModel(IControllerService service, SettingsState settings)
	{
		_service = service;
		_settings = settings;
		NameEditor = new ControllerNameViewModel(settings, () => SelectedTarget, () => FallbackName);
		Refresh();
		service.StateChanged += (_, _) => RunOnUi(Refresh);
		settings.ProfilesChanged += () => RunOnUi(() =>
		{
			OnPropertyChanged(nameof(ProfileOptions));
			OnPropertyChanged(nameof(SelectedProfile));
		});
		// A controller renamed: the chips, the list and the selected name show it.
		settings.KnownControllersChanged += () => RunOnUi(Refresh);
	}

	// The selected controller (connected), or null.
	private ControllerRef? SelectedTarget => _service.IsConnected && _service.SelectedController is { } s ? new ControllerRef(s.Id, s.IsEdge) : null;

	// Without a controller list (or a controller): the service's model name, or "no controller".
	private string FallbackName => _service.IsConnected ? _service.DeviceName : Text.DeviceNoController();

	// The selected controller's name (the user's name or the model), as the sidebar card and the Overview hero show it.
	public string SelectedName => NameEditor.Name;

	// Renames the selected controller in place (the sidebar card's pencil).
	public ControllerNameViewModel NameEditor { get; }

	public IReadOnlyList<ControllerChipViewModel> Items { get; private set; } = [];

	public bool IsMulti => Items.Count > 1;

	public string Label => Text.ControllerSelectorLabel();
	public string Help => Text.ControllerSelectorHelp();
	public string ProfileLabel => Text.ControllerProfileLabel();
	public string CountText => Text.ControllerCount(Items.Count);

	public ControllerChipViewModel? Selected => Items.FirstOrDefault(i => i.IsSelected);

	public IReadOnlyList<ControllerProfileOption> ProfileOptions =>
		[new(null, Text.ControllerProfileGlobal()), .. _settings.Profiles.Profiles.Select(p => new ControllerProfileOption(p.Id, SettingsState.DisplayName(p)))];

	// The selected controller's profile (the global settings when it has none).
	public ControllerProfileOption? SelectedProfile
	{
		get
		{
			string? id = Selected is { } s ? _settings.ControllerProfileId(s.Id) : null;
			return ProfileOptions.FirstOrDefault(o => o.Id == id);
		}
		set
		{
			if (_settingProfile || value == null || Selected is not { } s)
			{
				return;
			}
			_settingProfile = true;
			try
			{
				_settings.AssignControllerProfile(s.Id, value.Id);
			}
			finally
			{
				_settingProfile = false;
			}
			OnPropertyChanged();
		}
	}

	public void Select(string id) => _service.SelectController(id);

	// Rebuilds the items when a controller, the selection, a battery level or the charging state changed.
	public void Refresh()
	{
		IReadOnlyList<ControllerEntry> list = _service.Controllers;
		string? selected = _service.SelectedController?.Id;
		string signature = string.Join("|", list.Select(c =>
			$"{c.Id}:{c.Slot}:{_settings.ControllerName(c)}:{c.HasBattery}:{Math.Round(c.BatteryPercent)}:{c.IsCharging}:{c.Id == selected}"))
			+ "|" + _service.IsConnected + ":" + _service.DeviceName;
		NameEditor.Refresh();
		if (signature == _signature)
		{
			return;
		}
		_signature = signature;
		Items = [.. list.OrderBy(c => c.Slot).Select(c => new ControllerChipViewModel(c, c.Id == selected, Select, _settings.ControllerName(c)))];
		OnPropertyChanged(string.Empty);
	}

	protected override void OnLanguageChanged()
	{
		_signature = string.Empty;
		Refresh();
		OnPropertyChanged(string.Empty);
	}
}

// The pages that act on the selected controller carry the selector.
public sealed partial class OverviewViewModel
{
	private ControllerPickerViewModel? _picker;

	public ControllerPickerViewModel Picker => _picker ??= new ControllerPickerViewModel(_service, Settings);
}

public sealed partial class LightViewModel
{
	private ControllerPickerViewModel? _picker;

	public ControllerPickerViewModel Picker => _picker ??= new ControllerPickerViewModel(_service, Settings);
}

public sealed partial class TesterViewModel
{
	private ControllerPickerViewModel? _picker;
	private string? _liveFor;

	public ControllerPickerViewModel Picker
	{
		get
		{
			if (_picker == null)
			{
				_picker = new ControllerPickerViewModel(_service, Settings);
				_liveFor = _service.SelectedController?.Id;
				// The live input follows the selection while the page is open.
				_service.StateChanged += (_, _) => RunOnUi(FollowSelection);
			}
			return _picker;
		}
	}

	private void FollowSelection()
	{
		string? now = _service.SelectedController?.Id;
		if (now == _liveFor)
		{
			return;
		}
		_liveFor = now;
		if (IsVisible)
		{
			SetVisible(false);
			SetVisible(true);
		}
	}
}

// The sidebar's controller list.
public sealed partial class MainWindowViewModel
{
	private ControllerPickerViewModel? _picker;

	public ControllerPickerViewModel Picker => _picker ??= new ControllerPickerViewModel(_service, Settings);
}
