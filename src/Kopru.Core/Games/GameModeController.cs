// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;

namespace Kopru.Core.Games;

// The known game running in front (null when none) and the mode the bridge runs in. Mode follows the supported mode switch.
public sealed record GameModeState(string? Exe, string? Name, GameMode Mode, ModeReason Reason, CatalogGame? Match = null)
{
	public static GameModeState Initial { get; } = new(null, null, GameMode.Unsupported, ModeReason.Default);
}

// One row of the supported games list: a catalog game or a game the user added. Key matches ForegroundGame.Key.
public sealed record KnownGame(string Name, string Exe, bool Haptics, bool AdaptiveTriggers, bool IsUserAdded, CatalogGame? Catalog, string Key,
	string? Path = null);

// What is saved: the switch when the user turned it on without a game, the question setting, the "always" and "never
// ask" games (by key), the games the user added, the controller type per game and the controller type forced for every
// game (null: Automatic, the per-game choice).
public sealed record GameMemory(bool SupportedModeOn, bool AskOnKnownGame, IReadOnlyList<string> Always, IReadOnlyList<string> NeverAsk,
	IReadOnlyList<UserGame> UserGames, IReadOnlyDictionary<string, Kopru.Core.Virtual.VirtualPadType>? PadTypes = null,
	Kopru.Core.Virtual.VirtualPadType? ForcedPad = null)
{
	public static GameMemory Default { get; } = new(false, true, [], [], []);
}

// The supported mode switch and the known game in front. Off (default): the other games profile. On: the supported
// games profile. Only known games are looked at (GameWatcher). When one starts with the switch off, AskRequested is raised
// once per launch (unless it is on the "always" list: then the switch turns on by itself, or "never ask"). A switch turned
// on for a game turns off again when that game exits or another known game takes over; one the user turned on without a
// game stays on. The user's own flip always wins until the game exits. Tick is called about once a second (tests call it
// directly). Thread-safe; events are raised on the calling thread, outside the lock.
public sealed partial class GameModeController
{
	private const int LaunchMemoryLimit = 512;

	private readonly object gate = new();
	private readonly IGameWatcher watcher;
	private readonly IGameBridge? bridge;
	private readonly HashSet<string> always = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> neverAsk = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> launches = new(StringComparer.OrdinalIgnoreCase);
	private List<UserGame> userGames = [];

	private SupportedProfile supported = new();
	private UnsupportedProfile unsupported = new();
	private GameCatalog catalog;
	private ForegroundGame? active;
	private bool on;
	private ModeReason reason = ModeReason.Default;
	// The key of the game the switch was turned on for (it turns off when that game ends); null: on without a game.
	private string? onFor;
	private bool askOnKnownGame = true;
	private volatile bool gameInFront;
	private GameModeState state = GameModeState.Initial;
	private BridgeProfile? applied;

	public GameModeController(GameCatalog catalog, IGameWatcher watcher, IGameBridge? bridge)
	{
		this.catalog = catalog;
		this.watcher = watcher;
		this.bridge = bridge;
		Apply();
	}

	public GameCatalog Catalog
	{
		get
		{
			lock (gate)
			{
				return catalog;
			}
		}
	}

	// Swaps in an updated supported games list: the watcher matches with it from now on. Raises CatalogChanged.
	public void SetCatalog(GameCatalog updated)
	{
		lock (gate)
		{
			catalog = updated;
			if (active != null)
			{
				active = active with { Match = updated.Find(active.Exe) ?? active.Match };
			}
		}
		if (watcher is GameWatcher gameWatcher)
		{
			gameWatcher.Catalog = updated;
		}
		CatalogChanged?.Invoke();
		Apply();
	}

	// The supported games list was replaced (SetCatalog).
	public event Action? CatalogChanged;

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

	// The known game in front right now (its process still runs), or null.
	public ForegroundGame? ActiveGame
	{
		get
		{
			lock (gate)
			{
				return active;
			}
		}
	}

	// Whether the foreground window was a known game at the last Tick (about once a second). Unlike ActiveGame, false
	// while the game still runs behind another window. Lock-free (read on the input thread).
	public bool IsGameInFront => gameInFront;

