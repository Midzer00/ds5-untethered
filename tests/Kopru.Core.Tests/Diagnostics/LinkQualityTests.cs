// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Kopru.Core.Diagnostics;
using Kopru.Core.Localization;

namespace Kopru.Core.Tests;

// The link quality model: limits, the per-second monitor (window, hysteresis, stalls) and its warning rules.
public class LinkQualityTests
{
	// A healthy second as measured on a good adapter about a metre away: worst 1 % ~4.5 ms, ~600 reports, nothing lost.
	private static LinkSecond Healthy => new(4.5, 600, 0, 0, 0, 1);

	private static LinkSecond Worst(double ms) => new(ms, 600, 0, 0, 0, 1);

	// Feeds one sample a second from `at` (ms), returns the last reading and moves `at` on.
	private static LinkQualityReading Feed(LinkQualityMonitor m, ref long at, LinkSecond s, int seconds = 1)
	{
		LinkQualityReading r = LinkQualityReading.Measuring;
		for (int i = 0; i < seconds; i++)
		{
			at += 1000;
			r = m.Add(s, at);
		}
		return r;
	}

	private static (LinkQualityMonitor Monitor, List<string> Log) Started(out long at)
	{
		List<string> log = [];
		LinkQualityMonitor m = new(log.Add);
		at = 0;
		m.Restart(at, "connected");
		return (m, log);
	}

	[Fact]
	public void Limits_GradeEachMeasure()
	{
		Assert.Equal(LinkQuality.Good, LinkLimits.Delay(5.1));
		Assert.Equal(LinkQuality.Borderline, LinkLimits.Delay(15));
		Assert.Equal(LinkQuality.Poor, LinkLimits.Delay(30));
		Assert.Equal(LinkQuality.Good, LinkLimits.Lost(0.5));
		Assert.Equal(LinkQuality.Borderline, LinkLimits.Lost(0.6));
		Assert.Equal(LinkQuality.Poor, LinkLimits.Lost(2.1));
		Assert.Equal(LinkQuality.Good, LinkLimits.Rate(470));
		Assert.Equal(LinkQuality.Borderline, LinkLimits.Rate(200));
		Assert.Equal(LinkQuality.Poor, LinkLimits.Rate(100));
		// Gaps per 10 s; a short measurement counts as 10 s, so it never flags sooner.
		Assert.Equal(LinkQuality.Good, LinkLimits.Gaps(2, 2, 10));
		Assert.Equal(LinkQuality.Borderline, LinkLimits.Gaps(3, 0, 10));
		Assert.Equal(LinkQuality.Poor, LinkLimits.Gaps(3, 3, 10));
		Assert.Equal(LinkQuality.Good, LinkLimits.Gaps(4, 0, 20));
		Assert.Equal(LinkQuality.Borderline, LinkLimits.Gaps(3, 0, 5));
		// Holding a state: left only below 80 % of its limit.
		Assert.Equal(LinkQuality.Poor, LinkLimits.Delay(25, LinkQuality.Poor));
		Assert.Equal(LinkQuality.Borderline, LinkLimits.Delay(23, LinkQuality.Poor));
		Assert.Equal(LinkQuality.Borderline, LinkLimits.Delay(13, LinkQuality.Borderline));
		Assert.Equal(LinkQuality.Good, LinkLimits.Delay(11, LinkQuality.Borderline));
	}

	[Fact]
	public void HealthyLink_IsGood_AfterAShortMeasuringPhase()
	{
		(LinkQualityMonitor m, _) = Started(out long at);
		// The first 2 s settle, then 5 samples are needed.
		Assert.Equal(LinkQuality.Measuring, Feed(m, ref at, Healthy, 5).State);
		LinkQualityReading r = Feed(m, ref at, Healthy, 2);
		Assert.Equal(LinkQuality.Good, r.State);
		Assert.Equal(4.5, r.WorstDelayMs, 3);
		Assert.Equal(600, r.RateHz, 3);
		Assert.False(r.WarningDue);
	}

	[Fact]
	public void SustainedDelay_IsBorderlineThenPoor()
	{
		(LinkQualityMonitor m, List<string> log) = Started(out long at);
		Feed(m, ref at, Healthy, 12);
		Assert.Equal(LinkQuality.Borderline, Feed(m, ref at, Worst(20), 6).State);
		Assert.Equal(LinkQuality.Poor, Feed(m, ref at, Worst(45), 6).State);
		Assert.Contains(log, l => l.Contains("Good -> Borderline", StringComparison.Ordinal));
		Assert.Contains(log, l => l.Contains("Borderline -> Poor", StringComparison.Ordinal));
	}

