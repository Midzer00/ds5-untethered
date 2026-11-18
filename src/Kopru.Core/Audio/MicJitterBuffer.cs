// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Audio;

// Mono 48 kHz sample FIFO between the controller's mic packets (10 ms each, bursty over Bluetooth) and the host's 1 ms
// isochronous reads (the PC's clock). Thread-safe (one writer, one reader); nothing allocated per call.
//  - Adaptive depth: reads give silence until Target samples are buffered (again after every underrun; what arrived
//    beyond Target meanwhile is stale and skipped, so a stall does not add its length to the latency). Target starts at
//    MinTarget (40 ms), grows by TargetStep on every underrun up to MaxTarget, and comes back down by ShrinkStep after
//    ShrinkAfter samples read without one, so it stays as low as the link allows.
//  - Underrun concealment: when the buffer runs dry mid-read, the last pitch period heard is repeated with a fade to
//    silence over ConcealSamples (instead of a hard cut to zero), and the sound after it fades back in (FadeInSamples).
//  - Clock drift: the controller's and the PC's clocks differ slightly, so the fill level creeps. Beyond TrimDeadband
//    from Target (smoothed over about a second) the read rate is trimmed by up to MaxTrim with linear interpolation
//    (0.5 %, inaudible on speech); far above Target (CatchUpExcess) the excess is skipped at once.
//  - Beyond Capacity the oldest samples are dropped.
public sealed class MicJitterBuffer
{
	public const int SamplesPerMs = 48;
	public const int DefaultMinTarget = SamplesPerMs * 40;
	public const int DefaultMaxTarget = SamplesPerMs * 120;
	public const int DefaultCapacity = SamplesPerMs * 250;
	public const int TargetStep = SamplesPerMs * 10;
	public const int ShrinkStep = SamplesPerMs * 5;
	public const int ShrinkAfter = SamplesPerMs * 1000 * 30;
	public const int TrimDeadband = SamplesPerMs * 10;
	// Beyond the dead band, this much more than Target trims the rate by the full MaxTrim.
	public const int TrimFullScale = SamplesPerMs * 20;
	public const double MaxTrim = 0.005;
	public const int CatchUpExcess = SamplesPerMs * 80;
	// A catch-up skip crossfades from the skipped audio into the kept audio over this many samples (no click).
	public const int CrossfadeSamples = SamplesPerMs * 5;
	public const int FadeInSamples = SamplesPerMs * 2;
	public const int ConcealSamples = SamplesPerMs * 10;
	// The fill level is smoothed over about this many samples read (1 s).
	public const int LevelWindow = SamplesPerMs * 1000;

	// Pitch search for the concealment: periods of 2..10 ms, matched over the last 5 ms heard.
	private const int MinLag = SamplesPerMs * 2;
	private const int MaxLag = SamplesPerMs * 10;
	private const int MatchWindow = SamplesPerMs * 5;
	private const int HistorySize = 1024;

	private readonly short[] ring;

	private readonly object gate = new();

	// What was read last (before the caller's gain), for the concealment; history[historyAt] is the oldest.
	private readonly short[] history = new short[HistorySize];

	private readonly short[] pattern = new short[MaxLag];

	private int historyAt;

	private int head, count;

	private bool primed;

	// Read position between ring[head] and ring[head + 1] (0..1).
	private double frac;

	private double level;

	private int target;

	private long cleanRead;

	private int fadeIn;

	// The audio a catch-up skipped, played out under the kept audio while crossfading (xfadeAt: samples done).
	private readonly short[] xfadeFrom = new short[CrossfadeSamples];
	private int xfadeAt = CrossfadeSamples;

	// Concealment in progress: the repeated period, its length, and samples played of it.
	private int patternLength, concealAt = ConcealSamples;

	private long underruns, dropped, skipped, concealed;

	private int minLevel = int.MaxValue, maxLevel;

	public MicJitterBuffer(int minTarget = DefaultMinTarget, int capacity = DefaultCapacity, int maxTarget = DefaultMaxTarget)
	{
		ring = new short[Math.Max(2, capacity)];
		MinTarget = Math.Clamp(minTarget, 0, ring.Length);
		MaxTarget = Math.Clamp(maxTarget, MinTarget, ring.Length);
		target = MinTarget;
	}

	public int MinTarget { get; }

	public int MaxTarget { get; }

	public int Capacity => ring.Length;

	// The depth reads wait for before playing (MinTarget..MaxTarget).
	public int Target
	{
		get
		{
			lock (gate)
			{
				return target;
			}
		}
	}

	public long Underruns => Interlocked.Read(ref underruns);

	// Samples thrown away: past Capacity, or skipped to catch up (Skipped).
	public long Dropped => Interlocked.Read(ref dropped);

	public long Skipped => Interlocked.Read(ref skipped);

	// Samples played from the concealment after an underrun.
	public long Concealed => Interlocked.Read(ref concealed);

	// The current rate trim (read rate - 1; positive: reading faster to bring the level down).
	public double Trim
	{
		get
		{
			lock (gate)
			{
				return TrimFor(level);
			}
		}
	}

	public int Buffered
	{
		get
		{
			lock (gate)
			{
				return count;
			}
		}
	}

	// The lowest and highest fill seen before a read since the last call (null: nothing read while playing).
	public (int Min, int Max)? TakeLevels()
	{
		lock (gate)
		{
			(int Min, int Max)? levels = minLevel <= maxLevel ? (minLevel, maxLevel) : null;
			minLevel = int.MaxValue;
			maxLevel = 0;
			return levels;
		}
	}

