// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Virtual;

namespace Kopru.Core.Games;

// The controller type chosen per game (by game key). A game without an entry is on Automatic: it gets the default type.
public sealed class PadTypeMemory
{
	private readonly Dictionary<string, VirtualPadType> types = new(StringComparer.OrdinalIgnoreCase);

	// The game's own choice, or null (Automatic).
	public VirtualPadType? Choice(string? key) => key != null && types.TryGetValue(key, out VirtualPadType t) ? t : null;

	// The game's own choice, DualSense when it has none.
	public VirtualPadType For(string? key) => Choice(key) ?? VirtualPadType.DualSense;

	// The keys with a choice of their own.
	public IEnumerable<KeyValuePair<string, VirtualPadType>> Choices => types;

	// True when the choice changed. Null: back to Automatic.
	public bool Set(string key, VirtualPadType? type)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return false;
		}
		if (type is not { } t || !Enum.IsDefined(t))
		{
			return types.Remove(key);
		}
		if (types.TryGetValue(key, out VirtualPadType old) && old == t)
		{
			return false;
		}
		types[key] = t;
		return true;
	}

	public void Load(IReadOnlyDictionary<string, VirtualPadType>? saved)
	{
		types.Clear();
		foreach ((string key, VirtualPadType type) in saved ?? new Dictionary<string, VirtualPadType>())
		{
			if (Enum.IsDefined(type))
			{
				Set(key, type);
			}
		}
	}

	public IReadOnlyDictionary<string, VirtualPadType> Snapshot() => new Dictionary<string, VirtualPadType>(types, StringComparer.OrdinalIgnoreCase);
}

// Why games get the controller type they get.
public enum PadTypeSource
{
	// Nothing chosen: DualSense.
	Default,

	// The default controller type ("Varsayılan Kumanda Türü") set to a type.
	DefaultSetting,

	// The running game's own choice.
	Game
}

// The controller type for the UI. Current: what the virtual controller is (or becomes at the next connection). Pending: the
// type the running game should get, saved but held back while it runs (a swap could make the game lose the controller);
// null when nothing waits. Source: why the wanted type (Pending, else Current) was chosen; GameKey/GameName: the running game.
public sealed record PadTypeStatus(VirtualPadType Current, VirtualPadType? Pending, PadTypeSource Source, string? GameKey, string? GameName)
{
	public bool HasPending => Pending != null;
}

// The virtual controller type was swapped: From, To and the game running at the time (null: none). Raised after any swap
// handed to the bridge (not on the first one); a running bridge then replaces the pad (BridgeHost.PadTypeChanged), and a game
// that was already running may not see the new one until it restarts.
public sealed record PadSwap(VirtualPadType From, VirtualPadType To, string? GameKey, string? GameName);

// The controller type rules: a game's own choice, else the default type (ForcedPadType; null = Automatic), else DualSense.
// The type follows the running game: it is put in when the game's process appears (the watcher looks for games whose type
// differs from the current one) or when it comes to the front, and the default type comes back when it exits. A change made
// while a game runs (a choice, the default type, or another game taking over) is saved but not applied: it waits as Pending
// until the game exits or ApplyPadTypeNow. With no game running a change swaps at once.
public sealed partial class GameModeController
{
	private readonly PadTypeMemory padTypes = new();
	private VirtualPadType? appliedPad;
	private VirtualPadType? pendingPad;
	private VirtualPadType? forcedPad;

	// The default type for the games without their own choice (null: Automatic, i.e. DualSense).
	public VirtualPadType? ForcedPadType
	{
		get
		{
			lock (gate)
			{
				return forcedPad;
			}
		}
	}

	// The type the virtual controller has (or gets at the next connection) now.
	public VirtualPadType CurrentPadType
	{
		get
		{
			lock (gate)
			{
				return appliedPad ?? ResolvePad();
			}
		}
	}

	// The type held back for the running game (see the class comment), or null.
	public VirtualPadType? PendingPadType
	{
		get
		{
			lock (gate)
			{
				return pendingPad;
			}
		}
	}

	public PadTypeStatus PadStatus
	{
		get
		{
			lock (gate)
			{
				VirtualPadType? choice = active == null ? null : padTypes.Choice(active.Key);
				PadTypeSource source = choice != null ? PadTypeSource.Game : forcedPad != null ? PadTypeSource.DefaultSetting : PadTypeSource.Default;
				return new PadTypeStatus(appliedPad ?? ResolvePad(), pendingPad, source, active?.Key, active?.Name);
			}
		}
	}

