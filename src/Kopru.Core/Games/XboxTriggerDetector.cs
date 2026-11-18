// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Games;

// Learns per game key which games send Xbox trigger rumble (the trigger motors of the Xbox Series pad), with the same
// safeguards as NativeSupportDetector: only what the game sent while it was the active game and in the foreground counts,
// and only Ticks separate looks (about a second each) within one game session make it learned, so a stray command or a
// short test does not. Forget clears a game (the user chose another controller type for it).
public sealed class XboxTriggerDetector
{
	// Looks with trigger rumble needed within one game session.
	public const int Ticks = 5;

	private readonly HashSet<string> learned = new(StringComparer.OrdinalIgnoreCase);
	private string? evidenceKey;
	private long evidenceSession;
	private int evidence;

	public IReadOnlyCollection<string> Learned => learned;

	public void Load(IEnumerable<string>? keys)
	{
		learned.Clear();
		foreach (string key in keys ?? [])
		{
			if (!string.IsNullOrWhiteSpace(key))
			{
				learned.Add(key.Trim());
			}
		}
	}

	public bool IsLearned(string key) => learned.Contains(key);

	// True when the game became learned. session: the game session the look belongs to (evidence from another session or
	// game does not add up).
	public bool Observe(string key, bool triggerRumble, long session = 0)
	{
		if (!triggerRumble || learned.Contains(key))
		{
			return false;
		}
		if (!string.Equals(evidenceKey, key, StringComparison.OrdinalIgnoreCase) || evidenceSession != session)
		{
			evidenceKey = key;
			evidenceSession = session;
			evidence = 0;
		}
		if (++evidence < Ticks)
		{
			return false;
		}
		learned.Add(key);
		evidenceKey = null;
		return true;
	}

	// True when the game was learned.
	public bool Forget(string key)
	{
		if (string.Equals(evidenceKey, key, StringComparison.OrdinalIgnoreCase))
		{
			evidenceKey = null;
		}
		return learned.Remove(key);
	}
}