	[Fact]
	public void SingleSpikes_AndSingleGaps_AreIgnored()
	{
		(LinkQualityMonitor m, _) = Started(out long at);
		Feed(m, ref at, Healthy, 10);
		// The healthy link's own maxima (16-28 ms) and even a lone 200 ms second or a single long stall.
		Feed(m, ref at, Worst(28));
		Feed(m, ref at, Healthy, 2);
		Feed(m, ref at, Worst(200));
		Feed(m, ref at, Healthy with { LongGaps = 1, VeryLongGaps = 1 });
		Feed(m, ref at, Worst(60));
		LinkQualityReading r = Feed(m, ref at, Healthy, 3);
		Assert.Equal(LinkQuality.Good, r.State);
		Assert.False(r.WarningDue);
	}

	[Fact]
	public void RepeatedLongGaps_AndLostReports_AndALowRate_CountWithoutDelay()
	{
		(LinkQualityMonitor gaps, _) = Started(out long a1);
		Feed(gaps, ref a1, Healthy, 8);
		Assert.Equal(LinkQuality.Borderline, Feed(gaps, ref a1, Healthy with { LongGaps = 1 }, 3).State);

		(LinkQualityMonitor lost, _) = Started(out long a2);
		Feed(lost, ref a2, Healthy, 8);
		// 20 of 620 reports lost each second: 3.2 %.
		Assert.Equal(LinkQuality.Poor, Feed(lost, ref a2, Healthy with { Lost = 20 }, 10).State);

		(LinkQualityMonitor slow, _) = Started(out long a3);
		Assert.Equal(LinkQuality.Poor, Feed(slow, ref a3, Healthy with { Reports = 100 }, 10).State);
	}

	[Fact]
	public void Warning_OnlyAfterPoorLasted_AndOncePerEpisode()
	{
		(LinkQualityMonitor m, List<string> log) = Started(out long at);
		Feed(m, ref at, Healthy, 25);
		// Walking away: poor from here on.
		int seconds = 0;
		LinkQualityReading r;
		do
		{
			r = Feed(m, ref at, Worst(45));
			seconds++;
		}
		while (!r.WarningDue && seconds < 60);
		Assert.True(r.WarningDue);
		// Poor shows after ~6 s (half the window), then lasts 15 s.
		Assert.InRange(seconds, 20, 22);
		m.AcknowledgeWarning(at);
		Assert.False(m.Current!.WarningDue);
		Assert.Contains(log, l => l.StartsWith("link quality: warned", StringComparison.Ordinal));

		// Still poor, or poor again after only reaching Borderline: the same episode, no second warning.
		Assert.False(Feed(m, ref at, Worst(45), 30).WarningDue);
		Assert.Equal(LinkQuality.Borderline, Feed(m, ref at, Worst(18), 25).State);
		Assert.False(Feed(m, ref at, Worst(45), 40).WarningDue);
	}

	[Fact]
	public void Recovery_NeedsTimeAndMargin()
	{
		(LinkQualityMonitor m, _) = Started(out long at);
		Feed(m, ref at, Healthy, 8);
		Assert.Equal(LinkQuality.Poor, Feed(m, ref at, Worst(40), 10).State);
		// Just under the limit is not enough to leave Poor (80 % margin).
		Assert.Equal(LinkQuality.Poor, Feed(m, ref at, Worst(27), 30).State);
		// Clearly better: Borderline only after 10 s of it.
		Assert.Equal(LinkQuality.Poor, Feed(m, ref at, Worst(18), 9).State);
		Assert.Equal(LinkQuality.Borderline, Feed(m, ref at, Worst(18), 7).State);
		// Back to healthy: the median turns after ~5 s, then 10 s more.
		Assert.Equal(LinkQuality.Borderline, Feed(m, ref at, Healthy, 10).State);
		Assert.Equal(LinkQuality.Good, Feed(m, ref at, Healthy, 6).State);
	}

	[Fact]
	public void Cooldown_KeepsASecondEpisodeQuietForTenMinutes()
	{
		(LinkQualityMonitor m, _) = Started(out long at);
		Feed(m, ref at, Healthy, 10);
		Assert.True(Feed(m, ref at, Worst(45), 25).WarningDue);
		m.AcknowledgeWarning(at);
		Assert.Equal(LinkQuality.Good, Feed(m, ref at, Healthy, 30).State);
		// A new episode within ten minutes of the warning: no warning.
		Assert.False(Feed(m, ref at, Worst(45), 60).WarningDue);
		Assert.Equal(LinkQuality.Good, Feed(m, ref at, Healthy, 30).State);
		at += (long)LinkQualityMonitor.WarningCooldown.TotalMilliseconds;
		// After it: warned again (the long jump between samples restarts the measurement, like a resume).
		Assert.True(Feed(m, ref at, Worst(45), 30).WarningDue);
	}

