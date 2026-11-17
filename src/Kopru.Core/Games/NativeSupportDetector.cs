// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;

namespace Kopru.Core.Games;

// Learns per game key (ForegroundGame.Key) whether a game uses the DualSense natively, from what the bridge saw while it was
// the active game and in the foreground: non-silent haptics audio (channels 3-4) or a trigger effect other than off on
// NativeTicks separate looks (about a second each) within one game session means native, so one stray report or a short
// sound while switching windows is not enough; rumble alone means classic. Signals never downgrade native; classic is only
// recorded for a game without a result yet. Forget clears a game (the user chose a non-DualSense type for it).
public sealed class NativeSupportDetector
{
	// Looks with native signals needed within one game session.
	public const int NativeTicks = 5;

	private readonly Dictionary<string, bool> learned = new(StringComparer.OrdinalIgnoreCase);
	private string? evidenceKey;
	private long evidenceSession;
	private int evidence;

	// game key -> true (native) / false (classic rumble only).
	public IReadOnlyDictionary<string, bool> Learned => learned;

	public void Load(IEnumerable<KeyValuePair<string, bool>> values)
	{
		learned.Clear();
		foreach ((string game, bool native) in values)
		{
			string key = game?.Trim() ?? string.Empty;
			if (key.Length > 0)
			{
				learned[key] = native;
			}
		}
	}

	public bool? Get(string key) => learned.TryGetValue(key, out bool native) ? native : null;

	// True when the result for the game changed. session: the game session the signals were seen in (evidence from an
	// earlier session or another game does not add up).
	public bool Observe(string key, GameSignals signals, long session = 0)
	{
		bool? before = Get(key);
		if (signals.IsNative && before != true)
		{
			if (!string.Equals(evidenceKey, key, StringComparison.OrdinalIgnoreCase) || evidenceSession != session)
			{
				evidenceKey = key;
				evidenceSession = session;
				evidence = 0;
			}
			if (++evidence >= NativeTicks)
			{
				learned[key] = true;
				evidenceKey = null;
			}
		}
		else if (signals.Rumble && before == null)
		{
			learned[key] = false;
		}
		return Get(key) != before;
	}

	// True when the game had a result.
	public bool Forget(string key)
	{
		if (string.Equals(evidenceKey, key, StringComparison.OrdinalIgnoreCase))
		{
			evidenceKey = null;
		}
		return learned.Remove(key);
	}
}
