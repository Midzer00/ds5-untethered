// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Virtual;

namespace Kopru.Core.Games;

// The controller type chosen per game (by game key). Only non-default choices are kept: DualSense is never stored.
public sealed class PadTypeMemory
{
	private readonly Dictionary<string, VirtualPadType> types = new(StringComparer.OrdinalIgnoreCase);

	public VirtualPadType For(string? key) => key != null && types.TryGetValue(key, out VirtualPadType t) ? t : VirtualPadType.DualSense;

	// True when the choice changed.
	public bool Set(string key, VirtualPadType type)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			return false;
		}
		if (type == VirtualPadType.DualSense)
		{
			return types.Remove(key);
		}
		if (types.TryGetValue(key, out VirtualPadType old) && old == type)
		{
			return false;
		}
		types[key] = type;
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

// The per-game controller type: the game in front picks the virtual pad (DualSense unless the user chose otherwise). A
// controller type forced by the user ("Kumanda Türü" other than Automatic) wins over every per-game choice; the per-game
// choices are kept and apply again once it is back on Automatic.
public sealed partial class GameModeController
{
	private readonly PadTypeMemory padTypes = new();
	private VirtualPadType? appliedPad;
	private VirtualPadType? forcedPad;

	// The type forced for every game and every controller (null: Automatic).
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
				return ResolvePad();
			}
		}
	}

	// Null: Automatic. A change while the virtual controller runs swaps the pad the same way a game switch does.
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
			ApplyPad();
			MemoryChanged?.Invoke();
		}
	}

	public VirtualPadType PadTypeFor(string key)
	{
		lock (gate)
		{
			return padTypes.For(key);
		}
	}

	public void SetPadType(string key, VirtualPadType type)
	{
		bool changed;
		lock (gate)
		{
			changed = padTypes.Set(key, type);
		}
		if (changed)
		{
			MemoryChanged?.Invoke();
			ApplyPad();
		}
	}

	// Caller holds the lock.
	private void LoadPadTypes(IReadOnlyDictionary<string, VirtualPadType>? saved, VirtualPadType? forced)
	{
		padTypes.Load(saved);
		forcedPad = forced is { } f && Enum.IsDefined(f) ? f : null;
	}

	// The one place the pad type is decided: the forced type, else the game in front's choice (DualSense with no game).
	// Caller holds the lock.
	private VirtualPadType ResolvePad() => forcedPad ?? padTypes.For(active?.Key);

	// Caller holds the lock.
	private IReadOnlyDictionary<string, VirtualPadType> SnapshotPadTypes() => padTypes.Snapshot();

	// Tells the bridge the pad to use (only when it changes; see ResolvePad).
	private void ApplyPad()
	{
		VirtualPadType wanted;
		lock (gate)
		{
			wanted = ResolvePad();
			if (appliedPad == wanted)
			{
				return;
			}
			appliedPad = wanted;
		}
		bridge?.UsePad(wanted);
	}
}
