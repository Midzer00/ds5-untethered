// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Virtual;

namespace Kopru.Core.Games;

// The controller type chosen per game (by game key). A game without an entry is on Automatic (GameModePadTypes).
public sealed class PadTypeMemory
{
	private readonly Dictionary<string, VirtualPadType> types = new(StringComparer.OrdinalIgnoreCase);

	// The game's own choice, or null (Automatic).
	public VirtualPadType? Choice(string? key) => key != null && types.TryGetValue(key, out VirtualPadType t) ? t : null;

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
	// No game runs and no default type is set: DualSense.
	Default,

	// The default controller type ("Varsayılan Kumanda Türü") set to a type: no game runs, or the running game is on
	// Automatic and nothing is known about it.
	DefaultSetting,

	// The running game's own choice.
	Game,

	// The running game is on Automatic: what is known about it picked the type (or Xbox 360 with no default type).
	Automatic
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

// The controller type rules, for the running game: (1) its own choice, else Automatic: (2) what we know about the game:
// DualSense when it is in the supported list or the bridge learned that it uses the DualSense natively
// (NativeSupportDetector: haptics or trigger effects seen while it was in front); for a Steam game what its store page
// lists (PlayStationSupport, shipped with the app, then what Steam's own cache on this PC says, FoundGame.PlayStation):
// "DualSense Controller Support" gives DualSense, "DualShock Controller Support" alone DualShock 4. A game added by hand
// whose exe lies in a Steam library's install folder counts as that Steam app. (3) Nothing known: the default type
// (ForcedPadType, "used for games we know nothing about") when one is set, (4) else Xbox 360. With no game running: the
// default type, else DualSense. The type follows the running game: it is put in when
// the game's process appears (the watcher looks for known games whose type, own or Automatic, differs from the current
// one) or when it comes to the front, and the default type comes back when it exits. A change made
// while a game runs (a choice, the default type, or another game taking over) is saved but not applied: it waits as Pending
// until the game exits or ApplyPadTypeNow. With no game running a change swaps at once.
public sealed partial class GameModeController
{
	private readonly PadTypeMemory padTypes = new();
	private VirtualPadType? appliedPad;
	private VirtualPadType? pendingPad;
	private VirtualPadType? forcedPad;
	private readonly NativeSupportDetector native = new();
	private PlayStationSupport playStation = PlayStationSupport.LoadEmbedded();

	// The default type for the Automatic games we know nothing about, and with no game running (null: none).
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
				PadTypeSource source = choice != null ? PadTypeSource.Game
					: active != null ? (forcedPad != null && KnownPad(active.Key, active) == null ? PadTypeSource.DefaultSetting : PadTypeSource.Automatic)
					: forcedPad != null ? PadTypeSource.DefaultSetting : PadTypeSource.Default;
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

	// The type a game gets: its own choice, else the Automatic rules.
	public VirtualPadType PadTypeFor(string key)
	{
		lock (gate)
		{
			return WantedFor(key, RunningAs(key));
		}
	}

	// The type the game gets on Automatic (what "Otomatik (...)" shows): the Automatic rules 2-4.
	public VirtualPadType AutomaticPadTypeFor(string key)
	{
		lock (gate)
		{
			return AutoPad(key, RunningAs(key));
		}
	}

	// The game keys learned to use the DualSense natively.
	public IReadOnlyCollection<string> NativeGames
	{
		get
		{
			lock (gate)
			{
				return NativeKeys();
			}
		}
	}

	// The PlayStation controller support of Steam games (the shipped list by default; tests set their own).
	public PlayStationSupport PlayStation
	{
		get
		{
			lock (gate)
			{
				return playStation;
			}
		}
		set
		{
			lock (gate)
			{
				playStation = value;
			}
			ApplyPad(false);
			MemoryChanged?.Invoke();
		}
	}

	// The game's own choice, or null (Automatic).
	public VirtualPadType? PadChoiceFor(string key)
	{
		lock (gate)
		{
			return padTypes.Choice(key);
		}
	}