	public bool IsSupportedModeOn
	{
		get
		{
			lock (gate)
			{
				return on;
			}
		}
	}

	// Ask when a known game starts with the switch off (a setting; on by default).
	public bool AskOnKnownGame
	{
		get
		{
			lock (gate)
			{
				return askOnKnownGame;
			}
		}
		set
		{
			lock (gate)
			{
				askOnKnownGame = value;
			}
		}
	}

	// The current game or mode changed.
	public event Action<GameModeState>? StateChanged;

	// The "always" / "never ask" lists, the user's games or the saved switch changed (to be saved).
	public event Action? MemoryChanged;

	// A known game started with the switch off: ask whether to turn it on (answer with Answer).
	public event Action<ForegroundGame>? AskRequested;

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
		gameInFront = seen != null;
		ForegroundGame? ask = null;
		lock (gate)
		{
			if (active != null && !watcher.IsRunning(active))
			{
				EndSession();
			}
			if (seen != null && (active == null || !SameLaunch(seen, active)))
			{
				// Another known game took over.
				if (active != null)
				{
					EndSession();
				}
				active = seen;
				if (launches.Count >= LaunchMemoryLimit)
				{
					launches.Clear();
				}
				if (launches.Add(LaunchId(seen)))
				{
					if (always.Contains(seen.Key))
					{
						SetOn(true, ModeReason.Always, seen.Key);
					}
					else if (!on && askOnKnownGame && !neverAsk.Contains(seen.Key))
					{
						ask = seen;
					}
				}
				else if (always.Contains(seen.Key) && !on)
				{
					// Back to a launch seen before: an "always" game turns on again.
					SetOn(true, ModeReason.Always, seen.Key);
				}
			}
		}
		Apply();
		if (ask != null)
		{
			AskRequested?.Invoke(ask);
		}
	}

	// The user flips the switch. On with a game in front: it turns off again when that game exits.
	public void SetSupportedMode(bool value)
	{
		lock (gate)
		{
			if (value == on)
			{
				return;
			}
			SetOn(value, value ? ModeReason.User : ModeReason.Default, value ? active?.Key : null);
		}
		MemoryChanged?.Invoke();
		Apply();
	}

	// The answer to the question for a game. Turn on / always only act while that game is still the one in front.
	public void Answer(ForegroundGame game, AskAnswer answer)
	{
		bool memory = false;
		lock (gate)
		{
			if (answer == AskAnswer.Always)
			{
				memory = always.Add(game.Key);
			}
			if (answer != AskAnswer.NotNow && active != null && string.Equals(active.Key, game.Key, StringComparison.OrdinalIgnoreCase))
			{
				SetOn(true, answer == AskAnswer.Always ? ModeReason.Always : ModeReason.Game, active.Key);
			}
		}
		if (memory)
		{
			MemoryChanged?.Invoke();
		}
		Apply();
	}

	public bool IsAlways(string key)
	{
		lock (gate)
		{
			return always.Contains(key);
		}
	}

	public bool IsNeverAsk(string key)
	{
		lock (gate)
		{
			return neverAsk.Contains(key);
		}
	}

	// Adds or removes a game from the "always" list.
	public void SetAlways(string key, bool value)
	{
		bool changed;
		lock (gate)
		{
			changed = key.Length > 0 && (value ? always.Add(key) : always.Remove(key));
		}
		if (changed)
		{
			MemoryChanged?.Invoke();
		}
	}

	public void SetNeverAsk(string key, bool value)
	{
		bool changed;
		lock (gate)
		{
			changed = key.Length > 0 && (value ? neverAsk.Add(key) : neverAsk.Remove(key));
		}
		if (changed)
		{
			MemoryChanged?.Invoke();
		}
	}

	public IReadOnlyList<UserGame> UserGames
	{
		get
		{
			lock (gate)
			{
				return userGames.ToList();
			}
		}
	}

	// Adds a game by its exe (a path or a file name); false when it is blank or already there.
	public bool AddUserGame(UserGame game)
	{
		if (GameCatalog.Normalize(game.Path).Length == 0)
		{
			return false;
		}
		lock (gate)
		{
			if (userGames.Any(g => string.Equals(g.Path, game.Path, StringComparison.OrdinalIgnoreCase)))
			{
				return false;
			}
			userGames.Add(game);
			watcher.UserGames = userGames.ToList();
		}
		MemoryChanged?.Invoke();
		CatalogChanged?.Invoke();
		return true;
	}

	public bool RemoveUserGame(string path)
	{
		lock (gate)
		{
			if (userGames.RemoveAll(g => string.Equals(g.Path, path, StringComparison.OrdinalIgnoreCase)) == 0)
			{
				return false;
			}
			watcher.UserGames = userGames.ToList();
		}
		MemoryChanged?.Invoke();
		CatalogChanged?.Invoke();
		return true;
	}

	// Running apps that could be added as games.
	public IReadOnlyList<ProcessInfo> RunningApps() => watcher.RunningApps();

	// Saved memory (settings load).
	public void LoadMemory(GameMemory memory)
	{
		lock (gate)
		{
			always.Clear();
			always.UnionWith(memory.Always.Where(k => !string.IsNullOrWhiteSpace(k)));
			neverAsk.Clear();
			neverAsk.UnionWith(memory.NeverAsk.Where(k => !string.IsNullOrWhiteSpace(k)));
			userGames = memory.UserGames.Where(g => GameCatalog.Normalize(g.Path).Length > 0)
				.DistinctBy(g => g.Path, StringComparer.OrdinalIgnoreCase).ToList();
			watcher.UserGames = userGames.ToList();
			askOnKnownGame = memory.AskOnKnownGame;
			LoadPadTypes(memory.PadTypes, memory.ForcedPad);
			if (memory.SupportedModeOn && !on)
			{
				SetOn(true, ModeReason.User, null);
			}
		}
		CatalogChanged?.Invoke();
		Apply();
	}

	public GameMemory SnapshotMemory()
	{
		lock (gate)
		{
			return new GameMemory(on && onFor == null, askOnKnownGame, always.Order(StringComparer.OrdinalIgnoreCase).ToList(),
				neverAsk.Order(StringComparer.OrdinalIgnoreCase).ToList(), userGames.ToList(), SnapshotPadTypes(), forcedPad);
		}
	}

	// Catalog games plus the games the user added, by name.
	public IReadOnlyList<KnownGame> KnownGames()
	{
		lock (gate)
		{
			var list = catalog.Games
				.Select(g => new KnownGame(g.Name, g.Exe.FirstOrDefault() ?? string.Empty, g.Haptics, g.AdaptiveTriggers, false, g,
					ForegroundGame.KeyOf(g, string.Empty)))
				.ToList();
			foreach (UserGame game in userGames)
			{
				CatalogGame? match = catalog.Find(game.Exe);
				list.Add(new KnownGame(game.Name, game.Exe, match?.Haptics ?? false, match?.AdaptiveTriggers ?? false, true, match,
					ForegroundGame.KeyOf(match, game.Exe), game.Path));
			}
			return list.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
		}
	}

	// The same process of the same game: alt-tabbing back is not a new launch.
	private static bool SameLaunch(ForegroundGame a, ForegroundGame b) =>
		a.ProcessId == b.ProcessId && string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);

	private static string LaunchId(ForegroundGame game) => game.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + game.Key;

	// Caller holds the lock. The game in front ended (exited or another took over): a switch turned on for it goes off.
	private void EndSession()
	{
		ForegroundGame? ended = active;
		active = null;
		if (on && ended != null && onFor != null && string.Equals(onFor, ended.Key, StringComparison.OrdinalIgnoreCase))
		{
			SetOn(false, ModeReason.Default, null);
		}
	}

	// Caller holds the lock.
	private void SetOn(bool value, ModeReason why, string? forGame)
	{
		on = value;
		reason = value ? why : ModeReason.Default;
		onFor = value ? forGame : null;
	}

	private void Apply()
	{
		GameModeState? changed = null;
		BridgeProfile? toApply = null;
		lock (gate)
		{
			GameModeState next = new(active?.Exe, active?.Name, on ? GameMode.Supported : GameMode.Unsupported, reason, active?.Match);
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
		ApplyPad();
		if (changed != null)
		{
			StateChanged?.Invoke(changed);
		}
	}
}
