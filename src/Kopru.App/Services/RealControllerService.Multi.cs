// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Hid;
using Kopru.Core.Hiding;
using Kopru.Core.Localization;

namespace Kopru.App.Services;

// One controller of several (IControllerService.Controllers): its id (device instance id), player slot 1-4, name,
// battery and bridge state. HasOwnProfile: it runs its own profile instead of the global settings.
public sealed record ControllerEntry(string Id, int Slot, string Name, bool IsEdge, bool HasBattery, double BatteryPercent, bool IsCharging,
	BridgeState State, bool HasOwnProfile = false);

// Several controllers: BridgeManager runs one host per controller; the existing host is slot 1. The single-controller
// members describe the selected controller; settings go to every controller (or its own profile when it has one).
public sealed partial class RealControllerService
{
	public static readonly TimeSpan ControllerScanInterval = TimeSpan.FromSeconds(2);

	private readonly Dictionary<string, ControllerSettings> controllerSettings = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<int, ControllerSettings?> slotProfiles = [];
	private BridgeManager? multi;
	private int selectedSlot = 1;

	// Adds the other controllers; the game mode controller should use the manager as its bridge (see App).
	public RealControllerService UseManager(BridgeManager manager, bool watch = true)
	{
		multi = manager;
		manager.Mode = () => Games.State.Mode;
		for (int slotNumber = 2; slotNumber <= BridgeManager.MaxControllers; slotNumber++)
		{
			int s = slotNumber;
			BridgeHost other = manager.Host(s);
			other.StatusUpdated += _ => Raise();
			other.InputSink = host.InputSink;
			other.PadTypeChanged += type => RaiseNotice(new ServiceNotice(ServiceNoticeKind.PadTypeChanged, type.ToString()));
			other.ControllerOpened = HideOtherAsync;
			other.RawInput += body => OnHostRawInput(s, body);
			other.Shortcuts.Triggered += action => Shortcut?.Invoke(this, action);
			other.MicButtonPressed += OnMicButton;
			other.PoweredOff += (ok, idle) =>
			{
				if (ok && idle)
				{
					RaiseNotice(new ServiceNotice(ServiceNoticeKind.PoweredOffIdle));
				}
			};
		}
		manager.ControllersChanged += () =>
		{
			ApplyOthers();
			Raise();
		};
		ApplyOthers();
		if (watch)
		{
			manager.StartWatching(ControllerScanInterval);
		}
		return this;
	}

	public BridgeManager? Manager => multi;

	// The slot the pages show: the chosen one while its controller is here, else slot 1.
	public int SelectedSlot
	{
		get
		{
			BridgeManager? m = multi;
			int s = selectedSlot;
			return m == null || s == 1 || !m.Controllers.Any(c => c.Slot == s) ? 1 : s;
		}
	}

	internal BridgeHost Selected => multi?.Host(SelectedSlot) ?? host;

	private HidDevice? SelectedDevice
	{
		get
		{
			BridgeManager? m = multi;
			int s = SelectedSlot;
			if (m == null)
			{
				return host.Device ?? present;
			}
			IReadOnlyList<ControllerSlot> list = m.Controllers;
			HidDevice? listed = list.FirstOrDefault(c => c.Slot == s)?.Device;
			return s == 1 ? host.Device ?? listed ?? (list.Count == 0 ? present : null) : m.Host(s).Device ?? listed;
		}
	}

	public IReadOnlyList<ControllerEntry> Controllers => multi?.Controllers.Select(Entry).ToList() ?? [];

	public ControllerEntry? SelectedController
	{
		get
		{
			int s = SelectedSlot;
			return Controllers.FirstOrDefault(c => c.Slot == s);
		}
	}

	public void SelectController(string id)
	{
		if (multi?.SlotOf(id) is int s && s != selectedSlot)
		{
			selectedSlot = s;
			lock (rawInput)
			{
				rawInputAt = 0;
			}
			Raise();
		}
	}

	// Unchanged settings (the same profile pushed again) do nothing: no apply, no StateChanged.
	public void SetControllerProfile(string id, ControllerSettings? value)
	{
		lock (controllerSettings)
		{
			controllerSettings.TryGetValue(id, out ControllerSettings? before);
			if (before == value)
			{
				return;
			}
			if (value == null)
			{
				controllerSettings.Remove(id);
			}
			else
			{
				controllerSettings[id] = value;
			}
		}
		ApplyOthers();
		Raise();
	}

	// The controller's mic button: the settings flip the microphone switch (SettingsState.HandleShortcut).
	private void OnMicButton(bool turnOn) => Shortcut?.Invoke(this, turnOn ? Kopru.Core.Input.ShortcutAction.MicOn : Kopru.Core.Input.ShortcutAction.MicOff);

