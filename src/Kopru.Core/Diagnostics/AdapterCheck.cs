// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Kopru.Core.Bluetooth;
using Kopru.Core.Localization;

namespace Kopru.Core.Diagnostics;

public enum AdapterVerdict
{
	Suitable,
	Borderline,
	Unsuitable
}

public sealed record AdapterReport(AdapterVerdict Verdict, IReadOnlyList<string> Reasons, LinkResult Idle, LinkResult Load, double HapticsRateHz)
{
	// The measured values that decided the verdict (see AdapterCheck.Explain for the words).
	public IReadOnlyList<AdapterFinding> Findings { get; init; } = [];

	// How much of each phase the reports covered (the smaller of the two; 1 = all of it).
	public double Coverage { get; init; } = 1;

	// False when the controller went quiet during the test or too few reports arrived: then there is no verdict.
	public bool IsComplete => Coverage >= AdapterCheck.MinCoverage && Idle.Reports >= AdapterCheck.MinReports && Load.Reports >= AdapterCheck.MinReports;

	// In the current language; reasons are in the language chosen when graded.
	public string VerdictText => VerdictTextIn(Localizer.Current);

	public string VerdictTextIn(Language language) => Verdict switch
	{
		AdapterVerdict.Suitable => Text.VerdictSuitable(language),
		AdapterVerdict.Borderline => Text.VerdictBorderline(language),
		_ => Text.VerdictUnsuitable(language)
	};
}

// What was measured, how it compares with its limit (Good / Borderline / Poor) and the values: Rate (reports a second),
// Delay (worst 1 %, ms), Lost (percent), Gaps (count, Value2 = seconds measured), Haptics (reports a second, Value2 =
// the target), SlowsUnderLoad (idle rate, Value2 = rate with haptics).
public enum AdapterFindingKind
{
	Rate,
	Delay,
	Lost,
	Gaps,
	Haptics,
	SlowsUnderLoad
}

public sealed record AdapterFinding(AdapterFindingKind Kind, LinkQuality Level, double Value, double Value2 = 0);

// One line of the explanation: what was measured, what is needed, and how it did.
public sealed record AdapterLine(string Measured, string Needed, LinkQuality Level, string Status);

// The test result in words: Verdict null when the test could not measure properly (Headline says why).
public sealed record AdapterExplanation(AdapterVerdict? Verdict, string Headline, IReadOnlyList<AdapterLine> Lines, IReadOnlyList<string> Advice)
{
	public string ToText() => string.Join(Environment.NewLine,
		new[] { Headline }.Concat(Lines.Select(l => $"{l.Measured} ({l.Needed}): {l.Status}")).Concat(Advice));
}

// "Is my Bluetooth adapter good enough?": measures the input link while idle, then while haptics stream (the most
// demanding case), and grades the result with the same limits as the live link indicator (LinkLimits). Cheap or old
// adapters fail by dropping reports, slowing down under load or adding long stalls.
public static class AdapterCheck
{
	// Haptics need 93.75 reports a second.
	public const double HapticsTargetHz = 1 / 0.0106667;

	// Reports in the first moment after the controller is opened are connection setup, not the link: skipped.
	public static readonly TimeSpan WarmUp = TimeSpan.FromMilliseconds(500);

	// Below this share of a phase covered by reports, or this many reports in a phase, the test did not measure properly
	// (the controller fell asleep, was turned off or left range): no verdict then.
	public const double MinCoverage = 0.8;

	public const int MinReports = 100;

	public static async Task<AdapterReport?> RunAsync(BluetoothSession session, TimeSpan idle, TimeSpan load, CancellationToken cancel = default)
	{
		List<LinkSample> samples = [];
		void OnInput(Protocol.InputState s)
		{
			lock (samples)
			{
				samples.Add(new LinkSample(Stopwatch.GetTimestamp(), s.Sequence, s.SensorTicks));
			}
		}
		session.InputReceived += OnInput;
		try
		{
			long warmUpEnd = Stopwatch.GetTimestamp() + (long)(WarmUp.TotalSeconds * Stopwatch.Frequency);
			await Task.Delay(idle, cancel);
			List<LinkSample> idleSamples = Drain(samples).Where(s => s.ArrivalTicks >= warmUpEnd).ToList();

			long hapticsBefore = session.HapticsReports;
			Stopwatch clock = Stopwatch.StartNew();
			using CancellationTokenSource feeding = CancellationTokenSource.CreateLinkedTokenSource(cancel);
			Task feeder = FeedQuietToneAsync(session, feeding.Token);
			await Task.Delay(load, cancel);
			List<LinkSample> loadSamples = Drain(samples);
			double hapticsRate = (session.HapticsReports - hapticsBefore) / clock.Elapsed.TotalSeconds;
			feeding.Cancel();
			await feeder.ContinueWith(_ => { }, TaskScheduler.Default);

			LinkResult? idleResult = LinkAnalyzer.Analyze(idleSamples);
			LinkResult? loadResult = LinkAnalyzer.Analyze(loadSamples);
			if (idleResult == null || loadResult == null)
			{
				return null;
			}
			return Grade(idleResult, loadResult, hapticsRate) with { Coverage = Coverage(idleResult, idle - WarmUp, loadResult, load) };
		}
		finally
		{
			session.InputReceived -= OnInput;
		}
	}

