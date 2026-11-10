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

// The per-game controller type: the game in front picks the virtual pad (DualSense unless the user chose otherwise).
public sealed partial class GameModeController
{
	private readonly PadTypeMemory padTypes = new();
	private VirtualPadType? appliedPad;

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
	private void LoadPadTypes(IReadOnlyDictionary<string, VirtualPadType>? saved) => padTypes.Load(saved);

	// Caller holds the lock.
	private IReadOnlyDictionary<string, VirtualPadType> SnapshotPadTypes() => padTypes.Snapshot();

	// Tells the bridge the pad for the game in front (only when it changes; DualSense with no game).
	private void ApplyPad()
	{
		VirtualPadType wanted;
		lock (gate)
		{
			wanted = padTypes.For(active?.Key);
			if (appliedPad == wanted)
			{
				return;
			}
			appliedPad = wanted;
		}
		bridge?.UsePad(wanted);
	}
}
