// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;

namespace Kopru.Core.Games;

// The known game running (null when none). Mode is always Supported: the DualSense Mode switch is gone and every game gets
// what it sends (it stays only for the readers that still look at it).
public sealed record GameModeState(string? Exe, string? Name, GameMode Mode, CatalogGame? Match = null)
{
	public static GameModeState Initial { get; } = new(null, null, GameMode.Supported);
}

// One row of the supported games list: a catalog game or a game the user added. Key matches ForegroundGame.Key.
public sealed record KnownGame(string Name, string Exe, bool Haptics, bool AdaptiveTriggers, bool IsUserAdded, CatalogGame? Catalog, string Key,
	string? Path = null);

// What is saved: the games the user added, the controller type per game and the default controller type for the games
// without one (null: Automatic).
public sealed record GameMemory(IReadOnlyList<UserGame> UserGames,
	IReadOnlyDictionary<string, Kopru.Core.Virtual.VirtualPadType>? PadTypes = null, Kopru.Core.Virtual.VirtualPadType? ForcedPad = null)
{
	public static GameMemory Default { get; } = new([]);
}

// The known game that runs and the one profile the bridge runs with. Only known games are looked at (GameWatcher): the game
// in front, and (while no game runs and some game has its own controller type) a known game whose process appears. The
// active game drives the active game line, the controller type (GameModePadTypes) and the profile per game. Tick is
// called about once a second (tests call it directly). Thread-safe; events are raised on the calling thread, outside the
// lock.
public sealed partial class GameModeController
{
	private readonly object gate = new();
	private readonly IGameWatcher watcher;
	private readonly IGameBridge? bridge;
	private List<UserGame> userGames = [];

	private GameProfile profile = new();
	private GameCatalog catalog;
	private ForegroundGame? active;
	private long gameSession;
	private volatile string? gameInFront;
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

	// The profile every game runs with.
	public GameProfile Profile
	{
		get
		{
			lock (gate)
			{
				return profile;
			}
		}
	}

	// The known game running right now (in front when it was found that way, or found by its process starting), or null.
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
	public bool IsGameInFront => gameInFront != null;

	// The exe of the game in front at the last Tick (for the PS tap log), or null.
	public string? GameInFront => gameInFront;

	// The current game changed.
	public event Action<GameModeState>? StateChanged;

	// The user's games or the controller types changed (to be saved).
	public event Action? MemoryChanged;

	public void SetProfile(GameProfile value)
	{
		lock (gate)
		{
			profile = value;
		}
		Apply();
	}

	// Where the game lines go ("game: ..."; exe file names only, never paths). Tests replace it.
	public Action<string> Log { get; set; } = Diagnostics.AppLog.Write;

	public void Tick()
	{
		ForegroundGame? seen = watcher.Poll();
		gameInFront = seen?.Exe;
		List<string> lines = [];
		bool ended;
		bool started = false;
		IReadOnlySet<string>? lookFor = null;
		lock (gate)
		{
			ForegroundGame? before = active;
			if (active != null && !watcher.IsRunning(active))
			{
				EndSession("it exited", lines);
			}
			if (seen != null && (active == null || !SameLaunch(seen, active)))
			{
				// Another known game took over (the first one still runs: its controller type is kept, see ApplyPad).
				started = active == null;
				if (active != null)
				{
					EndSession($"{seen.Name} took over", lines);
				}
				StartSession(seen, "is the active game", lines);
			}
			if (active == null)
			{
				lookFor = GamesWithOtherPad();
			}
			ended = before != null && !ReferenceEquals(before, active);
		}
		if (lookFor is { Count: > 0 } && watcher.FindRunning(lookFor) is { } launched)
		{
			lock (gate)
			{
				if (active == null)
				{
					started = true;
					StartSession(launched, "started", lines);
				}
			}
		}
		foreach (string line in lines)
		{
			Log(line);
		}
		if (ended)
		{
			// The game's hold on the light ends with it (a game still writing its light takes it back at once).
			bridge?.GameEnded();
		}
		Apply(started);
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

	// The other games found installed (hidden ones left out), recognised by their install folder after every catalog match.
	public IReadOnlyList<FoundGame> FoundGames
	{
		get => watcher.FoundGames;
		set => watcher.FoundGames = value;
	}

	// Saved memory (settings load).
	public void LoadMemory(GameMemory memory)
	{
		lock (gate)
		{
			userGames = memory.UserGames.Where(g => GameCatalog.Normalize(g.Path).Length > 0)
				.DistinctBy(g => g.Path, StringComparer.OrdinalIgnoreCase).ToList();
			watcher.UserGames = userGames.ToList();
			LoadPadTypes(memory.PadTypes, memory.ForcedPad);
		}
		CatalogChanged?.Invoke();
		Apply();
	}

	public GameMemory SnapshotMemory()
	{
		lock (gate)
		{
			return new GameMemory(userGames.ToList(), SnapshotPadTypes(), forcedPad);
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

	// Caller holds the lock. A known game became the active game: a new game session (the fallback trigger rule).
	private void StartSession(ForegroundGame game, string how, List<string> lines)
	{
		active = game;
		gameSession++;
		lines.Add($"game: {game.Name} {how} ({game.Exe})");
	}

	// Caller holds the lock. The active game ended (exited or another took over).
	private void EndSession(string why, List<string> lines)
	{
		ForegroundGame? ended = active;
		active = null;
		gameSession++;
		if (ended != null)
		{
			lines.Add($"game: {ended.Name} is no longer the active game ({why})");
		}
	}

	// started: a game began running at this tick (its controller type may be put in at once; see ApplyPad).
	private void Apply(bool started = false)
	{
		GameModeState? changed = null;
		BridgeProfile? toApply = null;
		lock (gate)
		{
			GameModeState next = new(active?.Exe, active?.Name, GameMode.Supported, active?.Match);
			if (next != state)
			{
				state = next;
				changed = next;
			}
			BridgeProfile bridgeProfile = GameProfiles.For(profile, gameSession);
			if (applied == null || !applied.SameAs(bridgeProfile))
			{
				applied = toApply = bridgeProfile;
			}
		}
		if (toApply != null)
		{
			bridge?.ApplyProfile(toApply);
		}
		ApplyPad(started);
		if (changed != null)
		{
			StateChanged?.Invoke(changed);
		}
	}
}
