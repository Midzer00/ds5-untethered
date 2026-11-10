// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Games;
using Kopru.Core.Hid;
using Kopru.Core.Lighting;
using Kopru.Core.Virtual;

namespace Kopru.Core.Bridge;

// One Bluetooth DualSense seen by IControllerSource.Enumerate: a stable id (device instance id) and the HID device.
public sealed record ControllerPresence(string Id, HidDevice Device);

// A controller the manager has put in a player slot (1-4), with the host that bridges it.
public sealed record ControllerSlot(int Slot, string Id, HidDevice Device, BridgeHost Host);

// Player slot of a host: picks the virtual pad's USB/IP bus id and its virtual Bluetooth address (feature 0x09).
public sealed partial class BridgeHost
{
	private int slot = 1;

	// 1-4; set before starting. Slot 1 is the pad a single controller always had.
	public int Slot
	{
		get => slot;
		set => slot = Math.Clamp(value, 1, BridgeManager.MaxControllers);
	}

	public string BusId => VirtualDualSense.BusIdFor(slot);

	// Replaces where the controller comes from (BridgeManager binds each host to one controller); set before starting.
	public void UseControllerSource(IControllerSource source) => controllers = source;
}

// Opens only the controller bound to one slot (nothing while none is bound).
public sealed class BoundControllerSource(IControllerSource inner, Func<string?> boundId) : IControllerSource
{
	public IHidLink? TryOpen() => boundId() is { } id ? inner.TryOpen(id) : null;

	public IReadOnlyList<ControllerPresence> Enumerate() => inner.Enumerate();

	public IHidLink? TryOpen(string id) => inner.TryOpen(id);
}

// Up to four controllers at once: one BridgeHost per player slot, each bound to one Bluetooth DualSense (by device
// instance id) with its own USB/IP server, bus id and virtual address. Slot 1 is the app's existing host. A controller
// that leaves keeps its slot and gets it back when it returns, unless a new controller needed it meanwhile.
// Game mode profiles go to every host (the manager is the GameModeController's IGameBridge); a slot can have its own
// profile instead. The app starts and stops the manager with the bridge; RefreshAsync follows controllers coming and going.
public sealed class BridgeManager : IGameBridge, IAsyncDisposable
{
	public const int MaxControllers = 4;

	private readonly IControllerSource source;
	private readonly BridgeHost[] hosts = new BridgeHost[MaxControllers];
	private readonly string?[] bound = new string?[MaxControllers];
	private readonly ControllerPresence?[] present = new ControllerPresence?[MaxControllers];
	private readonly (SupportedProfile Supported, UnsupportedProfile Unsupported)?[] overrides = new (SupportedProfile, UnsupportedProfile)?[MaxControllers];
	private readonly Dictionary<string, int> remembered = new(StringComparer.OrdinalIgnoreCase);
	private readonly SemaphoreSlim refreshGate = new(1, 1);
	private readonly object sync = new();
	private BridgeProfile global = BridgeProfile.Default;
	private bool active;
	private Timer? timer;

	// createHost builds the host of slot 2-4 (the manager sets its slot and source).
	public BridgeManager(IControllerSource source, BridgeHost primary, Func<int, BridgeHost> createHost)
	{
		this.source = source;
		for (int i = 0; i < MaxControllers; i++)
		{
			int slotNumber = i + 1;
			BridgeHost host = i == 0 ? primary : createHost(slotNumber);
			host.Slot = slotNumber;
			host.UseControllerSource(new BoundControllerSource(source, () => BoundId(slotNumber)));
			hosts[i] = host;
		}
	}

	public static BridgeManager CreateDefault(BridgeHost primary)
	{
		WindowsControllerSource source = new();
		return new BridgeManager(source, primary, slot => new BridgeHost(source, IdentitySources.Default(), new UsbIpExeAttacher())
		{
			StateFile = new BridgeStateFile(Path.Combine(AppPaths.DataDirectory, $"bridge-state-{slot}.json"))
		});
	}

	public BridgeHost Primary => hosts[0];

	// Slot 1-4 to host.
	public BridgeHost Host(int slotNumber) => hosts[Math.Clamp(slotNumber, 1, MaxControllers) - 1];

	public IReadOnlyList<BridgeHost> Hosts => hosts;

	// The game mode's current mode, for the slots with their own profile (null: other games).
	public Func<GameMode>? Mode { get; set; }

	// True between StartAsync and StopAsync: present controllers are bridged.
	public bool IsActive
	{
		get
		{
			lock (sync)
			{
				return active;
			}
		}
	}

	// The controllers present now, by slot.
	public IReadOnlyList<ControllerSlot> Controllers
	{
		get
		{
			lock (sync)
			{
				List<ControllerSlot> list = [];
				for (int i = 0; i < MaxControllers; i++)
				{
					if (present[i] is { } p)
					{
						list.Add(new ControllerSlot(i + 1, p.Id, p.Device, hosts[i]));
					}
				}
				return list;
			}
		}
	}

	// Raised (on the refreshing thread) when a controller joined, left or changed slot.
	public event Action? ControllersChanged;

	public string? BoundId(int slotNumber)
	{
		lock (sync)
		{
			return bound[slotNumber - 1];
		}
	}

	// The slot a controller is (or was last) in, or null.
	public int? SlotOf(string id)
	{
		lock (sync)
		{
			return remembered.TryGetValue(id, out int s) ? s : null;
		}
	}

