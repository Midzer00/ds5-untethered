// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Core.Diagnostics;

// How the Bluetooth link feels to a player. Measuring: too little data yet (just connected, resumed or stalled).
public enum LinkQuality
{
	Measuring,
	Good,
	Borderline,
	Poor
}

// The limits shared by the live link indicator (LinkQualityMonitor) and the adapter test (AdapterCheck), so the two never
// contradict each other. A first calibration, to be tuned on hardware.
//
// Delay is the "worst 1 %" extra delay (p99 of LinkAnalyzer's extra delay: what the radio, the Bluetooth stack and Windows
// add on top of the best report, judged against the controller's own clock) - the same number the Overview shows as
// "en kötü". Why these numbers:
// - Measured here with a good adapter about a metre away: p99 3.8-5.1 ms, single maxima of 16-28 ms, nothing lost,
//   550-740 reports a second (the microphone lowers that by ~15 % without adding delay). Earlier notes put a poor dongle
//   around 30 ms. Borderline at 15 ms is 3x a healthy link's p99; single spikes barely move a p99 of ~1000 reports.
// - Perception: the lowest published thresholds are for direct touch (Jota et al., CHI 2013: tapping latency below about
//   24 ms goes unnoticed; direct touch is the most sensitive case, a gamepad is indirect input), and local latency of about
//   40 ms measurably hurts aiming in shooters (Ivkovic et al., CHI 2015). An extra 30 ms that keeps coming back every second
//   sits between the two: the line where a player can start to feel it ("Poor").
// - Gaps: 50 ms without a report is three frames at 60 fps, a visible hitch; 100 ms is a stutter. One now and then is
//   normal radio life; three or more in 10 seconds is not.
// - Lost reports and the report rate: the adapter test's long-standing limits (0.5 / 2 %, 250 / 125 reports a second);
//   a healthy link loses nothing and stays above ~470 a second even with the microphone on.
public static class LinkLimits
{
	public const double BorderlineDelayMs = 15;
	// The "you can feel it" line drawn on the Overview chart.
	public const double PoorDelayMs = 30;
	public const double LongGapMs = 50;
	public const double VeryLongGapMs = 100;
	// Gaps are counted per this many seconds (shorter measurements count as this long, so they never flag sooner).
	public const double GapWindowSeconds = 10;
	public const int GapsPerWindow = 3;
	public const double BorderlineLostPercent = 0.5;
	public const double PoorLostPercent = 2;
	public const double BorderlineRateHz = 250;
	public const double PoorRateHz = 125;
	// A state already reached is left only below this share of its limit (delay and loss), so values hovering at a limit
	// do not flip the state back and forth.
	public const double RecoverFactor = 0.8;

	public static LinkQuality Delay(double worstMs, LinkQuality current = LinkQuality.Good) =>
		worstMs >= Edge(PoorDelayMs, current >= LinkQuality.Poor) ? LinkQuality.Poor
		: worstMs >= Edge(BorderlineDelayMs, current >= LinkQuality.Borderline) ? LinkQuality.Borderline
		: LinkQuality.Good;

	public static LinkQuality Lost(double percent, LinkQuality current = LinkQuality.Good) =>
		percent > Edge(PoorLostPercent, current >= LinkQuality.Poor) ? LinkQuality.Poor
		: percent > Edge(BorderlineLostPercent, current >= LinkQuality.Borderline) ? LinkQuality.Borderline
		: LinkQuality.Good;

	// longGaps counts every gap of LongGapMs or more (the very long ones included).
	public static LinkQuality Gaps(int longGaps, int veryLongGaps, double seconds)
	{
		double scale = GapWindowSeconds / Math.Max(seconds, GapWindowSeconds);
		return veryLongGaps * scale >= GapsPerWindow ? LinkQuality.Poor
			: longGaps * scale >= GapsPerWindow ? LinkQuality.Borderline
			: LinkQuality.Good;
	}

	public static LinkQuality Rate(double hz) =>
		hz < PoorRateHz ? LinkQuality.Poor : hz < BorderlineRateHz ? LinkQuality.Borderline : LinkQuality.Good;

