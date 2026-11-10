// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Serialization;

namespace Kopru.App.Services;

public enum ControllerModel
{
	DualSense,
	DualSenseEdge
}

// One remembered controller as saved in settings.json (AppSettings.KnownControllers). Id is the same key the
// per-controller profile uses (the device instance id, see ControllerEntry.Id). Name is the user's name, null for the
// model name. LastBattery is in percent, null until a reading was seen. While connected LastSeen is when it connected.
public sealed record KnownController(string Id, ControllerModel Model, string? Name, DateTimeOffset FirstSeen, DateTimeOffset LastSeen,
	double? LastBattery = null)
{
	[JsonIgnore]
	public bool IsEdge => Model == ControllerModel.DualSenseEdge;

	public static string ModelName(bool isEdge) => isEdge ? "DualSense Edge" : "DualSense";

	[JsonIgnore]
	public string DisplayName => Name ?? ModelName(IsEdge);
}

// The controllers the app has seen: updated from the service's controller list (joins, leaves, battery), renamed and
// forgotten by the user, at most MaxRemembered (the one seen longest ago goes first). Thread-safe; Changed is raised
// after every change (saved by SettingsState), Forgotten for every controller removed (its own profile goes with it).
public sealed class ControllerRegistry
{
	public const int MaxRemembered = 16;
	public const int MaxNameLength = 24;

	private readonly object _gate = new();
	private readonly List<KnownController> _items = [];
	private readonly HashSet<string> _connected = new(StringComparer.OrdinalIgnoreCase);

	public ControllerRegistry(IEnumerable<KnownController>? saved = null)
	{
		foreach (KnownController c in saved ?? [])
		{
			if (!string.IsNullOrWhiteSpace(c.Id) && !_items.Any(i => SameId(i.Id, c.Id)))
			{
				_items.Add(c with { Name = Normalize(c.Name, c.IsEdge) });
			}
		}
		Prune([]);
	}

	// The wall clock (tests pass a fixed one).
	public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

	public event Action? Changed;

	public event Action<string>? Forgotten;

	// Connected ones first, then the most recently seen.
	public IReadOnlyList<KnownController> Items
	{
		get
		{
			lock (_gate)
			{
				return [.. _items.OrderByDescending(c => _connected.Contains(c.Id)).ThenByDescending(c => c.LastSeen)];
			}
		}
	}

	public KnownController? Find(string id)
	{
		lock (_gate)
		{
			return _items.FirstOrDefault(c => SameId(c.Id, id));
		}
	}

	public bool IsConnected(string id)
	{
		lock (_gate)
		{
			return _connected.Contains(id);
		}
	}

	// The user's name, or the model name.
	public string NameOf(string id, bool isEdge) => Find(id)?.Name ?? KnownController.ModelName(isEdge);

	// Follows the controllers present now: a new one is remembered, one that joins or leaves gets LastSeen, the battery is
	// kept (rounded). Changed only when something is different.
	public void Observe(IReadOnlyList<ControllerEntry> present)
	{
		List<string> forgotten;
		lock (_gate)
		{
			bool changed = false;
			DateTimeOffset now = Clock();
			HashSet<string> here = new(present.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
			foreach (ControllerEntry e in present)
			{
				double? battery = e.HasBattery ? Math.Round(e.BatteryPercent) : null;
				ControllerModel model = e.IsEdge ? ControllerModel.DualSenseEdge : ControllerModel.DualSense;
				int index = _items.FindIndex(c => SameId(c.Id, e.Id));
				if (index < 0)
				{
					_items.Add(new KnownController(e.Id, model, null, now, now, battery));
					changed = true;
				}
				else
				{
					KnownController c = _items[index];
					KnownController next = c with
					{
						Model = model,
						LastSeen = _connected.Contains(e.Id) ? c.LastSeen : now,
						LastBattery = battery ?? c.LastBattery
					};
					if (next != c)
					{
						_items[index] = next;
						changed = true;
					}
				}
			}
			foreach (string gone in _connected.Where(id => !here.Contains(id)).ToList())
			{
				int index = _items.FindIndex(c => SameId(c.Id, gone));
				if (index >= 0)
				{
					_items[index] = _items[index] with { LastSeen = now };
				}
				changed = true;
			}
			_connected.Clear();
			_connected.UnionWith(here);
			forgotten = Prune(here);
			if (!changed && forgotten.Count == 0)
			{
				return;
			}
		}
		Raise(forgotten);
	}

	// Sets the user's name: trimmed, at most MaxNameLength characters; empty (or the model name) is the model name again.
	public bool Rename(string id, string? name)
	{
		lock (_gate)
		{
			int index = _items.FindIndex(c => SameId(c.Id, id));
			if (index < 0)
			{
				return false;
			}
			KnownController c = _items[index];
			string? next = Normalize(name, c.IsEdge);
			if (next == c.Name)
			{
				return false;
			}
			_items[index] = c with { Name = next };
		}
		Raise([]);
		return true;
	}

	// Removes a controller that is not connected (its name and its own profile go too).
	public bool Forget(string id)
	{
		lock (_gate)
		{
			if (_connected.Contains(id) || _items.RemoveAll(c => SameId(c.Id, id)) == 0)
			{
				return false;
			}
		}
		Raise([id]);
		return true;
	}

	public static string? Normalize(string? name, bool isEdge)
	{
		string trimmed = new string((name ?? string.Empty).Where(ch => !char.IsControl(ch)).ToArray()).Trim();
		if (trimmed.Length > MaxNameLength)
		{
			trimmed = trimmed[..MaxNameLength].TrimEnd();
		}
		return trimmed.Length == 0 || trimmed == KnownController.ModelName(isEdge) ? null : trimmed;
	}

	// Caller holds the lock: drops the ones seen longest ago (never a connected one) beyond the limit.
	private List<string> Prune(HashSet<string> keep)
	{
		List<string> removed = [];
		while (_items.Count > MaxRemembered)
		{
			KnownController? oldest = _items.Where(c => !keep.Contains(c.Id)).MinBy(c => c.LastSeen);
			if (oldest == null)
			{
				break;
			}
			_items.Remove(oldest);
			removed.Add(oldest.Id);
		}
		return removed;
	}

	private void Raise(List<string> forgotten)
	{
		foreach (string id in forgotten)
		{
			Forgotten?.Invoke(id);
		}
		Changed?.Invoke();
	}

	private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