	[Fact]
	public void NoWarning_InTheFirstSecondsAfterConnect_EvenOnATerribleLink()
	{
		(LinkQualityMonitor m, _) = Started(out long at);
		for (int s = 1; s < LinkQualityMonitor.WarningGrace.TotalSeconds; s++)
		{
			Assert.False(Feed(m, ref at, new LinkSecond(90, 100, 50, 5, 5, 1)).WarningDue);
		}
		// A reconnect starts the grace again.
		m.Restart(at, "connected");
		Assert.False(Feed(m, ref at, Worst(90), 19).WarningDue);
	}

	[Fact]
	public void PausedProgram_IsNotABadLink()
	{
		(LinkQualityMonitor m, List<string> log) = Started(out long at);
		Feed(m, ref at, Healthy, 15);
		// PC asleep / debugger: no sample for 40 s, then the reports that piled up arrive in a burst.
		at += 40_000;
		Assert.Equal(LinkQuality.Measuring, m.Add(new LinkSecond(400, 3000, 200, 1, 1, 41), at).State);
		Assert.Contains(log, l => l.Contains("no sample for 40.0 s", StringComparison.Ordinal));
		Assert.Equal(LinkQuality.Measuring, Feed(m, ref at, new LinkSecond(150, 1500, 80, 1, 1, 1)).State);
		// The burst was skipped; the link itself is fine.
		LinkQualityReading r = Feed(m, ref at, Healthy, 8);
		Assert.Equal(LinkQuality.Good, r.State);
		Assert.Equal(0, r.LostPercent);
	}

	[Fact]
	public void SecondsWithoutReports_AreSkipped()
	{
		(LinkQualityMonitor m, _) = Started(out long at);
		Feed(m, ref at, Healthy, 10);
		Assert.Equal(LinkQuality.Good, Feed(m, ref at, new LinkSecond(0, 0, 0, 0, 0, 1), 2).State);
	}

	[Fact]
	public void Stop_ClearsTheReading_ButKeepsTheEpisode()
	{
		(LinkQualityMonitor m, _) = Started(out long at);
		Feed(m, ref at, Healthy, 10);
		Assert.True(Feed(m, ref at, Worst(45), 25).WarningDue);
		m.AcknowledgeWarning(at);
		m.Stop();
		Assert.Null(m.Current);
		// Reconnects while walking away: no second warning for the same episode.
		m.Restart(at, "connected");
		Assert.False(Feed(m, ref at, Worst(45), 60).WarningDue);
	}

	[Fact]
	public void LatencyMeter_CountsGapsAndLostReportsSinceTheLastSample()
	{
		static long Ticks(double ms) => (long)(ms * Stopwatch.Frequency / 1000);
		LatencyMeter meter = new(500);
		double t = 0;
		for (int i = 0; i < 300; i++)
		{
			// A 60 ms and a 120 ms gap; sequence numbers 100 and 101 missing.
			t += i == 50 ? 60 : i == 150 ? 120 : 1.5;
			int seq = i < 100 ? i : i + 2;
			meter.Record(Ticks(t), (byte)seq, (uint)(t * 3000), Ticks(t + 0.1));
		}
		LinkCounts counts = meter.TakeCounts();
		Assert.Equal(300, counts.Reports);
		Assert.Equal(2, counts.Lost);
		Assert.Equal(2, counts.LongGaps);
		Assert.Equal(1, counts.VeryLongGaps);
		Assert.Equal(new LinkCounts(0, 0, 0, 0), meter.TakeCounts());
	}
}

// The adapter test explained: the deciding values with what is needed, advice when it is not suitable, and no verdict
// when it could not measure. Same limits as the live indicator.
public class AdapterExplanationTests
{
	private static LinkResult Link(double rate, double p99 = 4.8, int lost = 0, int reports = 3200, double seconds = 5, int gaps = 0, int veryLong = 0) =>
		new(reports, seconds, rate, new Spread(1.3, 2.5, 4.4, 20), new Spread(1, 2, p99, p99 + 10), lost, gaps, veryLong);