	// The share of each phase the reports span (the smaller one, at most 1): reports that stop early mean the controller
	// went quiet, not that the link is fast.
	public static double Coverage(LinkResult idle, TimeSpan idleLength, LinkResult load, TimeSpan loadLength) =>
		Math.Min(1, Math.Min(idle.Seconds / Math.Max(0.001, idleLength.TotalSeconds), load.Seconds / Math.Max(0.001, loadLength.TotalSeconds)));

	public static AdapterReport Grade(LinkResult idle, LinkResult load, double hapticsRateHz, Language? language = null)
	{
		Language l = language ?? Localizer.Current;
		List<AdapterFinding> findings = [];
		List<string> reasons = [];

		double rate = Math.Min(idle.RateHz, load.RateHz);
		LinkQuality rateLevel = LinkLimits.Rate(rate);
		findings.Add(new AdapterFinding(AdapterFindingKind.Rate, rateLevel, rate));
		if (rateLevel != LinkQuality.Good)
		{
			reasons.Add(rateLevel == LinkQuality.Poor ? Text.AdapterRateTooLow(rate, l) : Text.AdapterRateLow(rate, l));
		}

		double stall = Math.Max(idle.ExtraDelayMs.P99, load.ExtraDelayMs.P99);
		LinkQuality delayLevel = LinkLimits.Delay(stall);
		findings.Add(new AdapterFinding(AdapterFindingKind.Delay, delayLevel, stall));
		if (delayLevel != LinkQuality.Good)
		{
			reasons.Add(delayLevel == LinkQuality.Poor ? Text.AdapterFrequentStalls(stall, l) : Text.AdapterOccasionalStalls(stall, l));
		}

		double lost = Math.Max(idle.LostShare, load.LostShare) * 100;
		LinkQuality lostLevel = LinkLimits.Lost(lost);
		findings.Add(new AdapterFinding(AdapterFindingKind.Lost, lostLevel, lost));
		if (lostLevel != LinkQuality.Good)
		{
			reasons.Add(Text.AdapterReportsLost(lost, l));
		}

		int gaps = idle.LongGaps + load.LongGaps;
		double seconds = idle.Seconds + load.Seconds;
		LinkQuality gapLevel = LinkLimits.Gaps(gaps, idle.VeryLongGaps + load.VeryLongGaps, seconds);
		if (gapLevel != LinkQuality.Good)
		{
			findings.Add(new AdapterFinding(AdapterFindingKind.Gaps, gapLevel, gaps, seconds));
			reasons.Add(Text.AdapterLongGaps(gaps, l));
		}

		LinkQuality hapticsLevel = hapticsRateHz < HapticsTargetHz * 0.9 ? LinkQuality.Poor
			: hapticsRateHz < HapticsTargetHz * 0.97 ? LinkQuality.Borderline : LinkQuality.Good;
		if (hapticsLevel != LinkQuality.Good)
		{
			findings.Add(new AdapterFinding(AdapterFindingKind.Haptics, hapticsLevel, hapticsRateHz, HapticsTargetHz));
			reasons.Add(hapticsLevel == LinkQuality.Poor ? Text.AdapterHapticsBehind(hapticsRateHz, HapticsTargetHz, l) : Text.AdapterHapticsStruggling(hapticsRateHz, l));
		}

		if (load.RateHz < idle.RateHz * SlowdownKeepShare)
		{
			findings.Add(new AdapterFinding(AdapterFindingKind.SlowsUnderLoad, LinkQuality.Borderline, idle.RateHz, load.RateHz));
			reasons.Add(Text.AdapterSlowsUnderLoad(idle.RateHz, load.RateHz, l));
		}

		LinkQuality worst = findings.Select(f => f.Level).Aggregate(LinkQuality.Good, LinkLimits.Max);
		AdapterVerdict verdict = worst switch
		{
			LinkQuality.Poor => AdapterVerdict.Unsuitable,
			LinkQuality.Borderline => AdapterVerdict.Borderline,
			_ => AdapterVerdict.Suitable
		};
		if (reasons.Count == 0)
		{
			reasons.Add(Text.AdapterAllGood(rate, l));
		}
		return new AdapterReport(verdict, reasons, idle, load, hapticsRateHz) { Findings = findings };
	}

