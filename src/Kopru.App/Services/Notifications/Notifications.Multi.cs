// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Localization;

namespace Kopru.App.Services;

// Connection notifications with several controllers: one per controller joining or leaving, titled with its slot
// ("Controller 2 Connected"). A single controller in slot 1 gets the usual titles. Services without a controller list
// keep the single-controller tracking.
public sealed partial class NotificationCenter
{
	private readonly Dictionary<string, int> _controllers = new(StringComparer.OrdinalIgnoreCase);
	// Slot notifications skip the per-type cooldown (two controllers may join together); repeats still count.
	private readonly HashSet<AppNotification> _slotNotices = new(ReferenceEqualityComparer.Instance);
	private bool _listSeen;

	private void InitControllers()
	{
		foreach (ControllerEntry c in _service.Controllers)
		{
			_controllers[c.Id] = c.Slot;
		}
		_listSeen = _controllers.Count > 0;
	}

	// Caller holds _gate.
	private bool IsSlotNotice(AppNotification n) => _slotNotices.Contains(n);

	// Caller holds _gate. False when the service has no controller list (the single-controller check runs instead).
	private bool CheckControllers(List<AppNotification> pending)
	{
		IReadOnlyList<ControllerEntry> now = _service.Controllers;
		if (now.Count == 0 && !_listSeen)
		{
			return false;
		}
		_listSeen = true;
		bool many = now.Count > 1 || _controllers.Count > 1;
		foreach (ControllerEntry c in now)
		{
			if (_controllers.ContainsKey(c.Id))
			{
				continue;
			}
			double? battery = c.HasBattery ? Math.Round(c.BatteryPercent) : null;
			if (!many && c.Slot == 1)
			{
				pending.Add(new AppNotification(NotificationType.Connected, NotificationKind.Success, Text.NotifyConnectedTitle(),
					Text.NotifyConnectedBody(_settings.ControllerName(c)), AppPage.Overview, battery, c.IsEdge, battery != null && c.IsCharging));
			}
			else
			{
				AppNotification n = new(NotificationType.Connected, NotificationKind.Success, Text.NotifyControllerConnectedTitle(c.Slot),
					Text.NotifyConnectedBody(_settings.ControllerName(c)), AppPage.Overview, battery, c.IsEdge, battery != null && c.IsCharging);
				_slotNotices.Add(n);
				pending.Add(n);
			}
		}
		foreach ((string id, int slot) in _controllers)
		{
			if (now.Any(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}
			if (!many && slot == 1)
			{
				if (!RecentlyRaised(NotificationType.PoweredOffIdle))
				{
					pending.Add(new AppNotification(NotificationType.Disconnected, NotificationKind.Info, Text.NotifyDisconnectedTitle(),
						Text.NotifyDisconnectedBody(), AppPage.Overview, IsEdge: IsEdge));
				}
			}
			else
			{
				AppNotification n = new(NotificationType.Disconnected, NotificationKind.Info, Text.NotifyControllerDisconnectedTitle(slot),
					Text.NotifyDisconnectedBody(), AppPage.Overview, IsEdge: IsEdge);
				_slotNotices.Add(n);
				pending.Add(n);
			}
		}
		_controllers.Clear();
		foreach (ControllerEntry c in now)
		{
			_controllers[c.Id] = c.Slot;
		}
		_connected = _service.IsConnected;
		return true;
	}
}
