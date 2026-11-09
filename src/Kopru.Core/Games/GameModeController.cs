// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;

namespace Kopru.Core.Games;

// The game in the foreground and the mode the bridge runs in for it. Exe and Name are null before any game was seen.
public sealed record GameModeState(string? Exe, string? Name, GameMode Mode, ModeReason Reason, CatalogGame? Match = null)
{
	public static GameModeState Initial { get; } = new(null, null, GameMode.Unsupported, ModeReason.Default);
}

// One row of the supported games list: a catalog game, or one detected as native on this PC.
public sealed record KnownGame(string Name, string Exe, bool Haptics, bool AdaptiveTriggers, bool IsLearned, CatalogGame? Catalog);

// Watches the foreground game, learns native support from the bridge's signals, resolves the mode and applies the
// matching profile to the bridge whenever the mode or a profile changes. Tick is called about once a second (tests call
// it directly). Thread-safe; events are raised on the calling thread, outside the lock.
public sealed class GameModeController
{
	private readonly object gate = new();
	private readonly IGameWatcher watcher;
	private readonly IGameBridge? bridge;
	private readonly NativeSupportDetector detector = new();
	private readonly Dictionary<string, GameOverride> overrides = new(StringComparer.OrdinalIgnoreCase);

	private SupportedProfile supported = new();
	private UnsupportedProfile unsupported = new();
	private ForegroundGame? current;
	private GameModeState state = GameModeState.Initial;
	private BridgeProfile? applied;

	public GameModeController(GameCatalog catalog, IGameWatcher watcher, IGameBridge? bridge)
	{
		Catalog = catalog;
		this.watcher = watcher;
		this.bridge = bridge;
		Apply();
	}

	public GameCatalog Catalog { get; }

	public GameModeState State
	{
		get
		{
			lock (gate)
			{
				return state;
			}
		}
	}

	public SupportedProfile Supported
	{
		get
		{
			lock (gate)
			{
				return supported;
			}
		}
	}

	public UnsupportedProfile Unsupported
	{
		get
		{
			lock (gate)
			{
				return unsupported;
			}
		}
	}

	// The current game or mode changed.
	public event Action<GameModeState>? StateChanged;

	// Overrides or learned results changed (to be saved).
	public event Action? MemoryChanged;

	// Mode resolution: the user's override, then the catalog, then a learned native result, else unsupported.
	public static (GameMode Mode, ModeReason Reason) Resolve(GameOverride userChoice, CatalogGame? match, bool? learnedNative) => userChoice switch
	{
		GameOverride.Supported => (GameMode.Supported, ModeReason.User),
		GameOverride.Unsupported => (GameMode.Unsupported, ModeReason.User),
		_ when match != null => (GameMode.Supported, ModeReason.Catalog),
		_ when learnedNative == true => (GameMode.Supported, ModeReason.Detected),
		_ when learnedNative == false => (GameMode.Unsupported, ModeReason.Detected),
		_ => (GameMode.Unsupported, ModeReason.Default)
	};

	public void SetProfiles(SupportedProfile supportedProfile, UnsupportedProfile unsupportedProfile)
	{
		lock (gate)
		{
			supported = supportedProfile;
			unsupported = unsupportedProfile;
		}
		Apply();
	}

	public void Tick()
	{
		ForegroundGame? seen = watcher.Poll();
		GameSignals signals = bridge?.TakeGameSignals() ?? default;
		bool learnedChanged = false;
		lock (gate)
		{
			if (seen != null)
			{
				current = seen;
			}
			if (current != null && seen != null)
			{
				learnedChanged = detector.Observe(current.Exe, signals);
			}
		}
		if (learnedChanged)
		{
			MemoryChanged?.Invoke();
		}
		Apply();
	}

	public GameOverride GetOverride(string exe)
	{
		lock (gate)
		{
			return overrides.TryGetValue(GameCatalog.Normalize(exe), out GameOverride o) ? o : GameOverride.Auto;
		}
	}

	public void SetOverride(string exe, GameOverride choice)
	{
		string key = GameCatalog.Normalize(exe);
		lock (gate)
		{
			if (GetOverride(key) == choice)
			{
				return;
			}
			if (choice == GameOverride.Auto)
			{
				overrides.Remove(key);
			}
			else
			{
				overrides[key] = choice;
			}
		}
		MemoryChanged?.Invoke();
		Apply();
	}

	// Saved overrides and learned results (settings load).
	public void LoadMemory(IEnumerable<KeyValuePair<string, GameOverride>>? savedOverrides, IEnumerable<KeyValuePair<string, bool>>? learned)
	{
		lock (gate)
		{
			overrides.Clear();
			foreach ((string exe, GameOverride choice) in savedOverrides ?? [])
			{
				string key = GameCatalog.Normalize(exe);
				if (key.Length > 0 && choice != GameOverride.Auto)
				{
					overrides[key] = choice;
				}
			}
			detector.Load(learned ?? []);
		}
		Apply();
	}

	public Dictionary<string, GameOverride> SnapshotOverrides()
	{
		lock (gate)
		{
			return new(overrides, StringComparer.OrdinalIgnoreCase);
		}
	}

	public Dictionary<string, bool> SnapshotLearned()
	{
		lock (gate)
		{
			return new(detector.Learned, StringComparer.OrdinalIgnoreCase);
		}
	}

	// Catalog games plus games detected as native here that are not in the catalog, by name.
	public IReadOnlyList<KnownGame> KnownGames()
	{
		lock (gate)
		{
			var list = Catalog.Games
				.Select(g => new KnownGame(g.Name, g.Exe.FirstOrDefault() ?? string.Empty, g.Haptics, g.AdaptiveTriggers, false, g))
				.ToList();
			foreach ((string exe, bool native) in detector.Learned)
			{
				if (native && Catalog.Find(exe) == null)
				{
					list.Add(new KnownGame(ForegroundGame.DisplayName(exe), exe, false, false, true, null));
				}
			}
			return list.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
		}
	}

	private void Apply()
	{
		GameModeState? changed = null;
		BridgeProfile? toApply = null;
		lock (gate)
		{
			GameModeState next = state;
			if (current != null)
			{
				(GameMode mode, ModeReason reason) = Resolve(
					overrides.TryGetValue(current.Exe, out GameOverride o) ? o : GameOverride.Auto, current.Match, detector.Get(current.Exe));
				next = new GameModeState(current.Exe, current.Name, mode, reason, current.Match);
			}
			if (next != state)
			{
				state = next;
				changed = next;
			}
			BridgeProfile profile = GameProfiles.For(state.Mode, supported, unsupported);
			if (applied == null || !applied.SameAs(profile))
			{
				applied = toApply = profile;
			}
		}
		if (toApply != null)
		{
			bridge?.ApplyProfile(toApply);
		}
		if (changed != null)
		{
			StateChanged?.Invoke(changed);
		}
	}
}