	// With haptics the input rate should keep at least this share of the idle rate.
	public const double SlowdownKeepShare = 0.6;

	// The result in words, in the given language: the verdict as one sentence, the deciding values (rate, delay and lost
	// reports always, the rest when they failed) with what is needed, and what to try when it is not suitable. Without a
	// report, or with an incomplete one, it says the test could not measure instead of giving a verdict.
	public static AdapterExplanation Explain(AdapterReport? report, Language? language = null)
	{
		Language l = language ?? Localizer.Current;
		if (report == null)
		{
			return new AdapterExplanation(null, Text.AdapterHeadlineNoData(l), [], [Text.AdapterAdviceRetry(l)]);
		}
		if (!report.IsComplete)
		{
			return new AdapterExplanation(null, Text.AdapterHeadlineIncomplete(l), [], [Text.AdapterAdviceRetry(l)]);
		}
		string headline = report.Verdict switch
		{
			AdapterVerdict.Suitable => Text.AdapterHeadlineSuitable(l),
			AdapterVerdict.Borderline => Text.AdapterHeadlineBorderline(l),
			_ => Text.AdapterHeadlineUnsuitable(l)
		};
		List<AdapterLine> lines = report.Findings.Select(f => Line(f, l)).ToList();
		IReadOnlyList<string> advice = report.Verdict == AdapterVerdict.Suitable ? [] : [Text.AdapterAdvicePlacement(l), Text.AdapterAdviceDistance(l)];
		return new AdapterExplanation(report.Verdict, headline, lines, advice);
	}

	public static string Status(LinkQuality level, Language? language = null) => level switch
	{
		LinkQuality.Poor => Text.AdapterStatusBad(language),
		LinkQuality.Borderline => Text.AdapterStatusBorderline(language),
		_ => Text.AdapterStatusOk(language)
	};

	private static AdapterLine Line(AdapterFinding f, Language l)
	{
		(string measured, string needed) = f.Kind switch
		{
			AdapterFindingKind.Rate => (Text.AdapterMeasuredRate(f.Value, l), Text.AdapterNeedRate(LinkLimits.BorderlineRateHz, l)),
			AdapterFindingKind.Delay => (Text.AdapterMeasuredDelay(f.Value, l), Text.AdapterNeedDelay(LinkLimits.BorderlineDelayMs, l)),
			AdapterFindingKind.Lost => (f.Value <= 0 ? Text.AdapterMeasuredNoLoss(l) : Text.AdapterMeasuredLoss(f.Value, l),
				Text.AdapterNeedLoss(LinkLimits.BorderlineLostPercent, l)),
			AdapterFindingKind.Gaps => (Text.AdapterMeasuredGaps((int)f.Value, LinkLimits.LongGapMs, l),
				Text.AdapterNeedGaps(LinkLimits.GapsPerWindow, LinkLimits.GapWindowSeconds, l)),
			AdapterFindingKind.Haptics => (Text.AdapterMeasuredHaptics(f.Value, l), Text.AdapterNeedHaptics(f.Value2 * 0.97, l)),
			_ => (Text.AdapterMeasuredSlowdown(f.Value, f.Value2, l), Text.AdapterNeedSlowdown(SlowdownKeepShare * 100, l))
		};
		return new AdapterLine(measured, needed, f.Level, Status(f.Level, l));
	}

	private static List<LinkSample> Drain(List<LinkSample> samples)
	{
		lock (samples)
		{
			List<LinkSample> taken = [.. samples];
			samples.Clear();
			return taken;
		}
	}

	// A faint 80 Hz tone, so the haptics stream runs at full rate (silence would stop it) without a strong buzz.
	private static async Task FeedQuietToneAsync(BluetoothSession session, CancellationToken cancel)
	{
		double phase = 0;
		while (!cancel.IsCancellationRequested)
		{
			while (session.Haptics.Buffered < Haptics.HapticsMixer.MaxFrames / 2)
			{
				sbyte v = (sbyte)Math.Round(12 * Math.Sin(phase));
				session.Haptics.Push(v, v);
				phase += 2 * Math.PI * 80 / Protocol.BluetoothReports.HapticsSampleRate;
			}
			session.WakeHaptics();
			try
			{
				await Task.Delay(10, cancel);
			}
			catch (TaskCanceledException)
			{
				return;
			}
		}
	}
}