	public static LinkQuality Classify(double worstMs, double lostPercent, int longGaps, int veryLongGaps, double seconds, double rateHz,
		LinkQuality current = LinkQuality.Good) =>
		Max(Max(Delay(worstMs, current), Lost(lostPercent, current)), Max(Gaps(longGaps, veryLongGaps, seconds), Rate(rateHz)));

	public static LinkQuality Max(LinkQuality a, LinkQuality b) => a > b ? a : b;

	private static double Edge(double limit, bool holding) => holding ? limit * RecoverFactor : limit;
}

// About one second of the running link: the worst-1 % extra delay of the latest reports (LatencyMeter.Snapshot) and what
// arrived since the previous sample (LatencyMeter.TakeCounts).
public readonly record struct LinkSecond(double WorstDelayMs, int Reports, int Lost, int LongGaps, int VeryLongGaps, double Seconds);

// The link as the UI shows it. Values are over the monitor's window; WarningDue: the one warning of this bad episode may
// be shown now (cleared by LinkQualityMonitor.AcknowledgeWarning).
public sealed record LinkQualityReading(LinkQuality State, double WorstDelayMs, double LostPercent, int LongGaps, double RateHz, bool WarningDue = false)
{
	public static LinkQualityReading Measuring { get; } = new(LinkQuality.Measuring, 0, 0, 0, 0);
}

