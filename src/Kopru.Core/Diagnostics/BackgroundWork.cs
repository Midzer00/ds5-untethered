// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Core.Diagnostics;

// Cheap counters of the app's recurring background work (no per-event logging): a session's log shows the effect of the
// idle cost work without a profiler. Reporting writes one line at most every ReportInterval and one at exit.
public static class BackgroundWork
{
	public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(10);

	private static long hidListings, healthRefreshes, healthFull, steamParses, stateRefreshes, settingsSaves;
	private static readonly object Gate = new();
	private static Counts reportedCounts;
	private static long reportedAt = Environment.TickCount64;
	private static Timer? timer;

	public readonly record struct Counts(long HidListings, long HealthRefreshes, long HealthFull, long SteamParses, long StateRefreshes,
		long SettingsSaves)
	{
		public static Counts operator -(Counts a, Counts b) => new(a.HidListings - b.HidListings, a.HealthRefreshes - b.HealthRefreshes,
			a.HealthFull - b.HealthFull, a.SteamParses - b.SteamParses, a.StateRefreshes - b.StateRefreshes, a.SettingsSaves - b.SettingsSaves);
	}

	// A full listing of the HID devices (SetupAPI enumeration).
	public static void HidListing() => Interlocked.Increment(ref hidListings);

	// A health report rebuilt; full: the slow system facts (installed drivers, restart pending, adapter file) were read too.
	public static void HealthRefresh(bool full)
	{
		Interlocked.Increment(ref healthRefreshes);
		if (full)
		{
			Interlocked.Increment(ref healthFull);
		}
	}

	// A Steam config file read and parsed.
	public static void SteamConfigParse() => Interlocked.Increment(ref steamParses);

	// The controller service raised StateChanged.
	public static void StateRefresh() => Interlocked.Increment(ref stateRefreshes);

	// settings.json written.
	public static void SettingsSave() => Interlocked.Increment(ref settingsSaves);

	public static Counts Snapshot() => new(Interlocked.Read(ref hidListings), Interlocked.Read(ref healthRefreshes), Interlocked.Read(ref healthFull),
		Interlocked.Read(ref steamParses), Interlocked.Read(ref stateRefreshes), Interlocked.Read(ref settingsSaves));

	// "background work: last 600 s, hid listings N, health refreshes N (full M), steam config parses N, state refreshes N, settings saves N"
	public static string Format(Counts c, TimeSpan elapsed) => string.Create(CultureInfo.InvariantCulture,
		$"background work: last {elapsed.TotalSeconds:F0} s, hid listings {c.HidListings}, health refreshes {c.HealthRefreshes} (full {c.HealthFull}), steam config parses {c.SteamParses}, state refreshes {c.StateRefreshes}, settings saves {c.SettingsSaves}");

	// Writes one line every ReportInterval until StopReporting (the app; idempotent).
	public static void StartReporting()
	{
		lock (Gate)
		{
			reportedCounts = Snapshot();
			reportedAt = Environment.TickCount64;
			timer ??= new Timer(_ => Report(), null, ReportInterval, ReportInterval);
		}
	}

	// The last line (app exit).
	public static void StopReporting()
	{
		lock (Gate)
		{
			if (timer == null)
			{
				return;
			}
			timer.Dispose();
			timer = null;
		}
		Report();
	}

	private static void Report()
	{
		string line;
		lock (Gate)
		{
			Counts now = Snapshot();
			long at = Environment.TickCount64;
			line = Format(now - reportedCounts, TimeSpan.FromMilliseconds(at - reportedAt));
			reportedCounts = now;
			reportedAt = at;
		}
		AppLog.Write(line);
	}
}
