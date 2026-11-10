// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Kopru.Probe;

// The loopback test signal and its analysis (speakermic, looptest): a 1 kHz tone on for 1 s, off for 1 s, repeated; the
// controller's own microphone is the ear. The level of the tone in the recording is measured with Goertzel filters on
// 50 ms Hann-windowed blocks, in a tone band and in a control band, per second of the schedule.
internal static class ToneTest
{
	public const double ToneHz = 1000;

	// -6 dBFS: clearly audible, well clear of clipping in the Opus encoder and resampler.
	public const double Amplitude = 0.5;

	// The tone counts as heard when, at the best latency, it is this much louder in the on seconds than in the off ones,
	// and this much louder than the control band in the on seconds.
	public const double HeardThresholdDb = 10;

	// A recording quieter than this overall (or shorter than half the run) is "mic silent".
	public const double SilentDb = -80;

	// The speaker path resamples 48 -> 45 kHz and the controller plays at 48 kHz (about +6.7 % pitch), so the band covers
	// 1000..1070 Hz; the control band is the same width 500 Hz higher (no harmonic of the tone falls in it).
	private static readonly double[] ToneBand = Enumerable.Range(0, 8).Select(i => 1000.0 + 10 * i).ToArray();
	private static readonly double[] ControlBand = Enumerable.Range(0, 8).Select(i => 1500.0 + 10 * i).ToArray();

	private const double BlockSeconds = 0.05;
	private const double RampSeconds = 0.01;

	public static bool IsOn(double t, int seconds) => t >= 0 && t < seconds && (int)Math.Floor(t) % 2 == 0;

	// The schedule's sample at t seconds (10 ms raised-cosine ramps, so switching does not click into the control band).
	public static float Sample(double t, int seconds)
	{
		if (!IsOn(t, seconds))
		{
			return 0f;
		}
		double inSecond = t - Math.Floor(t);
		double edge = Math.Min(inSecond, 1 - inSecond);
		double gain = edge >= RampSeconds ? 1 : 0.5 - 0.5 * Math.Cos(Math.PI * edge / RampSeconds);
		return (float)(Amplitude * gain * Math.Sin(2 * Math.PI * ToneHz * t));
	}

	public sealed record Second(int Index, bool On, double ToneDb, double ControlDb, int Blocks);

	public sealed record Result(IReadOnlyList<Second> Seconds, double LatencySeconds, double OnToneDb, double OffToneDb, double OnControlDb,
		double OverallDb, double CoveredSeconds)
	{
		public double ContrastDb => OnToneDb - OffToneDb;

		public double AboveControlDb => OnToneDb - OnControlDb;
	}

	// mono: the recording; startOffset: schedule time (s) of mono[0]. The latency (0..1 s, 50 ms steps) that gives the
	// largest on/off contrast is taken; each second is measured over [k + latency + 0.1, k + latency + 0.9].
	public static Result Analyze(float[] mono, int rate, double startOffset, int seconds)
	{
		int block = Math.Max(1, (int)(rate * BlockSeconds));
		int blocks = mono.Length / block;
		double[] window = Enumerable.Range(0, block).Select(n => 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / (block - 1))).ToArray();
		double[] tone = new double[blocks], control = new double[blocks];
		double[] buffer = new double[block];
		for (int b = 0; b < blocks; b++)
		{
			for (int n = 0; n < block; n++)
			{
				buffer[n] = mono[b * block + n] * window[n];
			}
			tone[b] = ToneBand.Max(f => Power(buffer, f, rate));
			control[b] = ControlBand.Max(f => Power(buffer, f, rate));
		}
		double sum = 0;
		foreach (float v in mono)
		{
			sum += (double)v * v;
		}
		double overall = mono.Length > 0 ? Db(Math.Sqrt(sum / mono.Length)) : double.NegativeInfinity;

