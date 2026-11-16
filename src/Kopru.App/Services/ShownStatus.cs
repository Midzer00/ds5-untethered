// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;

namespace Kopru.App.Services;

// What of a bridge status the app shows, at the precision it shows it: the state and error, the rounded battery and its
// charge state, the rounded input rate, whether haptics flow, the link delay as printed (0.1 ms, relay 0.01 ms), the
// link quality class with its warning, and whether headphones are in the controller's jack (read from the host beside
// its status; it has no event of its own). The bridge reports its status four times a second but its rates once a
// second; a status whose shown values did not change raises no StateChanged (except while a weak link warning is due).
public readonly record struct ShownStatus(BridgeState State, BridgeError LastError, string? LastErrorDetail, int? BatteryPercent,
	ChargeState? Charge, long InputRate, bool HapticsActive, long? LinkP50, long? LinkP99, long? Relay, LinkQuality? Link, bool LinkWarningDue,
	bool HeadphonesPlugged)
{
	public static ShownStatus Of(BridgeStatus s, bool headphonesPlugged = false) => new(s.State, s.LastError, s.LastErrorDetail,
		s.Battery?.Percent, s.Battery?.State,
		(long)Math.Round(s.InputRateHz), s.HapticsReportsPerSecond > 1,
		Tenths(s.LinkLatencyP50Ms), Tenths(s.LinkLatencyP99Ms), s.RelayMs is { } r ? (long)Math.Round(r * 100) : null,
		s.Link?.State, s.Link?.WarningDue == true, headphonesPlugged);

	private static long? Tenths(double? ms) => ms is { } v ? (long)Math.Round(v * 10) : null;
}

// Remembers the last shown status per player slot; Changed is true when the shown values differ from the last ones.
public sealed class ShownStatusFilter
{
	private readonly Dictionary<int, ShownStatus> last = [];

	public bool Changed(int slot, BridgeStatus status, bool headphonesPlugged = false)
	{
		ShownStatus next = ShownStatus.Of(status, headphonesPlugged);
		lock (last)
		{
			// A due weak link warning is looked at on every status (the toast may wait for the game to be left).
			if (last.TryGetValue(slot, out ShownStatus before) && before == next && !next.LinkWarningDue)
			{
				return false;
			}
			last[slot] = next;
			return true;
		}
	}
}