	// Null: back to Automatic. A change for the running game waits (Pending). Choosing Xbox 360 or DualShock 4 forgets that
	// the game was learned to use the DualSense natively (the user says it does not), so Automatic no longer gives it one.
	public void SetPadType(string key, VirtualPadType? type)
	{
		bool changed;
		lock (gate)
		{
			changed = padTypes.Set(key, type);
			if (type is VirtualPadType.Xbox360 or VirtualPadType.DualShock4 && native.Forget(key))
			{
				changed = true;
			}
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
	private void LoadPadTypes(IReadOnlyDictionary<string, VirtualPadType>? saved, VirtualPadType? forced, IReadOnlyCollection<string>? nativeGames)
	{
		padTypes.Load(saved);
		forcedPad = forced is { } f && Enum.IsDefined(f) ? f : null;
		native.Load((nativeGames ?? []).Select(k => new KeyValuePair<string, bool>(k, true)));
	}

	// Caller holds the lock.
	private List<string> NativeKeys() =>
		native.Learned.Where(p => p.Value).Select(p => p.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

	// The one place the wanted type is decided. Caller holds the lock.
	private VirtualPadType ResolvePad() => active == null ? forcedPad ?? VirtualPadType.DualSense : WantedFor(active.Key, active);

	// Caller holds the lock. running: the game's running instance when it is the active game (its catalog match and found
	// entry), else null.
	private VirtualPadType WantedFor(string key, ForegroundGame? running) => padTypes.Choice(key) ?? AutoPad(key, running);

	// Caller holds the lock.
	private ForegroundGame? RunningAs(string key) =>
		active != null && string.Equals(active.Key, key, StringComparison.OrdinalIgnoreCase) ? active : null;

	// The Automatic rules 2-4 (see the class comment) for a known game. Caller holds the lock.
	private VirtualPadType AutoPad(string key, ForegroundGame? running, FoundGame? found = null) =>
		KnownPad(key, running, found) ?? forcedPad ?? VirtualPadType.Xbox360;

	// Rule 2: the type what we know about the game gives, or null when we know nothing. Caller holds the lock.
	private VirtualPadType? KnownPad(string key, ForegroundGame? running, FoundGame? found = null)
	{
		if (native.Get(key) == true || running?.Match != null || catalog.FindByKey(key) != null)
		{
			return VirtualPadType.DualSense;
		}
		found ??= running?.Found ?? FoundFor(key);
		long? appId = SteamAppIdOf(key) ?? found?.SteamAppId;
		if (appId == null && found == null && SteamAppIdByPath(key, running) is long byPath)
		{
			// A game added by hand from a Steam library: that app's store page, and Steam's cache when the scan found it.
			appId = byPath;
			found = FoundFor(FoundGame.KeyFor(InstalledSource.Steam, byPath.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}
		PlayStationPad? pad = (appId is long id ? playStation.For(id) : null) ?? found?.PlayStation;
		return pad switch
		{
			PlayStationPad.DualSense => VirtualPadType.DualSense,
			PlayStationPad.DualShock4 => VirtualPadType.DualShock4,
			_ => null
		};
	}

	// The Steam app id of the install folder the game's exe lies in (the running process, else the game added by hand with
	// this key), or null. Caller holds the lock.
	private long? SteamAppIdByPath(string key, ForegroundGame? running)
	{
		string? path = running?.Path
			?? userGames.FirstOrDefault(g => string.Equals(ForegroundGame.KeyOf(catalog.Find(g.Exe), g.Exe), key, StringComparison.OrdinalIgnoreCase))?.Path;
		return string.IsNullOrWhiteSpace(path) ? null : watcher.SteamAppIdFor(path);
	}

	// Caller holds the lock (the watcher has its own).
	private FoundGame? FoundFor(string key) =>
		key.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) || key.StartsWith("epic:", StringComparison.OrdinalIgnoreCase)
			? watcher.FoundGames.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase))
			: null;

	// "steam:<appid>" -> the app id.
	private static long? SteamAppIdOf(string key) =>
		key.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)
		&& long.TryParse(key.AsSpan(6), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long id)
			? id
			: null;

	// Caller holds the lock.
	private IReadOnlyDictionary<string, VirtualPadType> SnapshotPadTypes() => padTypes.Snapshot();

	// Caller holds the lock. The games whose type differs from the current one (looked for while no game runs, so the type
	// is put in when the process starts): their own choice, and on Automatic the type the rules give the games added by
	// hand, found installed or listed.
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
		void Check(string key, FoundGame? found = null)
		{
			if (key.Length > 0 && padTypes.Choice(key) == null && !keys.Contains(key) && AutoPad(key, null, found) != current)
			{
				keys.Add(key);
			}
		}
		foreach (FoundGame found in watcher.FoundGames)
		{
			// On Automatic only what Steam calls a game: software that runs in the background (Wallpaper Engine, Lossless
			// Scaling) must not swap the controller from the desktop. A game Steam's cache was not read for waits until it is
			// in front.
			if (found.IsKnownGame)
			{
				Check(found.Key, found);
			}
		}
		foreach (UserGame game in userGames)
		{
			Check(ForegroundGame.KeyOf(catalog.Find(game.Exe), game.Exe));
		}
		if (current != VirtualPadType.DualSense)
		{
			// Listed games get DualSense on Automatic.
			foreach (CatalogGame game in catalog.Games)
			{
				Check(game.Name);
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
