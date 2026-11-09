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
	// In the current language; reasons are in the language chosen when graded.
	public string VerdictText => VerdictTextIn(Localizer.Current);

	public string VerdictTextIn(Language language) => Verdict switch
	{
		AdapterVerdict.Suitable => Text.VerdictSuitable(language),
		AdapterVerdict.Borderline => Text.VerdictBorderline(language),
		_ => Text.VerdictUnsuitable(language)
	};
}

// "Is my Bluetooth adapter good enough?": measures the input link while idle, then while haptics stream (the most
// demanding case), and grades the result. Cheap or old adapters fail by dropping reports, slowing down under load or
// adding long stalls.
public static class AdapterCheck
{
	// Haptics need 93.75 reports a second.
	public const double HapticsTargetHz = 1 / 0.0106667;

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
			await Task.Delay(idle, cancel);
			List<LinkSample> idleSamples = Drain(samples);

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
			return idleResult == null || loadResult == null ? null : Grade(idleResult, loadResult, hapticsRate);
		}
		finally
		{
			session.InputReceived -= OnInput;
		}
	}

	public static AdapterReport Grade(LinkResult idle, LinkResult load, double hapticsRateHz, Language? language = null)
	{
		Language l = language ?? Localizer.Current;
		List<string> reasons = [];
		AdapterVerdict verdict = AdapterVerdict.Suitable;
		void Mark(AdapterVerdict level, string reason)
		{
			reasons.Add(reason);
			if (level > verdict)
			{
				verdict = level;
			}
		}

		double rate = Math.Min(idle.RateHz, load.RateHz);
		if (rate < 125)
		{
			Mark(AdapterVerdict.Unsuitable, Text.AdapterRateTooLow(rate, l));
		}
		else if (rate < 250)
		{
			Mark(AdapterVerdict.Borderline, Text.AdapterRateLow(rate, l));
		}

		double lost = Math.Max(idle.LostShare, load.LostShare) * 100;
		if (lost > 2)
		{
			Mark(AdapterVerdict.Unsuitable, Text.AdapterReportsLost(lost, l));
		}
		else if (lost > 0.5)
		{
			Mark(AdapterVerdict.Borderline, Text.AdapterReportsLost(lost, l));
		}

		double stall = Math.Max(idle.ExtraDelayMs.P99, load.ExtraDelayMs.P99);
		if (stall > 30)
		{
			Mark(AdapterVerdict.Unsuitable, Text.AdapterFrequentStalls(stall, l));
		}
		else if (stall > 15)
		{
			Mark(AdapterVerdict.Borderline, Text.AdapterOccasionalStalls(stall, l));
		}

		if (hapticsRateHz < HapticsTargetHz * 0.9)
		{
			Mark(AdapterVerdict.Unsuitable, Text.AdapterHapticsBehind(hapticsRateHz, HapticsTargetHz, l));
		}
		else if (hapticsRateHz < HapticsTargetHz * 0.97)
		{
			Mark(AdapterVerdict.Borderline, Text.AdapterHapticsStruggling(hapticsRateHz, l));
		}

		if (load.RateHz < idle.RateHz * 0.6)
		{
			Mark(AdapterVerdict.Borderline, Text.AdapterSlowsUnderLoad(idle.RateHz, load.RateHz, l));
		}

		if (reasons.Count == 0)
		{
			reasons.Add(Text.AdapterAllGood(rate, l));
		}
		return new AdapterReport(verdict, reasons, idle, load, hapticsRateHz);
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