	// Player 1 keeps the user's pattern; the others show their slot while the user has the default player 1 pattern.
	public static LightSettings LightForSlot(LightSettings light, int slotNumber)
	{
		if (slotNumber <= 1 || !light.PlayerLeds || light.Options.PlayerPattern != PlayerLedPattern.One)
		{
			return light;
		}
		PlayerLedPattern pattern = slotNumber switch { 2 => PlayerLedPattern.Two, 3 => PlayerLedPattern.Three, _ => PlayerLedPattern.Four };
		return light with { Options = light.Options with { PlayerPattern = pattern } };
	}

	// Looks at the controllers present, puts new ones in slots and starts or stops the hosts of slots 2-4.
	public async Task RefreshAsync()
	{
		await refreshGate.WaitAsync().ConfigureAwait(false);
		try
		{
			IReadOnlyList<ControllerPresence> now;
			try
			{
				now = source.Enumerate();
			}
			catch (Exception)
			{
				now = [];
			}
			bool changed = Assign(now);
			bool run = IsActive;
			for (int i = 1; i < MaxControllers; i++)
			{
				bool here;
				lock (sync)
				{
					here = present[i] != null;
				}
				if (run && here)
				{
					await hosts[i].StartAsync("controller joined").ConfigureAwait(false);
				}
				else if (hosts[i].State != BridgeState.Stopped)
				{
					await hosts[i].StopAsync("controller left").ConfigureAwait(false);
				}
			}
			if (changed)
			{
				ControllersChanged?.Invoke();
			}
		}
		finally
		{
			refreshGate.Release();
		}
	}

	private bool Assign(IReadOnlyList<ControllerPresence> now)
	{
		lock (sync)
		{
			bool changed = false;
			Dictionary<string, ControllerPresence> byId = new(StringComparer.OrdinalIgnoreCase);
			foreach (ControllerPresence p in now)
			{
				byId.TryAdd(p.Id, p);
			}
			for (int i = 0; i < MaxControllers; i++)
			{
				ControllerPresence? next = bound[i] is { } id && byId.TryGetValue(id, out ControllerPresence? p) ? p : null;
				if (next?.Id != present[i]?.Id)
				{
					changed = true;
				}
				present[i] = next;
			}
			foreach (ControllerPresence p in byId.Values)
			{
				if (Array.Exists(bound, b => string.Equals(b, p.Id, StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}
				int free = remembered.TryGetValue(p.Id, out int last) && present[last - 1] == null ? last - 1 : FreeSlot();
				if (free < 0)
				{
					continue;
				}
				bound[free] = p.Id;
				present[free] = p;
				remembered[p.Id] = free + 1;
				changed = true;
			}
			return changed;
		}
	}

	// Caller holds the lock: a never used slot first, then one whose controller is away.
	private int FreeSlot()
	{
		int empty = Array.FindIndex(bound, b => b == null);
		return empty >= 0 ? empty : Array.FindIndex(present, p => p == null);
	}

	// Bridges every present controller (slot 1 waits for one as a single controller always did).
	public async Task StartAsync()
	{
		lock (sync)
		{
			active = true;
		}
		await RefreshAsync().ConfigureAwait(false);
		await hosts[0].StartAsync("virtual controller start").ConfigureAwait(false);
	}

	// Stops every host (virtual pads unplugged).
	public Task StopAsync() => StopAsync("unspecified");

	// reason: who asked (each host logs it).
	public async Task StopAsync(string reason)
	{
		lock (sync)
		{
			active = false;
		}
		await refreshGate.WaitAsync().ConfigureAwait(false);
		try
		{
			await Task.WhenAll(hosts.Select(h => h.StopAsync(reason))).ConfigureAwait(false);
		}
		finally
		{
			refreshGate.Release();
		}
	}

	// Refreshes every interval until disposed.
	public void StartWatching(TimeSpan interval)
	{
		timer?.Dispose();
		timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, interval);
	}

	// Detaches the virtual pads an earlier run left behind, for every slot.
	public async Task CleanupStaleAsync(CancellationToken cancel = default)
	{
		foreach (BridgeHost host in hosts)
		{
			await host.CleanupStaleAsync(cancel).ConfigureAwait(false);
		}
	}

	// The game mode's profile: every slot without its own profile runs it.
	public void ApplyProfile(BridgeProfile profile)
	{
		lock (sync)
		{
			global = profile;
		}
		ApplyProfiles();
	}

	// A slot's own profile (null: the global one again).
	// Per-game virtual pad type: every controller gets the same type.
	public void UsePad(VirtualPadType type)
	{
		foreach (BridgeHost host in hosts)
		{
			host.UsePad(type);
		}
	}

	public void SetProfileOverride(int slotNumber, SupportedProfile? supported, UnsupportedProfile? unsupported)
	{
		lock (sync)
		{
			overrides[slotNumber - 1] = supported != null && unsupported != null ? (supported, unsupported) : null;
		}
		ApplyProfiles();
	}

	public BridgeProfile ProfileFor(int slotNumber)
	{
		lock (sync)
		{
			return overrides[slotNumber - 1] is { } o ? GameProfiles.For(Mode?.Invoke() ?? GameMode.Unsupported, o.Supported, o.Unsupported) : global;
		}
	}

	private void ApplyProfiles()
	{
		for (int i = 0; i < MaxControllers; i++)
		{
			hosts[i].ApplyProfile(ProfileFor(i + 1));
		}
	}

	// What the games sent to any of the pads.
	public GameSignals TakeGameSignals()
	{
		GameSignals all = default;
		foreach (BridgeHost host in hosts)
		{
			GameSignals s = host.TakeGameSignals();
			all = new GameSignals(all.Haptics || s.Haptics, all.Triggers || s.Triggers, all.Rumble || s.Rumble);
		}
		return all;
	}

	public async ValueTask DisposeAsync()
	{
		timer?.Dispose();
		timer = null;
		await StopAsync().ConfigureAwait(false);
	}
}
