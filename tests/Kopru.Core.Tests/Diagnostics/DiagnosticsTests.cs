// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Kopru.Core.Diagnostics;
using Kopru.Core.Localization;

namespace Kopru.Core.Tests;

public class LinkAnalyzerTests
{
	private static List<LinkSample> Stream(int n, double periodMs = 1.25, Dictionary<int, double>? delays = null, HashSet<int>? drop = null,
		double driftPpm = 0, uint sensorStart = 0)
	{
		List<LinkSample> samples = [];
		byte seq = 0;
		for (int i = 0; i < n; i++)
		{
			double deviceMs = i * periodMs;
			if (drop?.Contains(i) != true)
			{
				double hostMs = deviceMs * (1 + driftPpm / 1e6) + 5 + (delays?.GetValueOrDefault(i) ?? 0);
				samples.Add(new LinkSample((long)(hostMs * Stopwatch.Frequency / 1000), seq, unchecked(sensorStart + (uint)(deviceMs * 3000))));
			}
			seq++;
		}
		return samples;
	}

	[Fact]
	public void PerfectStream()
	{
		LinkResult r = LinkAnalyzer.Analyze(Stream(2000))!;
		Assert.InRange(r.RateHz, 799, 801);
		Assert.Equal(0, r.Lost);
		Assert.True(r.ExtraDelayMs.Max < 0.01);
	}

	[Fact]
	public void LostReportsCounted() => Assert.Equal(3, LinkAnalyzer.Analyze(Stream(500, drop: [10, 11, 200]))!.Lost);

	[Fact]
	public void SingleStallMeasured()
	{
		LinkResult r = LinkAnalyzer.Analyze(Stream(2000, delays: new() { [1000] = 6 }))!;
		Assert.Equal(6, r.ExtraDelayMs.Max, 1);
		Assert.True(r.ExtraDelayMs.P99 < 0.1);
	}

	[Fact]
	public void ClockDriftRemoved() => Assert.True(LinkAnalyzer.Analyze(Stream(8000, driftPpm: 80))!.ExtraDelayMs.Max < 0.05);

	[Fact]
	public void SensorClockWrapHandled() => Assert.InRange(LinkAnalyzer.Analyze(Stream(500, sensorStart: 0xFFFF0000))!.RateHz, 799, 801);

	[Fact]
	public void TooFew() => Assert.Null(LinkAnalyzer.Analyze(Stream(2)));
}

public class AdapterGradeTests
{
	internal static LinkResult Link(double rate, double p99 = 2, int lost = 0, int reports = 5000) =>
		new(reports, 5, rate, new Spread(1, 2, 3, 5), new Spread(0.5, 1, p99, p99 + 5), lost);

	[Fact]
	public void TheMeasuredCsrAdapter_IsSuitable()
	{
		// 2026-11-09, CSR clone dongle: 731 Hz idle, 639 Hz with haptics, p99 7.3 ms, nothing lost, haptics 93.9/s.
		AdapterReport report = AdapterCheck.Grade(Link(731, 7.3), Link(639, 7.2), 93.9, Language.Turkish);
		Assert.Equal(AdapterVerdict.Suitable, report.Verdict);
		Assert.Equal("Uygun", report.VerdictTextIn(Language.Turkish));
	}

	[Fact]
	public void SlowLink_IsBorderline() => Assert.Equal(AdapterVerdict.Borderline, AdapterCheck.Grade(Link(200), Link(190), 94).Verdict);

	[Fact]
	public void FrequentStalls_AreUnsuitable() => Assert.Equal(AdapterVerdict.Unsuitable, AdapterCheck.Grade(Link(700, 40), Link(650, 45), 94).Verdict);

	[Fact]
	public void LosingReports_IsUnsuitable() => Assert.Equal(AdapterVerdict.Unsuitable, AdapterCheck.Grade(Link(700), Link(650, lost: 200), 94).Verdict);

	[Fact]
	public void HapticsFallingBehind_IsUnsuitable()
	{
		AdapterReport report = AdapterCheck.Grade(Link(700), Link(650), 70, Language.Turkish);
		Assert.Equal(AdapterVerdict.Unsuitable, report.Verdict);
		Assert.Contains(report.Reasons, r => r.Contains("Haptik"));
	}

	[Fact]
	public void SlowingDownUnderLoad_IsBorderline() => Assert.Equal(AdapterVerdict.Borderline, AdapterCheck.Grade(Link(800), Link(300), 94).Verdict);
}