	// Current or Pending changed (raised outside the lock, on the thread that made the change: the UI or the game tick).
	public event Action? PadStateChanged;

	// The type handed to the bridge changed (see PadSwap).
	public event Action<PadSwap>? PadSwapped;

	// Null: Automatic. With no game running the virtual controller is swapped at once; with one it waits (Pending).
	public void SetForcedPadType(VirtualPadType? type)
	{
		if (type is { } t && !Enum.IsDefined(t))
		{
			type = null;
		}
		bool changed;
		lock (gate)
		{
			changed = forcedPad != type;
			forcedPad = type;
		}
		if (changed)
		{
			ApplyPad(false);
			MemoryChanged?.Invoke();
		}
	}

	// The type a game gets: its own choice, else the default type, else DualSense.
	public VirtualPadType PadTypeFor(string key)
	{
		lock (gate)
		{
			return padTypes.Choice(key) ?? forcedPad ?? VirtualPadType.DualSense;
		}
	}

	// The game's own choice, or null (Automatic: it gets the default type).
	public VirtualPadType? PadChoiceFor(string key)
	{
		lock (gate)
		{
			return padTypes.Choice(key);
		}
	}

	// Null: back to Automatic. A change for the running game waits (Pending).
	public void SetPadType(string key, VirtualPadType? type)
	{
		bool changed;
		lock (gate)
		{
			changed = padTypes.Set(key, type);
		}
		if (changed)
		{
			MemoryChanged?.Invoke();
			ApplyPad(false);
		}
	}

	// Puts the pending type in now (the user accepts that the running game may lose the controller). False when nothing waits.
	public bool ApplyPadTypeNow()
	{
		lock (gate)
		{
			if (pendingPad == null)
			{
				return false;
			}
		}
		Log("game: controller type applied now while the game runs");
		ApplyPad(true);
		return true;
	}

	// Caller holds the lock.
	private void LoadPadTypes(IReadOnlyDictionary<string, VirtualPadType>? saved, VirtualPadType? forced)
	{
		padTypes.Load(saved);
		forcedPad = forced is { } f && Enum.IsDefined(f) ? f : null;
	}

	// The one place the wanted type is decided. Caller holds the lock.
	private VirtualPadType ResolvePad() =>
		(active == null ? null : padTypes.Choice(active.Key)) ?? forcedPad ?? VirtualPadType.DualSense;

	// Caller holds the lock.
	private IReadOnlyDictionary<string, VirtualPadType> SnapshotPadTypes() => padTypes.Snapshot();

	// Caller holds the lock. The games whose own type differs from the current one (looked for while no game runs).
	private IReadOnlySet<string> GamesWithOtherPad()
	{
		VirtualPadType current = appliedPad ?? ResolvePad();
		var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach ((string key, VirtualPadType type) in padTypes.Choices)
		{
			if (type != current)
			{
				keys.Add(key);
			}
		}
		return keys;
	}

	// Tells the bridge the type to use when it changes. mayInterrupt: a game that runs may get it now (it just started, or
	// the user asked for it); otherwise a change while a game runs waits as Pending.
	private void ApplyPad(bool mayInterrupt)
	{
		VirtualPadType wanted;
		VirtualPadType? from = null;
		bool swap = false, stateChanged = false;
		string? gameKey, gameName;
		lock (gate)
		{
			wanted = ResolvePad();
			gameKey = active?.Key;
			gameName = active?.Name;
			if (appliedPad == wanted)
			{
				stateChanged = pendingPad != null;
				pendingPad = null;
			}
			else if (appliedPad != null && active != null && !mayInterrupt)
			{
				stateChanged = pendingPad != wanted;
				pendingPad = wanted;
			}
			else
			{
				from = appliedPad;
				appliedPad = wanted;
				pendingPad = null;
				swap = stateChanged = true;
			}
		}
		if (swap)
		{
			bridge?.UsePad(wanted);
			if (from is { } before)
			{
				Log($"game: controller type {before} -> {wanted}{(gameName != null ? $" ({gameName} running)" : string.Empty)}");
				PadSwapped?.Invoke(new PadSwap(before, wanted, gameKey, gameName));
			}
		}
		if (stateChanged)
		{
			PadStateChanged?.Invoke();
		}
	}
}