	public void Write(ReadOnlySpan<short> samples)
	{
		lock (gate)
		{
			foreach (short s in samples)
			{
				if (count == ring.Length)
				{
					head = (head + 1) % ring.Length;
					count--;
					dropped++;
				}
				ring[(head + count) % ring.Length] = s;
				count++;
			}
		}
	}

	// Fills output: buffered audio, else the concealment, else silence. Returns the samples taken from real audio.
	public int Read(Span<short> output)
	{
		lock (gate)
		{
			if (!primed)
			{
				if (count == 0 || count < target)
				{
					Conceal(output);
					return 0;
				}
				primed = true;
				if (count > target)
				{
					Skip(count - target);
				}
				fadeIn = FadeInSamples;
				concealAt = ConcealSamples;
				level = count;
				frac = 0;
			}
			minLevel = Math.Min(minLevel, count);
			maxLevel = Math.Max(maxLevel, count);
			level += (count - level) * Math.Min(1, output.Length / (double)LevelWindow);
			if (count > target + CatchUpExcess)
			{
				for (int i = 0; i < CrossfadeSamples; i++)
				{
					xfadeFrom[i] = ring[(head + i) % ring.Length];
				}
				xfadeAt = 0;
				Skip(count - target);
				level = count;
				frac = 0;
			}
			double step = 1 + TrimFor(level);
			int n = 0;
			while (n < output.Length && count > 0)
			{
				short s = frac == 0 || count < 2
					? ring[head]
					: (short)Math.Round(ring[head] + (ring[(head + 1) % ring.Length] - ring[head]) * frac);
				if (fadeIn > 0)
				{
					s = (short)(s * (FadeInSamples - fadeIn) / FadeInSamples);
					fadeIn--;
				}
				else if (xfadeAt < CrossfadeSamples)
				{
					s = (short)((xfadeFrom[xfadeAt] * (CrossfadeSamples - xfadeAt) + s * xfadeAt) / CrossfadeSamples);
					xfadeAt++;
				}
				output[n++] = s;
				Remember(s);
				frac += step;
				while (frac >= 1 && count > 0)
				{
					head = (head + 1) % ring.Length;
					count--;
					frac -= 1;
				}
			}
			if (n < output.Length)
			{
				underruns++;
				primed = false;
				frac = 0;
				target = Math.Min(target + TargetStep, MaxTarget);
				cleanRead = 0;
				StartConcealment();
				Conceal(output[n..]);
			}
			else if ((cleanRead += output.Length) >= ShrinkAfter)
			{
				cleanRead = 0;
				target = Math.Max(MinTarget, target - ShrinkStep);
			}
			return n;
		}
	}

	// Empties the buffer without counting an underrun (the mic switched off and on again): the next read waits for the
	// target again, with no concealment of what played before.
	public void Clear()
	{
		lock (gate)
		{
			head = count = 0;
			primed = false;
			frac = 0;
			concealAt = ConcealSamples;
			xfadeAt = CrossfadeSamples;
			fadeIn = 0;
		}
	}

	// Caller holds gate. Drops the oldest `samples`.
	private void Skip(int samples)
	{
		head = (head + samples) % ring.Length;
		count -= samples;
		dropped += samples;
		skipped += samples;
	}

	// Caller holds gate. The rate trim for a smoothed level: none within the dead band, then up to MaxTrim.
	private double TrimFor(double smoothed)
	{
		double excess = smoothed - target;
		if (Math.Abs(excess) <= TrimDeadband)
		{
			return 0;
		}
		excess -= Math.Sign(excess) * TrimDeadband;
		return Math.Clamp(excess / TrimFullScale * MaxTrim, -MaxTrim, MaxTrim);
	}

	private void Remember(short s)
	{
		history[historyAt] = s;
		historyAt = (historyAt + 1) % HistorySize;
	}

	// The history sample `back` samples before the newest (1 = the newest).
	private short Back(int back) => history[(historyAt - back + HistorySize * 2) % HistorySize];

	// Caller holds gate. Picks the period to repeat: the lag (MinLag..MaxLag) whose waveform best matches the last
	// MatchWindow samples heard (normalised correlation), and copies the last period. Silence: nothing to repeat.
	private void StartConcealment()
	{
		double energy = 0;
		for (int i = 1; i <= MatchWindow; i++)
		{
			energy += Back(i) * (double)Back(i);
		}
		if (energy < MatchWindow * 4.0)
		{
			patternLength = 0;
			concealAt = ConcealSamples;
			return;
		}
		int bestLag = MaxLag;
		double best = double.NegativeInfinity;
		for (int lag = MinLag; lag <= MaxLag; lag++)
		{
			double dot = 0, other = 0;
			for (int i = 1; i <= MatchWindow; i++)
			{
				double a = Back(i), b = Back(i + lag);
				dot += a * b;
				other += b * b;
			}
			double score = other > 0 ? dot / Math.Sqrt(other) : double.NegativeInfinity;
			if (score > best)
			{
				best = score;
				bestLag = lag;
			}
		}
		patternLength = bestLag;
		for (int i = 0; i < bestLag; i++)
		{
			pattern[i] = Back(bestLag - i);
		}
		concealAt = 0;
	}

	// Caller holds gate. The rest of a running concealment (the period repeated, fading out), then silence.
	private void Conceal(Span<short> output)
	{
		int i = 0;
		for (; i < output.Length && concealAt < ConcealSamples && patternLength > 0; i++, concealAt++)
		{
			output[i] = (short)(pattern[concealAt % patternLength] * (ConcealSamples - concealAt) / ConcealSamples);
			concealed++;
		}
		output[i..].Clear();
	}
}
