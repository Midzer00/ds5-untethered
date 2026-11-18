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

// What is saved: the games the user added, the controller type per game, the default controller type for the games
// without one (null: Automatic), the game keys learned to use the DualSense natively (NativeSupportDetector) and to send
// Xbox trigger rumble (XboxTriggerDetector), whether this PC remembers that the Xbox Series pad does not start here, the
// failures in a row so far, when and with which app version it was remembered (the retry, GameModePadTypes), and the
// experimental Xbox Trigger Rumble setting (off by default: Automatic never gives Xbox Series then).
public sealed record GameMemory(IReadOnlyList<UserGame> UserGames,
	IReadOnlyDictionary<string, Kopru.Core.Virtual.VirtualPadType>? PadTypes = null, Kopru.Core.Virtual.VirtualPadType? ForcedPad = null,
	IReadOnlyList<string>? NativeGames = null, IReadOnlyList<string>? XboxTriggerGames = null, bool XboxSeriesFailed = false,
	int XboxSeriesFailures = 0, DateTimeOffset? XboxSeriesFailedAt = null, string? XboxSeriesFailedVersion = null,
	bool XboxTriggerRumbleOn = false)
{
	public static GameMemory Default { get; } = new([]);
}

// The known game that runs and the one profile the bridge runs with. Only known games are looked at (GameWatcher): the game
// in front, and (while no game runs and some known game would get another controller type) a known game whose process
// appears. What the bridge saw while the active game was in front (GameSignals) teaches whether it uses the DualSense natively. The
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
	// The active game was found by its process starting (the launch-time scan), not in front: a game that comes to the front
	// then takes over at once, controller type included (the game in front wins).
	private bool activeByScan;
	private long gameSession;
	private HashSet<string>? scannedKeys;
	private long scannedAt;
	private bool? padIsDefaultOutput;
	private long padIsDefaultOutputAt;
	private volatile string? gameInFront;
	private GameModeState state = GameModeState.Initial;
	private BridgeProfile? applied;

	public GameModeController(GameCatalog catalog, IGameWatcher watcher, IGameBridge? bridge)
	{
		this.catalog = catalog;
		this.watcher = watcher;
		this.bridge = bridge;
		ShareTriggerGames();
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

	// Milliseconds since some start (tests replace it).
	public Func<long> ClockMs { get; set; } = () => Environment.TickCount64;

	// While no game runs, running processes are listed (Process.GetProcesses) at most this often for the same games, so a
	// game's process starting gets its controller type within a few seconds without listing them every second.
	public static readonly TimeSpan LaunchScanInterval = TimeSpan.FromSeconds(3);

	// Whether the virtual DualSense is Windows' default output (asked at most every 10 s while haptics sound arrives). Then
	// any program's surround sound reaches the haptics channels, so haptics sound alone teaches nothing. Null: not known
	// (haptics count); a throw counts as true.
	public Func<bool>? PadIsDefaultOutput { get; set; }

	public void Tick()
	{
		ForegroundGame? seen = watcher.Poll();
		gameInFront = seen?.Exe;
		// What the game sent since the last tick: the game in front sent it (the bridge's own fallbacks are not counted).
		GameSignals signals = bridge?.TakeGameSignals() ?? default;
		if (signals.Haptics && !signals.Triggers && HapticsFromAnyProgram())
		{
			signals = signals with { Haptics = false };
		}
		List<string> lines = [];
		bool ended;
		bool started = false;
		bool learned = false;
		IReadOnlySet<string>? lookFor = null;
		lock (gate)
		{
			if (seen != null && active != null && SameLaunch(seen, active))
			{
				// It is in front now: no longer a game found only by its process.
				activeByScan = false;
			}
			if (seen != null && active != null && SameLaunch(seen, active) && native.Get(active.Key) != true)
			{
				native.Observe(active.Key, signals, gameSession);
				if (native.Get(active.Key) == true)
				{
					learned = true;
					lines.Add($"game: {active.Name} uses the DualSense natively (learned)");
				}
			}
			// Trigger rumble only reaches us through the Xbox Series pad: what the game in front sent there counts.
			if (seen != null && active != null && SameLaunch(seen, active) && appliedPad == Kopru.Core.Virtual.VirtualPadType.XboxSeries
				&& xboxTriggers.Observe(active.Key, signals.TriggerRumble, gameSession))
			{
				learned = true;
				lines.Add($"game: {active.Name} sends Xbox trigger rumble (learned)");
			}
			ForegroundGame? before = active;
			if (active != null && !watcher.IsRunning(active))
			{
				EndSession("it exited", lines);
			}
			if (seen != null && (active == null || !SameLaunch(seen, active)))
			{
				// Another known game took over (the first one still runs: its controller type is kept, see ApplyPad), unless the
				// first one was only found by its process (Wallpaper Engine-like apps in the background): the game in front wins.
				started = active == null || activeByScan;
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
		if (lookFor is { Count: > 0 } && LaunchScanDue(lookFor) && watcher.FindRunning(lookFor) is { } launched)
		{
			lock (gate)
			{
				if (active == null)
				{
					started = true;
					StartSession(launched, "started", lines);
					activeByScan = true;
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
			bool none;
			lock (gate)
			{
				none = active == null;
			}
			if (none)
			{
				// Gone with no game taking over: its last rumble stops (on a switch the new game takes the rumble over).
				bridge?.GameExited();
			}
		}
		if (learned)
		{
			MemoryChanged?.Invoke();
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
			LoadPadTypes(memory);
		}
		ShareTriggerGames();
		CatalogChanged?.Invoke();
		Apply();
	}

	public GameMemory SnapshotMemory()
	{
		lock (gate)
		{
			return new GameMemory(userGames.ToList(), SnapshotPadTypes(), forcedPad, NativeKeys(), TriggerKeys(), xboxSeriesRemembered,
				xboxSeriesFailures, xboxSeriesFailedAt, xboxSeriesFailedVersion, xboxTriggerRumbleOn);
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

	// The launch-time scan runs when the games to look for changed, else at most every LaunchScanInterval. Tick's thread only.
	private bool LaunchScanDue(IReadOnlySet<string> keys)
	{
		long now = ClockMs();
		if (scannedKeys != null && scannedKeys.SetEquals(keys) && now - scannedAt < LaunchScanInterval.TotalMilliseconds)
		{
			return false;
		}
		scannedKeys = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
		scannedAt = now;
		return true;
	}

	// Whether haptics sound may come from any program (the virtual DualSense is the default output). Tick's thread only.
	private bool HapticsFromAnyProgram()
	{
		if (PadIsDefaultOutput is not { } ask)
		{
			return false;
		}
		long now = ClockMs();
		if (padIsDefaultOutput is { } known && now - padIsDefaultOutputAt < 10_000)
		{
			return known;
		}
		bool result;
		try
		{
			result = ask();
		}
		catch (Exception)
		{
			result = true;
		}
		padIsDefaultOutput = result;
		padIsDefaultOutputAt = now;
		return result;
	}

	// Caller holds the lock. A known game became the active game: a new game session (the fallback trigger rule).
	private void StartSession(ForegroundGame game, string how, List<string> lines)
	{
		active = game;
		activeByScan = false;
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
			BridgeProfile bridgeProfile = GameProfiles.For(profile, gameSession) with { XboxTriggerRumbleOn = xboxTriggerRumbleOn };
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
