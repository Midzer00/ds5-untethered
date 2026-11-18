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

// The virtual Xbox Series pad could not be used, so games got Xbox 360 instead: Reason says why (for the log); Remembered:
// this PC remembers it, so Automatic gives Xbox 360 from now on.
public sealed record XboxSeriesFallback(string Reason, bool Remembered);

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
// Fallback: the swap is the Xbox Series pad falling back to Xbox 360 (XboxSeriesFallback tells the user instead).
public sealed record PadSwap(VirtualPadType From, VirtualPadType To, string? GameKey, string? GameName, bool Fallback = false);

// The controller type rules, for the running game: (1) its own choice, else Automatic: (2) native DualSense: DualSense when
// it is in the supported list or the bridge learned that it uses the DualSense natively (NativeSupportDetector: haptics or
// trigger effects seen while it was in front), or a Steam game whose store page lists "DualSense Controller Support"
// (PlayStationSupport, shipped with the app, then what Steam's own cache on this PC says, FoundGame.PlayStation); (3) Xbox
// trigger rumble: Xbox Series for a game in the shipped list (XboxTriggerGames) or learned to send trigger rumble
// (XboxTriggerDetector), only while the experimental Xbox Trigger Rumble setting is on (XboxTriggerRumbleOn, off by
// default) and the Xbox Series pad can be used here (Windows 10 21H2 or newer, and it did not fail to start on this PC
// before: XboxSeriesUsableForAutomatic); (4) a Steam game listing "DualShock Controller Support" alone: DualShock 4. A game
// added by hand whose exe lies in a Steam library's install folder counts as that Steam app. (5) Nothing known: the default
// type (ForcedPadType, "used for games we know nothing about") when one is set, (6) else Xbox 360 (never Xbox Series: only
// games known to send trigger rumble get the experimental pad). An Xbox Series choice is the user's and holds with the
// setting off too (only its trigger rumble is not played then); on a PC where it can't be used it gives Xbox 360 (EffectivePad). With no game running: the default type, else DualSense. The type follows the running game: it is put in when
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
	private readonly XboxTriggerDetector xboxTriggers = new();
	private PlayStationSupport playStation = PlayStationSupport.LoadEmbedded();
	private XboxTriggerGames triggerGames = XboxTriggerGames.LoadEmbedded();
	private bool xboxSeriesOs = XboxSeriesSupport.OsSupported;
	// The experimental Xbox Trigger Rumble setting (saved with the games, off by default).
	private bool xboxTriggerRumbleOn;
	// The Xbox Series pad failed to start on this PC before (saved: RememberAfter failures in a row, the last one when and
	// with which app version), and in this run.
	private bool xboxSeriesRemembered;
	private int xboxSeriesFailures;
	private DateTimeOffset? xboxSeriesFailedAt;
	private string? xboxSeriesFailedVersion;
	private bool xboxSeriesFailedNow;
	private bool xboxSeriesOsAnnounced;

	// Failures in a row (one per run at most) before this PC remembers that the Xbox Series pad does not start here: one
	// can be a slow first driver install or a pad still held by Windows after a crash.
	public const int XboxSeriesRememberAfter = 2;

	// A remembered failure is tried again (Automatic gives Xbox Series once more) after this long, or with another app version.
	public static readonly TimeSpan XboxSeriesRetryAfter = TimeSpan.FromDays(7);

	// The clock and app version of the retry (tests set them).
	public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

	public string? AppVersion { get; set; } = typeof(GameModeController).Assembly
		.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
		.OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;

	// Windows is new enough for the Xbox Series pad (tests set it).
	public bool XboxSeriesOsSupported
	{
		get
		{
			lock (gate)
			{
				return xboxSeriesOs;
			}
		}
		set
		{
			lock (gate)
			{
				xboxSeriesOs = value;
			}
			ApplyPad(false);
		}
	}

	// Automatic may give Xbox Series: Windows is new enough, the pad did not fail in this run, and this PC does not remember
	// it failing (or the retry is due).
	public bool XboxSeriesUsableForAutomatic
	{
		get
		{
			lock (gate)
			{
				return SeriesForAutomatic;
			}
		}
	}

	// The Xbox Series pad failed to start on this PC before (saved; Automatic gives Xbox 360).
	public bool XboxSeriesFailedBefore
	{
		get
		{
			lock (gate)
			{
				return xboxSeriesRemembered;
			}
		}
	}

	// The experimental Xbox Trigger Rumble setting (off by default; saved with the games, GameMemory.XboxTriggerRumbleOn).
	// On: the game's trigger motor levels play on the DualSense triggers (with the profile's Trigger Rumble choice) and
	// Automatic gives Xbox Series to the listed and learned trigger rumble games. Off: the trigger motor levels are not played
	// (BridgeProfile.XboxTriggerRumbleOn; the triggers keep the fallback effect), Automatic never gives Xbox Series (the
	// trigger rumble games get what they got before: DualSense, DualShock 4, the default type, else Xbox 360) and the watcher
	// does not recognise games by the trigger rumble list alone. Xbox Series is an ordinary controller type either way: a
	// choice of it holds (input, rumble as haptics, Create as View), only its trigger rumble waits for the setting. Learning
	// keeps going while off (a game seen sending trigger rumble on an Xbox Series choice is remembered, XboxTriggerDetector):
	// it costs nothing and fills the list, and it changes no pick until the setting is on, since Automatic reads it only then.
	// Raises MemoryChanged when it changes.
	public bool XboxTriggerRumbleOn
	{
		get
		{
			lock (gate)
			{
				return xboxTriggerRumbleOn;
			}
		}
		set
		{
			lock (gate)
			{
				if (xboxTriggerRumbleOn == value)
				{
					return;
				}
				xboxTriggerRumbleOn = value;
			}
			Log($"game: Xbox trigger rumble (experimental) {(value ? "on" : "off")}");
			ShareTriggerGames();
			// The profile (whether the bridge plays trigger rumble) and the controller type.
			Apply();
			MemoryChanged?.Invoke();
		}
	}

	// Hands the watcher the trigger rumble list while the setting is on, else none. Called outside the lock.
	private void ShareTriggerGames()
	{
		if (watcher is GameWatcher gameWatcher)
		{
			XboxTriggerGames shared;
			lock (gate)
			{
				shared = xboxTriggerRumbleOn ? triggerGames : XboxTriggerGames.Empty;
			}
			gameWatcher.TriggerGames = shared;
		}
	}

	// The games that send Xbox trigger rumble (the shipped list by default; tests set their own). The watcher recognises them
	// while XboxTriggerRumbleOn.
	public XboxTriggerGames TriggerGames
	{
		get
		{
			lock (gate)
			{
				return triggerGames;
			}
		}
		set
		{
			lock (gate)
			{
				triggerGames = value;
			}
			ShareTriggerGames();
			ApplyPad(false);
			MemoryChanged?.Invoke();
		}
	}

	// The game keys learned to send Xbox trigger rumble.
	public IReadOnlyCollection<string> LearnedTriggerGames
	{
		get
		{
			lock (gate)
			{
				return TriggerKeys();
			}
		}
	}

	// The Xbox Series pad fell back to Xbox 360 (raised outside the lock, on the caller's thread).
	public event Action<XboxSeriesFallback>? XboxSeriesFellBack;

	// The bridge: Windows did not start the Xbox Series pad (no Set Device State START in time). Games get Xbox 360 now and
	// in this run. Counted once per run; after XboxSeriesRememberAfter in a row this PC remembers it, so Automatic picks
	// Xbox 360 until the retry is due.
	public void ReportXboxSeriesFailed(string reason)
	{
		bool remembered;
		int failures;
		lock (gate)
		{
			if (xboxSeriesFailedNow)
			{
				return;
			}
			xboxSeriesFailedNow = true;
			failures = xboxSeriesFailures = Math.Max(xboxSeriesFailures, 0) + 1;
			if (failures >= XboxSeriesRememberAfter)
			{
				xboxSeriesRemembered = true;
				xboxSeriesFailedAt = UtcNow();
				xboxSeriesFailedVersion = AppVersion;
			}
			remembered = xboxSeriesRemembered;
		}
		Log(remembered
			? $"game: Xbox Series pad fell back to Xbox 360 ({reason}); failure {failures} in a row, remembered on this PC (tried again in {XboxSeriesRetryAfter.TotalDays:F0} days or with another app version)"
			: $"game: Xbox Series pad fell back to Xbox 360 ({reason}); failure {failures} in a row, tried again next time");
		MemoryChanged?.Invoke();
		ApplyPad(true, fallback: true);
		XboxSeriesFellBack?.Invoke(new XboxSeriesFallback(reason, remembered));
	}

	// The bridge: Windows started an Xbox Series pad. Earlier failures are forgotten (it works here now).
	public void ReportXboxSeriesStarted()
	{
		bool cleared;
		lock (gate)
		{
			cleared = xboxSeriesRemembered || xboxSeriesFailures > 0;
			xboxSeriesRemembered = false;
			xboxSeriesFailures = 0;
			xboxSeriesFailedAt = null;
			xboxSeriesFailedVersion = null;
		}
		if (cleared)
		{
			Log("game: Xbox Series pad started by Windows; the earlier fallback to Xbox 360 is forgotten");
			MemoryChanged?.Invoke();
			ApplyPad(false);
		}
	}

	// A remembered failure is old enough, or from another app version, to try Xbox Series again. Caller holds the lock.
	private bool XboxSeriesRetryDue =>
		xboxSeriesFailedAt is not { } at || !string.Equals(xboxSeriesFailedVersion, AppVersion, StringComparison.Ordinal)
		|| UtcNow() - at >= XboxSeriesRetryAfter;

	// Whether a game sends Xbox trigger rumble: in the shipped list or learned (whatever its other support and the setting).
	public bool UsesXboxTriggers(string key, FoundGame? found = null)
	{
		lock (gate)
		{
			ForegroundGame? running = RunningAs(key);
			found ??= running?.Found ?? FoundFor(key);
			return IsXboxTriggerGame(key, running, found, SteamAppFor(key, running, ref found));
		}
	}

	// Whether Automatic gives a game DualSense for what we know about it (listed, learned native, Steam's DualSense flag).
	public bool SupportsDualSense(string key, FoundGame? found = null)
	{
		lock (gate)
		{
			ForegroundGame? running = RunningAs(key);
			if (NativeDualSense(key, running))
			{
				return true;
			}
			found ??= running?.Found ?? FoundFor(key);
			return PlayStationFor(SteamAppFor(key, running, ref found), found) == PlayStationPad.DualSense;
		}
	}

	// Caller holds the lock.
	private bool SeriesForAutomatic => xboxSeriesOs && !xboxSeriesFailedNow && (!xboxSeriesRemembered || XboxSeriesRetryDue);

	// Caller holds the lock. The pad games really get for a wanted type: Xbox Series only where it can be used.
	private VirtualPadType EffectivePad(VirtualPadType wanted) =>
		wanted == VirtualPadType.XboxSeries && (!xboxSeriesOs || xboxSeriesFailedNow) ? VirtualPadType.Xbox360 : wanted;

	// Caller holds the lock.
	private List<string> TriggerKeys() => xboxTriggers.Learned.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

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

	// The type the game gets on Automatic (what "Otomatik (...)" shows): the Automatic rules 2-6.
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

	// Null: back to Automatic. A change for the running game waits (Pending). Choosing a type other than DualSense forgets
	// that the game was learned to use the DualSense natively, and one other than Xbox Series that it was learned to send
	// Xbox trigger rumble (the user says it does not), so Automatic no longer gives it that type.
	public void SetPadType(string key, VirtualPadType? type)
	{
		bool changed;
		lock (gate)
		{
			changed = padTypes.Set(key, type);
			if (type is VirtualPadType.Xbox360 or VirtualPadType.DualShock4 or VirtualPadType.XboxSeries && native.Forget(key))
			{
				changed = true;
			}
			if (type is { } t && t != VirtualPadType.XboxSeries && xboxTriggers.Forget(key))
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
	private void LoadPadTypes(GameMemory memory)
	{
		padTypes.Load(memory.PadTypes);
		forcedPad = memory.ForcedPad is { } f && Enum.IsDefined(f) ? f : null;
		native.Load((memory.NativeGames ?? []).Select(k => new KeyValuePair<string, bool>(k, true)));
		xboxTriggers.Load(memory.XboxTriggerGames);
		xboxTriggerRumbleOn = memory.XboxTriggerRumbleOn;
		xboxSeriesRemembered = memory.XboxSeriesFailed;
		// A remembered failure counts as enough failures in a row, so a failed retry remembers it again.
		xboxSeriesFailures = Math.Max(memory.XboxSeriesFailures, xboxSeriesRemembered ? XboxSeriesRememberAfter : 0);
		xboxSeriesFailedAt = xboxSeriesRemembered ? memory.XboxSeriesFailedAt : null;
		xboxSeriesFailedVersion = xboxSeriesRemembered ? memory.XboxSeriesFailedVersion : null;
	}

	// Caller holds the lock.
	private List<string> NativeKeys() =>
		native.Learned.Where(p => p.Value).Select(p => p.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

	// The one place the wanted type is decided. Caller holds the lock.
	private VirtualPadType ResolvePad() => EffectivePad(ResolveWanted());

	// Before EffectivePad (an Xbox Series choice where it can't be used). Caller holds the lock.
	private VirtualPadType ResolveWanted() => active == null ? forcedPad ?? VirtualPadType.DualSense : WantedRaw(active.Key, active);

	// Caller holds the lock. running: the game's running instance when it is the active game (its catalog match and found
	// entry), else null.
	private VirtualPadType WantedFor(string key, ForegroundGame? running) => EffectivePad(WantedRaw(key, running));

	private VirtualPadType WantedRaw(string key, ForegroundGame? running) => padTypes.Choice(key) ?? AutoPad(key, running);

	// Caller holds the lock.
	private ForegroundGame? RunningAs(string key) =>
		active != null && string.Equals(active.Key, key, StringComparison.OrdinalIgnoreCase) ? active : null;

	// The Automatic rules 2-6 (see the class comment) for a known game. Caller holds the lock.
	private VirtualPadType AutoPad(string key, ForegroundGame? running, FoundGame? found = null) =>
		KnownPad(key, running, found) ?? forcedPad ?? VirtualPadType.Xbox360;

	// Rules 2-4: the type what we know about the game gives, or null when we know nothing. Caller holds the lock.
	private VirtualPadType? KnownPad(string key, ForegroundGame? running, FoundGame? found = null)
	{
		if (NativeDualSense(key, running))
		{
			return VirtualPadType.DualSense;
		}
		found ??= running?.Found ?? FoundFor(key);
		long? appId = SteamAppFor(key, running, ref found);
		PlayStationPad? pad = PlayStationFor(appId, found);
		if (pad == PlayStationPad.DualSense)
		{
			return VirtualPadType.DualSense;
		}
		if (xboxTriggerRumbleOn && SeriesForAutomatic && IsXboxTriggerGame(key, running, found, appId))
		{
			return VirtualPadType.XboxSeries;
		}
		return pad == PlayStationPad.DualShock4 ? VirtualPadType.DualShock4 : null;
	}

	// Listed, running as a listed game, or learned to use the DualSense natively. Caller holds the lock.
	private bool NativeDualSense(string key, ForegroundGame? running) =>
		native.Get(key) == true || running?.Match != null || catalog.FindByKey(key) != null;

	// The game's Steam app id: its key, the found game, else the Steam library folder its exe lies in (a game added by hand
	// from a Steam library is that app; found becomes Steam's entry for it when the scan found it). Caller holds the lock.
	private long? SteamAppFor(string key, ForegroundGame? running, ref FoundGame? found)
	{
		long? appId = SteamAppIdOf(key) ?? found?.SteamAppId;
		if (appId == null && found == null && SteamAppIdByPath(key, running) is long byPath)
		{
			appId = byPath;
			found = FoundFor(FoundGame.KeyFor(InstalledSource.Steam, byPath.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}
		return appId;
	}

	// What the store says (the shipped list, then Steam's cache on this PC). Caller holds the lock.
	private PlayStationPad? PlayStationFor(long? appId, FoundGame? found) => (appId is long id ? playStation.For(id) : null) ?? found?.PlayStation;

	// In the Xbox trigger rumble list (by Steam app id, key, name or exe) or learned to send trigger rumble. Caller holds
	// the lock.
	private bool IsXboxTriggerGame(string key, ForegroundGame? running, FoundGame? found, long? appId)
	{
		if (xboxTriggers.IsLearned(key) || running?.Trigger != null)
		{
			return true;
		}
		if (triggerGames.Count == 0)
		{
			return false;
		}
		if (triggerGames.FindBySteamAppId(appId) != null || triggerGames.FindByKey(key) != null || triggerGames.FindByName(found?.Name) != null)
		{
			return true;
		}
		if (running != null && triggerGames.FindByExe(running.Exe) != null)
		{
			return true;
		}
		if (key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			if (triggerGames.FindByExe(key) != null)
			{
				return true;
			}
			UserGame? added = userGames.FirstOrDefault(g => string.Equals(ForegroundGame.KeyOf(catalog.Find(g.Exe), g.Exe), key, StringComparison.OrdinalIgnoreCase));
			return added != null && triggerGames.FindByName(added.Name) != null;
		}
		return false;
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
			if (EffectivePad(type) != current)
			{
				keys.Add(key);
			}
		}
		void Check(string key, FoundGame? found = null)
		{
			if (key.Length > 0 && padTypes.Choice(key) == null && !keys.Contains(key) && EffectivePad(AutoPad(key, null, found)) != current)
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
	// fallback: the Xbox Series pad just failed (the swap to Xbox 360 says so).
	private void ApplyPad(bool mayInterrupt, bool fallback = false)
	{
		VirtualPadType wanted;
		VirtualPadType? from = null;
		bool swap = false, stateChanged = false, osFallback = false;
		string? gameKey, gameName;
		lock (gate)
		{
			wanted = ResolvePad();
			// An Xbox Series choice on a Windows that is too old: Xbox 360, said once per run.
			if (!xboxSeriesOs && !xboxSeriesOsAnnounced && ResolveWanted() == VirtualPadType.XboxSeries)
			{
				xboxSeriesOsAnnounced = osFallback = true;
			}
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
				PadSwapped?.Invoke(new PadSwap(before, wanted, gameKey, gameName, fallback || osFallback));
			}
		}
		if (stateChanged)
		{
			PadStateChanged?.Invoke();
		}
		if (osFallback)
		{
			string reason = $"{XboxSeriesSupport.OsText} is older than Windows 10 21H2";
			Log($"game: Xbox Series chosen, Xbox 360 used ({reason})");
			XboxSeriesFellBack?.Invoke(new XboxSeriesFallback(reason, false));
		}
	}
}
