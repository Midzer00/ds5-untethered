// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.App.Services;
using Kopru.Core.Bridge;

namespace Kopru.App.Tests;

// Several simulated controllers (empty list: the single-controller service).
public sealed partial class FakeControllerService
{
	public List<ControllerEntry> ControllerList { get; } = [];

	public string? SelectedId { get; set; }

	public Dictionary<string, ControllerSettings?> ControllerProfiles { get; } = [];

	public IReadOnlyList<ControllerEntry> Controllers => [.. ControllerList];

	public ControllerEntry? SelectedController => ControllerList.FirstOrDefault(c => c.Id == SelectedId) ?? ControllerList.FirstOrDefault();

	public void SelectController(string id)
	{
		SelectedId = id;
		RaiseStateChanged();
	}

	// How often a controller's own settings were pushed.
	public int ProfilePushes { get; private set; }

	public void SetControllerProfile(string id, ControllerSettings? settings)
	{
		ProfilePushes++;
		ControllerProfiles[id] = settings;
	}
}