// Turns the per-second samples into a steady state and decides when one warning is worth showing.
// - State: over the last WindowSamples samples, from the median worst-1 % delay (single bad seconds do not count),
//   the share of lost reports, long gaps and the report rate. Worse states show at once (the window already smooths);
//   better ones only after RecoverTime of better samples, and with LinkLimits.RecoverFactor on the limits.
// - What is not the radio: a gap of StallGap or more between samples means this program did not run (PC asleep, process
//   paused by a debugger, a hung system): the window starts over and the first SettleTime is skipped (the reports that
//   piled up meanwhile arrive in a burst). Seconds without any report are skipped too (a controller that stopped sending
//   is about to be lost or turned off; the connection toasts cover that). A connect, reconnect or virtual pad swap
//   calls Restart. What it cannot tell apart: a PC so busy that this program reads reports late looks like a slow link.
// - Warning: only when Poor has lasted WarnAfter, never in the first WarningGrace after a (re)start, at most one per bad
//   episode (an episode ends when the link is Good again) and at most one per WarningCooldown.
public sealed class LinkQualityMonitor
{
	public const int WindowSamples = 10;
	public const int MinSamples = 5;
	public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);
	public static readonly TimeSpan StallGap = TimeSpan.FromSeconds(3);
	public static readonly TimeSpan RecoverTime = TimeSpan.FromSeconds(10);
	public static readonly TimeSpan WarnAfter = TimeSpan.FromSeconds(15);
	public static readonly TimeSpan WarningGrace = TimeSpan.FromSeconds(20);
	public static readonly TimeSpan WarningCooldown = TimeSpan.FromMinutes(10);

	private readonly object gate = new();
	private readonly Queue<LinkSecond> window = new();
	private readonly Action<string>? log;
	private bool started;
	private long restartedAt;
	private long lastAt;
	private LinkQuality shown = LinkQuality.Measuring;
	private long? betterSince;
	private long? poorSince;
	private bool warned;
	private long? lastWarnAt;
	private LinkQualityReading? reading;

	// log: one line per state change (no device ids).
	public LinkQualityMonitor(Action<string>? log = null)
	{
		this.log = log;
	}

	// Null while nothing runs (before the first Restart and after Stop).
	public LinkQualityReading? Current
	{
		get
		{
			lock (gate)
			{
				return reading;
			}
		}
	}

	// A (re)connect, a virtual pad swap or anything else after which the first seconds say nothing about the radio.
	public void Restart(long nowMs, string reason)
	{
		lock (gate)
		{
			StartOver(nowMs);
			log?.Invoke($"link quality: measuring ({reason})");
		}
	}

	// The bridge stopped or lost the controller: nothing to show. The warning bookkeeping stays (one per episode, cooldown).
	public void Stop()
	{
		lock (gate)
		{
			window.Clear();
			started = false;
			shown = LinkQuality.Measuring;
			betterSince = poorSince = null;
			reading = null;
		}
	}

	public LinkQualityReading Add(LinkSecond sample, long nowMs)
	{
		lock (gate)
		{
			if (!started)
			{
				StartOver(nowMs);
			}
			else if (nowMs - lastAt >= StallGap.TotalMilliseconds)
			{
				log?.Invoke(string.Create(CultureInfo.InvariantCulture,
					$"link quality: no sample for {(nowMs - lastAt) / 1000.0:F1} s (PC asleep or this program paused), measuring again"));
				StartOver(nowMs);
			}
			lastAt = nowMs;
			if (nowMs - restartedAt < SettleTime.TotalMilliseconds || sample.Reports <= 0 || sample.Seconds <= 0)
			{
				return reading!;
			}
			window.Enqueue(sample);
			while (window.Count > WindowSamples)
			{
				window.Dequeue();
			}
			(double worst, double lost, int gaps, int veryLong, double seconds, double rate) = Totals();
			if (window.Count < MinSamples)
			{
				reading = new LinkQualityReading(LinkQuality.Measuring, worst, lost, gaps, rate);
				return reading;
			}
			LinkQuality raw = LinkLimits.Classify(worst, lost, gaps, veryLong, seconds, rate, shown);
			LinkQuality next = shown;
			if (shown == LinkQuality.Measuring || raw > shown)
			{
				next = raw;
				betterSince = null;
			}
			else if (raw < shown)
			{
				betterSince ??= nowMs;
				if (nowMs - betterSince.Value >= RecoverTime.TotalMilliseconds)
				{
					next = raw;
					betterSince = null;
				}
			}
			else
			{
				betterSince = null;
			}
			if (next != shown)
			{
				log?.Invoke(string.Create(CultureInfo.InvariantCulture,
					$"link quality: {shown} -> {next} (worst 1 % {worst:F1} ms, lost {lost:F1} %, {gaps} gaps over {LinkLimits.LongGapMs:F0} ms, {rate:F0} reports/s)"));
				shown = next;
			}
			if (shown == LinkQuality.Poor)
			{
				poorSince ??= nowMs;
			}
			else
			{
				poorSince = null;
				if (shown == LinkQuality.Good)
				{
					warned = false;
				}
			}
			bool due = shown == LinkQuality.Poor && !warned
				&& nowMs - poorSince!.Value >= WarnAfter.TotalMilliseconds
				&& nowMs - restartedAt >= WarningGrace.TotalMilliseconds
				&& (lastWarnAt == null || nowMs - lastWarnAt.Value >= WarningCooldown.TotalMilliseconds);
			reading = new LinkQualityReading(shown, worst, lost, gaps, rate, due);
			return reading;
		}
	}

	// The warning was shown: no other one until the link was Good again and the cooldown passed.
	public void AcknowledgeWarning(long nowMs)
	{
		lock (gate)
		{
			if (reading is not { WarningDue: true })
			{
				return;
			}
			warned = true;
			lastWarnAt = nowMs;
			reading = reading with { WarningDue = false };
			log?.Invoke(string.Create(CultureInfo.InvariantCulture,
				$"link quality: warned (poor for {(nowMs - (poorSince ?? nowMs)) / 1000.0:F0} s)"));
		}
	}

	private void StartOver(long nowMs)
	{
		window.Clear();
		started = true;
		restartedAt = lastAt = nowMs;
		shown = LinkQuality.Measuring;
		betterSince = poorSince = null;
		reading = LinkQualityReading.Measuring;
	}

	private (double Worst, double Lost, int Gaps, int VeryLong, double Seconds, double Rate) Totals()
	{
		double[] worst = [.. window.Select(s => s.WorstDelayMs).Order()];
		double median = worst.Length == 0 ? 0 : LinkAnalyzer.Percentile(worst, 50);
		long reports = window.Sum(s => (long)s.Reports);
		long lost = window.Sum(s => (long)s.Lost);
		double seconds = window.Sum(s => s.Seconds);
		return (median, reports + lost == 0 ? 0 : lost * 100.0 / (reports + lost), window.Sum(s => s.LongGaps), window.Sum(s => s.VeryLongGaps),
			seconds, seconds > 0 ? reports / seconds : 0);
	}
}
