// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kopru.App.Services;
using Kopru.Core.Localization;

namespace Kopru.App.ViewModels;

// One remembered controller in the Settings list: its name (renamed in place), model, connected or last seen, last
// battery, its own profile, and Forget (after a confirm; not while connected).
public sealed partial class KnownControllerRowViewModel : ViewModelBase
{
	private readonly SettingsState _settings;
	private KnownController _controller;
	private bool _connected;

	public KnownControllerRowViewModel(SettingsState settings, KnownController controller, bool connected)
	{
		_settings = settings;
		_controller = controller;
		_connected = connected;
		NameEditor = new ControllerNameViewModel(settings, new ControllerRef(controller.Id, controller.IsEdge));
		NameEditor.Refresh();
	}

	public string Id => _controller.Id;

	public ControllerNameViewModel NameEditor { get; }

	public string ModelText => KnownController.ModelName(_controller.IsEdge);

	public bool IsConnected => _connected;

	public string StatusText => _connected ? Text.ControllerConnectedNow() : Text.ControllerLastSeen(Ago(_controller.LastSeen, _settings.KnownControllers.Clock()));

	public bool HasBattery => _controller.LastBattery != null;

	public string BatteryText => _controller.LastBattery is { } b ? Text.ControllerLastBattery(b) : string.Empty;

	public string ProfileText => _settings.ControllerProfileId(_controller.Id) is { } id && _settings.Profiles.Find(id) is { } p
		? Text.ControllerProfileLine(SettingsState.DisplayName(p)) : string.Empty;

	public bool HasProfile => ProfileText.Length > 0;

	public bool CanForget => !_connected;

	public string ForgetText => Text.ControllerForget();

	public string CancelText => Text.ControllerForgetCancel();

	public string ForgetTip => _connected ? Text.ControllerForgetConnectedTip() : Text.ControllerForgetTip();

	public string ForgetAsk => Text.ControllerForgetAsk(NameEditor.Name);

	// Forget was pressed: the row asks before it forgets.
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsNotConfirming))]
	private bool _isConfirming;

	public bool IsNotConfirming => !IsConfirming;

	[RelayCommand]
	private void Forget()
	{
		if (CanForget)
		{
			IsConfirming = true;
		}
	}

	[RelayCommand]
	private void ConfirmForget()
	{
		IsConfirming = false;
		_settings.ForgetController(_controller.Id);
	}

	[RelayCommand]
	private void CancelForget() => IsConfirming = false;

	public void Update(KnownController controller, bool connected)
	{
		_controller = controller;
		_connected = connected;
		if (connected)
		{
			IsConfirming = false;
		}
		NameEditor.Refresh();
		foreach (string p in new[] { nameof(ModelText), nameof(IsConnected), nameof(StatusText), nameof(HasBattery), nameof(BatteryText), nameof(ProfileText),
			nameof(HasProfile), nameof(CanForget), nameof(ForgetTip), nameof(ForgetAsk) })
		{
			OnPropertyChanged(p);
		}
	}

	// "just now", "5 min ago", "2 h ago", "3 days ago".
	public static string Ago(DateTimeOffset then, DateTimeOffset now)
	{
		TimeSpan span = now - then;
		return span.TotalMinutes < 1 ? Text.AgoJustNow()
			: span.TotalHours < 1 ? Text.AgoMinutes(Math.Floor(span.TotalMinutes))
			: span.TotalDays < 1 ? Text.AgoHours(Math.Floor(span.TotalHours))
			: Text.AgoDays(Math.Floor(span.TotalDays));
	}
}

// The Settings section with every remembered controller. Rows are kept and updated in place, so a name being edited is
// not lost when a battery reading comes in.
public sealed class ControllersPanelViewModel : ViewModelBase
{
	private readonly SettingsState _settings;

	public ControllersPanelViewModel(SettingsState settings)
	{
		_settings = settings;
		Sync();
		settings.KnownControllersChanged += () => RunOnUi(Sync);
		settings.ProfilesChanged += () => RunOnUi(Sync);
	}

	public string Title => Text.ControllersSection();

	public string Hint => Text.ControllersHint();

	public string EmptyText => Text.ControllersEmpty();

	public ObservableCollection<KnownControllerRowViewModel> Rows { get; } = [];

	public bool IsEmpty => Rows.Count == 0;

	public bool HasRows => Rows.Count > 0;

	// Matches the rows to the registry: same order keeps every row and updates it, otherwise the list is rebuilt.
	public void Sync()
	{
		ControllerRegistry registry = _settings.KnownControllers;
		IReadOnlyList<KnownController> items = registry.Items;
		if (items.Select(i => i.Id).SequenceEqual(Rows.Select(r => r.Id), StringComparer.OrdinalIgnoreCase))
		{
			for (int i = 0; i < items.Count; i++)
			{
				Rows[i].Update(items[i], registry.IsConnected(items[i].Id));
			}
			return;
		}
		Dictionary<string, KnownControllerRowViewModel> old = Rows.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
		Rows.Clear();
		foreach (KnownController c in items)
		{
			bool connected = registry.IsConnected(c.Id);
			if (old.TryGetValue(c.Id, out KnownControllerRowViewModel? row))
			{
				row.Update(c, connected);
			}
			else
			{
				row = new KnownControllerRowViewModel(_settings, c, connected);
			}
			Rows.Add(row);
		}
		OnPropertyChanged(nameof(IsEmpty));
		OnPropertyChanged(nameof(HasRows));
	}
}

public sealed partial class SettingsState
{
	private ControllersPanelViewModel? _controllersPanel;

	// The Settings section "Controllers".
	public ControllersPanelViewModel ControllersPanel => _controllersPanel ??= new ControllersPanelViewModel(this);
}