		Result? best = null;
		for (int step = 0; step <= 20; step++)
		{
			double latency = step * 0.05;
			List<Second> list = [];
			for (int k = 0; k < seconds; k++)
			{
				double from = k + latency + 0.1, to = k + latency + 0.9;
				double t = 0, c = 0;
				int count = 0;
				for (int b = 0; b < blocks; b++)
				{
					double start = startOffset + b * BlockSeconds;
					if (start >= from && start + BlockSeconds <= to)
					{
						t += tone[b];
						c += control[b];
						count++;
					}
				}
				list.Add(count == 0
					? new Second(k, IsOn(k + 0.5, seconds), double.NaN, double.NaN, 0)
					: new Second(k, IsOn(k + 0.5, seconds), PowerDb(t / count), PowerDb(c / count), count));
			}
			double on = Mean(list.Where(s => s.On && s.Blocks >= 4).Select(s => s.ToneDb));
			double off = Mean(list.Where(s => !s.On && s.Blocks >= 4).Select(s => s.ToneDb));
			double onControl = Mean(list.Where(s => s.On && s.Blocks >= 4).Select(s => s.ControlDb));
			Result r = new(list, latency, on, off, onControl, overall, mono.Length / (double)rate);
			if (best == null || (!double.IsNaN(r.ContrastDb) && (double.IsNaN(best.ContrastDb) || r.ContrastDb > best.ContrastDb)))
			{
				best = r;
			}
		}
		return best!;
	}

	// One line per second, the summary and the verdict; returns the verdict's exit code (0 heard, 4 not heard, 5 inconclusive).
	public static int Report(Result r, int seconds, string silentReason = "")
	{
		foreach (Second s in r.Seconds)
		{
			Console.WriteLine(F($"  s{s.Index} {(s.On ? "on " : "off")}: tone {Show(s.ToneDb)} dBFS, control {Show(s.ControlDb)} dBFS ({s.Blocks} blocks)"));
		}
		Console.WriteLine(F($"Best latency {r.LatencySeconds * 1000:F0} ms; tone on {Show(r.OnToneDb)} / off {Show(r.OffToneDb)} dBFS: on - off {Show(r.ContrastDb)} dB; ") +
			F($"on: tone - control {Show(r.AboveControlDb)} dB; recording {r.CoveredSeconds:F1} s, overall RMS {Show(r.OverallDb)} dBFS"));
		Console.WriteLine(F($"Thresholds: heard when on - off >= {HeardThresholdDb:F0} dB and tone - control (on) >= {HeardThresholdDb:F0} dB; ") +
			F($"mic silent when overall RMS < {SilentDb:F0} dBFS or under {seconds / 2.0:F1} s recorded"));
		if (silentReason.Length > 0 || !(r.OverallDb >= SilentDb) || r.CoveredSeconds < seconds / 2.0)
		{
			Console.WriteLine($"Verdict: inconclusive (mic silent{(silentReason.Length > 0 ? ": " + silentReason : "")})");
			return 5;
		}
		bool heard = r.ContrastDb >= HeardThresholdDb && r.AboveControlDb >= HeardThresholdDb;
		Console.WriteLine(heard ? "Verdict: speaker heard by the mic" : "Verdict: not heard");
		return heard ? 0 : 4;
	}

	// |X|^2 of one Goertzel bin, scaled so a full-scale sine (amplitude 1) under the Hann window reads 1.0.
	private static double Power(double[] x, double hz, int rate)
	{
		double coeff = 2 * Math.Cos(2 * Math.PI * hz / rate);
		double s1 = 0, s2 = 0;
		foreach (double v in x)
		{
			double s = v + coeff * s1 - s2;
			s2 = s1;
			s1 = s;
		}
		double magnitude2 = s1 * s1 + s2 * s2 - coeff * s1 * s2;
		double amplitude = 4 * Math.Sqrt(Math.Max(0, magnitude2)) / x.Length;
		return amplitude * amplitude;
	}

	private static double PowerDb(double power) => power > 0 ? 10 * Math.Log10(power) : double.NegativeInfinity;

	private static double Db(double level) => level > 0 ? 20 * Math.Log10(level) : double.NegativeInfinity;

	private static double Mean(IEnumerable<double> values)
	{
		double[] v = values.Where(d => !double.IsNaN(d)).Select(d => Math.Max(d, -200)).ToArray();
		return v.Length == 0 ? double.NaN : v.Average();
	}

	private static string Show(double db) => double.IsNaN(db) ? "n/a" : double.IsNegativeInfinity(db) ? "-inf" : db.ToString("F1", CultureInfo.InvariantCulture);

	private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