	[Fact]
	public void GoodAdapter_ListsTheValuesThatDecidedIt()
	{
		AdapterReport report = AdapterCheck.Grade(Link(640), Link(620, 4.6), 93.9, Language.Turkish);
		AdapterExplanation e = AdapterCheck.Explain(report, Language.Turkish);
		Assert.Equal(AdapterVerdict.Suitable, e.Verdict);
		Assert.Equal("Uygun: titreşim ve tetikler için yeterince hızlı.", e.Headline);
		Assert.Equal(["Saniyede 620 rapor", "En kötü gecikme 4,8 ms", "Kayıp rapor yok"], e.Lines.Select(l => l.Measured));
		Assert.Equal(["en az 250 gerekli", "15 ms'nin altında olmalı", "en çok %0,5"], e.Lines.Select(l => l.Needed));
		Assert.All(e.Lines, l => Assert.Equal("Tamam", l.Status));
		Assert.Empty(e.Advice);
		Assert.StartsWith("Uygun:", e.ToText());
		Assert.Contains("Saniyede 620 rapor (en az 250 gerekli): Tamam", e.ToText());
		AdapterExplanation en = AdapterCheck.Explain(report, Language.English);
		Assert.Equal("620 reports a second", en.Lines[0].Measured);
		Assert.Equal("OK", en.Lines[0].Status);
	}

	[Fact]
	public void BorderlineAndUnsuitable_SayWhichValueFailed_AndWhatToTry()
	{
		AdapterExplanation borderline = AdapterCheck.Explain(AdapterCheck.Grade(Link(640, 19), Link(620, 21), 94), Language.Turkish);
		Assert.Equal(AdapterVerdict.Borderline, borderline.Verdict);
		AdapterLine delay = borderline.Lines.Single(l => l.Measured.StartsWith("En kötü gecikme", StringComparison.Ordinal));
		Assert.Equal("En kötü gecikme 21,0 ms", delay.Measured);
		Assert.Equal("Sınırda", delay.Status);
		Assert.Equal(2, borderline.Advice.Count);
		Assert.Contains("USB 2.0", borderline.Advice[0]);
		Assert.Contains("Bluetooth 5", borderline.Advice[1]);

		AdapterExplanation unsuitable = AdapterCheck.Explain(AdapterCheck.Grade(Link(640), Link(600, lost: 100), 94), Language.Turkish);
		Assert.Equal(AdapterVerdict.Unsuitable, unsuitable.Verdict);
		AdapterLine lost = unsuitable.Lines.Single(l => l.Measured.StartsWith("Kayıp", StringComparison.Ordinal));
		Assert.Equal("Kayıp rapor %3,0", lost.Measured);
		Assert.Equal(LinkQuality.Poor, lost.Level);
		Assert.Equal("Kötü", lost.Status);

		// Gaps and haptics only show when they failed.
		AdapterExplanation gaps = AdapterCheck.Explain(AdapterCheck.Grade(Link(640, gaps: 2), Link(620, gaps: 2), 80), Language.Turkish);
		Assert.Contains(gaps.Lines, l => l.Measured == "4 kez 50 ms'den uzun kesinti" && l.Needed == "10 saniyede 3'ten az olmalı");
		Assert.Contains(gaps.Lines, l => l.Measured == "Titreşim akışı saniyede 80" && l.Status == "Kötü");
	}

	[Fact]
	public void SameLimitsAsTheLiveIndicator()
	{
		foreach (double p99 in new[] { 4.8, 14.9, 15, 29.9, 30, 60 })
		{
			AdapterVerdict verdict = AdapterCheck.Grade(Link(640, p99), Link(640, p99), 94).Verdict;
			AdapterVerdict expected = LinkLimits.Delay(p99) switch
			{
				LinkQuality.Poor => AdapterVerdict.Unsuitable,
				LinkQuality.Borderline => AdapterVerdict.Borderline,
				_ => AdapterVerdict.Suitable
			};
			Assert.Equal(expected, verdict);
		}
	}

	[Fact]
	public void ControllerWentQuiet_GivesNoVerdict()
	{
		// Before: 500 reports in the first second of a 5 s phase looked like a fine 500 reports a second, "Suitable".
		LinkResult idle = Link(600, reports: 500, seconds: 0.8);
		LinkResult load = Link(600);
		AdapterReport report = AdapterCheck.Grade(idle, load, 94) with { Coverage = AdapterCheck.Coverage(idle, TimeSpan.FromSeconds(4.5), load, TimeSpan.FromSeconds(5)) };
		Assert.Equal(AdapterVerdict.Suitable, report.Verdict);
		Assert.False(report.IsComplete);
		AdapterExplanation e = AdapterCheck.Explain(report, Language.Turkish);
		Assert.Null(e.Verdict);
		Assert.StartsWith("Ölçüm yarım kaldı", e.Headline);
		Assert.Empty(e.Lines);
		Assert.Single(e.Advice);

		// Too few reports, or none at all.
		Assert.False(AdapterCheck.Grade(Link(600, reports: 60), load, 94).IsComplete);
		AdapterExplanation none = AdapterCheck.Explain(null, Language.English);
		Assert.Null(none.Verdict);
		Assert.Equal("Could not measure: no data came from the controller.", none.Headline);
	}
}
