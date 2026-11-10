// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Kopru.Core.Bridge;
using Kopru.Core.Localization;

namespace Kopru.Core.Health;

// The last battery reading seen, kept across runs ("Son: %62 · 2 sa önce").
public sealed record LastKnownBattery(int Percent, bool Charging, DateTimeOffset Time)
{
	public string Describe(DateTimeOffset now, Language? l = null)
	{
		string when = RelativeTime.Format(now - Time, l);
		return Charging ? Text.LastKnownBatteryCharging(Percent, when, l) : Text.LastKnownBattery(Percent, when, l);
	}
}

// "az önce", "5 dk önce", "2 sa önce", "dün", "3 gün önce" (and English).
public static class RelativeTime
{
	public static string Format(TimeSpan age, Language? l = null)
	{
		if (age < TimeSpan.FromMinutes(1))
		{
			return Text.TimeJustNow(l);
		}
		if (age < TimeSpan.FromHours(1))
		{
			return Text.TimeMinutesAgo((int)age.TotalMinutes, l);
		}
		if (age < TimeSpan.FromDays(1))
		{
			return Text.TimeHoursAgo((int)age.TotalHours, l);
		}
		return age < TimeSpan.FromDays(2) ? Text.TimeYesterday(l) : Text.TimeDaysAgo((int)age.TotalDays, l);
	}
}

// %LOCALAPPDATA%\Untethered\last-battery.json. Record() writes when the reading changed or the stored one is older than
// RewriteAfter, so a steady stream of identical readings does not hit the disk every time.
public sealed class LastBatteryStore(string path)
{
	public const string FileName = "last-battery.json";

	public static readonly TimeSpan RewriteAfter = TimeSpan.FromMinutes(1);

	private readonly object gate = new();

	private LastKnownBattery? last;

	private bool loaded;

	public string Path { get; } = path;

	public static LastBatteryStore CreateDefault() => new(System.IO.Path.Combine(AppPaths.DataDirectory, FileName));

	public LastKnownBattery? Current
	{
		get
		{
			lock (gate)
			{
				if (!loaded)
				{
					last = Load();
					loaded = true;
				}
				return last;
			}
		}
	}

	// Returns true when the reading was written.
	public bool Record(BatteryStatus reading, DateTimeOffset now)
	{
		LastKnownBattery next = new(reading.Percent, reading.State is ChargeState.Charging, now);
		lock (gate)
		{
			LastKnownBattery? previous = Current;
			if (previous != null && previous.Percent == next.Percent && previous.Charging == next.Charging &&
				now - previous.Time < RewriteAfter)
			{
				return false;
			}
			last = next;
			try
			{
				string? dir = System.IO.Path.GetDirectoryName(Path);
				if (!string.IsNullOrEmpty(dir))
				{
					Directory.CreateDirectory(dir);
				}
				File.WriteAllText(Path, JsonSerializer.Serialize(next));
				return true;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				return false;
			}
		}
	}

	private LastKnownBattery? Load()
	{
		try
		{
			return File.Exists(Path) ? JsonSerializer.Deserialize<LastKnownBattery>(File.ReadAllText(Path)) : null;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
		{
			return null;
		}
	}
}