	// Speaker, mic and light workers of one host (see LatestValueWorker).
	private sealed class HostAppliers(BridgeHost target)
	{
		public LatestValueWorker<SpeakerSettings> Speaker { get; } = new(target.ApplySpeaker);
		public LatestValueWorker<MicSettings> Mic { get; } = new(target.ApplyMic);
		public LatestValueWorker<LightSettings> Light { get; } = new(target.ApplyLight);

		public Task Idle => Task.WhenAll(Speaker.Idle, Mic.Idle, Light.Idle);
	}

	private readonly Dictionary<BridgeHost, HostAppliers> appliers = [];

	private HostAppliers AppliersFor(BridgeHost target)
	{
		lock (appliers)
		{
			if (!appliers.TryGetValue(target, out HostAppliers? a))
			{
				a = new HostAppliers(target);
				appliers[target] = a;
			}
			return a;
		}
	}

	// Completes when every speaker, mic and light change posted so far reached its host (tests).
	public Task SettleAppliesAsync()
	{
		lock (appliers)
		{
			return Task.WhenAll(appliers.Values.Select(a => a.Idle));
		}
	}

	// How many speaker / mic changes reached a host (tests).
	public (int Speaker, int Mic) AudioApplies(BridgeHost target)
	{
		HostAppliers a = AppliersFor(target);
		return (a.Speaker.Applied, a.Mic.Applied);
	}

	// The own profile of the controller bound to a slot, or null (global settings).
	private ControllerSettings? OwnSettings(int slotNumber)
	{
		if (multi?.BoundId(slotNumber) is not { } id)
		{
			return null;
		}
		lock (controllerSettings)
		{
			return controllerSettings.TryGetValue(id, out ControllerSettings? own) ? own : null;
		}
	}

	private ControllerEntry Entry(ControllerSlot c)
	{
		BridgeStatus s = c.Host.Status;
		bool own;
		lock (controllerSettings)
		{
			own = controllerSettings.ContainsKey(c.Id);
		}
		return new ControllerEntry(c.Id, c.Slot, c.Device.IsEdge ? "DualSense Edge" : "DualSense", c.Device.IsEdge, s.Battery != null,
			s.Battery?.Percent ?? 0, s.Battery?.State is ChargeState.Charging, s.State, own);
	}

	private async Task StartOthersAsync()
	{
		if (multi != null)
		{
			await multi.StartAsync().ConfigureAwait(false);
		}
	}

	private async Task StopOthersAsync(string reason)
	{
		if (multi != null)
		{
			await multi.StopAsync(reason).ConfigureAwait(false);
		}
	}

	// Every slot gets its own profile or the global settings; slot 1's global settings were applied by ApplySettings.
	private void ApplyOthers()
	{
		BridgeManager? m = multi;
		if (m == null)
		{
			return;
		}
		for (int s = 1; s <= BridgeManager.MaxControllers; s++)
		{
			ControllerSettings? own = OwnSettings(s);
			slotProfiles.TryGetValue(s, out ControllerSettings? before);
			if (own != before)
			{
				slotProfiles[s] = own;
				m.SetProfileOverride(s, own?.Supported, own?.Unsupported);
			}
			if (s == 1 && own == null)
			{
				if (before != null)
				{
					ApplyTo(host, settings, 1);
				}
				continue;
			}
			ApplyTo(m.Host(s), own ?? settings, s);
		}
	}

	// Speaker, mic and light go through the host's workers, which skip a value equal to the last one posted.
	private void ApplyTo(BridgeHost target, ControllerSettings value, int slotNumber)
	{
		target.IdleMinutes = value.IdlePowerOffMinutes;
		target.PowerOffWhenIdle = value.PowerOffWhenIdle;
		target.Shortcuts.Enabled = value.ShortcutsEnabled;
		target.Desktop.Settings = value.Desktop;
		target.Desktop.Active = value.DesktopModeOn;
		HostAppliers a = AppliersFor(target);
		a.Speaker.Post(value.Speaker);
		a.Mic.Post(value.Mic);
		Kopru.Core.Input.InputTransform t = target.InputTransform;
		if (t.Tuning != value.Tuning || t.RemapSettings != value.Remap || t.GyroSettings != value.Gyro)
		{
			target.InputTransform = new Kopru.Core.Input.InputTransform(value.Tuning, value.Remap, value.Gyro);
		}
		a.Light.Post(BridgeManager.LightForSlot(value.Light, slotNumber));
	}

	private void OnHostRawInput(int slotNumber, ReadOnlySpan<byte> body)
	{
		if (slotNumber == SelectedSlot)
		{
			OnRawInput(body);
		}
	}

	// The other controllers are hidden like the first one (the hiding session takes any number).
	private async Task HideOtherAsync(HidDevice device, CancellationToken cancel)
	{
		if (!hiding.IsInstalled || instanceId(device) is not { } id)
		{
			return;
		}
		try
		{
			await hiding.HideAsync(id).ConfigureAwait(false);
		}
		catch (Exception e) when (e is HidHideException or ArgumentException)
		{
			SetHidingWarning(Text.HidingFailed(e.Message));
		}
	}
}
