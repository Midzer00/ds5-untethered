// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;

namespace Kopru.Core.Games;

// Learns per exe whether a game uses the DualSense natively, from what the bridge saw while it was in the foreground:
// non-silent haptics audio (channels 3-4) or a trigger effect other than off means native; rumble alone means classic.
// Native is never downgraded; classic is only recorded for a game without a result yet.
public sealed class NativeSupportDetector
{
	private readonly Dictionary<string, bool> learned = new(StringComparer.OrdinalIgnoreCase);

	// exe -> true (native) / false (classic rumble only).
	public IReadOnlyDictionary<string, bool> Learned => learned;

	public void Load(IEnumerable<KeyValuePair<string, bool>> values)
	{
		learned.Clear();
		foreach ((string exe, bool native) in values)
		{
			string key = GameCatalog.Normalize(exe);
			if (key.Length > 0)
			{
				learned[key] = native;
			}
		}
	}

	public bool? Get(string exe) => learned.TryGetValue(exe, out bool native) ? native : null;

	// True when the result for the exe changed.
	public bool Observe(string exe, GameSignals signals)
	{
		bool? before = Get(exe);
		if (signals.IsNative)
		{
			learned[exe] = true;
		}
		else if (signals.Rumble && before == null)
		{
			learned[exe] = false;
		}
		return Get(exe) != before;
	}
}
