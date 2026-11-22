// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Bridge;

namespace Kopru.Core.Audio;

// Which part of the game's sound becomes haptics: Bass keeps what is felt more than heard (explosions, engines, impacts,
// below ~140 Hz); Full keeps a wider band (up to ~400 Hz: also footsteps, gunfire bodies, collisions).
public enum AudioHapticsBand
{
	Bass,
	Full
}

// Turns captured game sound (interleaved float, any rate and channel count) into the controller's 3 kHz signed 8-bit
// stereo haptics frames:
//  1. downmix to left/right (centre and LFE go to both sides, surround channels to their side, mono to both);
//  2. a 25 Hz high-pass (2nd order) against DC and rumble the actuators cannot play, and a 4th-order Butterworth low-pass
//     at the band's cutoff (Bass 140 Hz, Full 400 Hz), at the input rate;
//  3. down to 3 kHz by averaging the samples of each output frame (the low-pass already removed everything above 400 Hz);
//  4. at 3 kHz: an envelope follower (peak, 2 ms attack, 120 ms release) on the louder side, a noise gate with hysteresis
//     and hold (opens where Sensitivity says, -46 dBFS by default; closes 6 dB under that after 60 ms), and a compressor
//     on that envelope (threshold -30 dBFS, ratio 4:1, +18 dB makeup) whose gain is applied to both sides alike, so left/right stay as the game mixed
//     them and quiet scenes do not buzz while loud ones do not saturate;
//  5. strength (0..2), then a soft limit (tanh) to full scale.
// A closed gate gives exact zeros (and digital silence into settled filters stays exactly 0), so the haptics stream can go
// idle. Allocates nothing after construction; not thread-safe (one capture thread). Band, strength and sensitivity may be
// changed from any thread: they are read once per block.
public sealed class AudioHapticsDsp
{
	public const int OutputRate = HapticsConverter.OutputRate;
	public const double BassCutoffHz = 140;
	public const double FullCutoffHz = 400;
	public const double HighPassHz = 25;
	public const double AttackMs = 2;
	public const double ReleaseMs = 120;
	// Sensitivity (0-100 %) sets where the gate opens: -22 dBFS at 0 %, -46 at 50 % (the default), -70 at 100 %; it closes
	// GateHysteresisDb under that.
	public const double DefaultSensitivity = 50;
	public const double MaxSensitivity = 100;
	public const double GateOpenDbAtZero = -22;
	// dB the open threshold moves over the whole 0-100 % (0.48 dB per percent).
	public const double GateOpenDbRange = 48;
	public const double GateHysteresisDb = 6;
	public static readonly double DefaultGateOpenDb = GateOpenDbFor(DefaultSensitivity);
	public const double GateHoldMs = 60;
	public const double GateFadeInMs = 2;
	public const double GateFadeOutMs = 25;
	public const double ThresholdDb = -30;
	public const double Ratio = 4;
	public const double MakeupDb = 18;
	public const double MaxStrength = 2;

	// Filter state below this is set to 0 (far under one output step), so silence settles at exactly 0.
	private const double Settled = 1e-9;

	// Gate gain under this counts as closed: exact zeros go out.
	private const double GateClosed = 1e-4;

	private int inputRate;
	private int channels;
	private AudioHapticsBand builtBand;
	private bool configured;

	// Per side: high-pass, then two low-pass sections.
	private Biquad hpL, hpR, lp1L, lp1R, lp2L, lp2R;

	// Averaging down to 3 kHz: sums since the last output frame, their count, and the rate accumulator.
	private double sumL, sumR;
	private int sumCount;
	private long phase;

	// 3 kHz stage.
	private double envelope;
	private double peakEnvelope;
	private double gateGain;
	private bool gateOpen;
	private int holdFrames;
	private double attackCoef, releaseCoef, gateInCoef, gateOutCoef;
	private int holdLength;

	private volatile int band = (int)AudioHapticsBand.Bass;
	private double strength = 1;
	private double sensitivity = DefaultSensitivity;

	public AudioHapticsDsp()
	{
		attackCoef = Coef(AttackMs);
		releaseCoef = Coef(ReleaseMs);
		gateInCoef = Coef(GateFadeInMs);
		gateOutCoef = Coef(GateFadeOutMs);
		holdLength = (int)(GateHoldMs * OutputRate / 1000);
	}

	public AudioHapticsBand Band
	{
		get => (AudioHapticsBand)band;
		set => band = (int)value;
	}

	// 0..2 (0-200 %): 1 plays the compressed level as is.
	public double Strength
	{
		get => Volatile.Read(ref strength);
		set => Volatile.Write(ref strength, Math.Clamp(double.IsFinite(value) ? value : 1, 0, MaxStrength));
	}

	// 0..100 %: how quiet a sound can be and still open the gate (higher: quieter sounds count).
	public double Sensitivity
	{
		get => Volatile.Read(ref sensitivity);
		set => Volatile.Write(ref sensitivity, ClampSensitivity(value));
	}

	// Where the gate opens and closes now (dBFS).
	public double GateOpenDb => GateOpenDbFor(Sensitivity);
	public double GateCloseDb => GateOpenDb - GateHysteresisDb;

	// The gate's open threshold (dBFS) for a sensitivity in percent (clamped to 0-100; not finite reads as the default).
	public static double GateOpenDbFor(double sensitivityPercent) => GateOpenDbAtZero - GateOpenDbRange * ClampSensitivity(sensitivityPercent) / MaxSensitivity;

	public static double ClampSensitivity(double value) => Math.Clamp(double.IsFinite(value) ? value : DefaultSensitivity, 0, MaxSensitivity);

	// The envelope (0..1 of full scale) after the last block, for diagnostics.
	public double Envelope => envelope;

	// The loudest envelope (0..1 of full scale, what the gate compares with) since Reset, for diagnostics.
	public double PeakEnvelope => peakEnvelope;

	// Whether the gate let anything through in the last frame.
	public bool GateOpen => gateOpen;

	// The most frames Process can produce from this many input frames.
	public static int MaxFrames(int inputFrames, int inputRate) =>
		(int)(((long)inputFrames * OutputRate + inputRate - 1) / Math.Max(1, inputRate)) + 1;

	// Forgets all state (a new capture run).
	public void Reset()
	{
		configured = false;
		peakEnvelope = 0;
		ResetState();
	}

	// Processes one block of interleaved samples (frames = samples.Length / channels) into left/right; returns the frames
	// written. left and right must hold at least MaxFrames(frames, sampleRate).
	public int Process(ReadOnlySpan<float> samples, int channelCount, int sampleRate, Span<sbyte> left, Span<sbyte> right)
	{
		if (channelCount < 1 || sampleRate < OutputRate)
		{
			return 0;
		}
		AudioHapticsBand wanted = Band;
		if (!configured || channelCount != channels || sampleRate != inputRate || wanted != builtBand)
		{
			Configure(channelCount, sampleRate, wanted);
		}
		int frames = samples.Length / channelCount;
		if (left.Length < MaxFrames(frames, sampleRate) || right.Length < left.Length)
		{
			throw new ArgumentException("output too short", nameof(left));
		}
		double k = Strength;
		double open = GateOpenDb, close = open - GateHysteresisDb;
		int written = 0;
		for (int f = 0; f < frames; f++)
		{
			ReadOnlySpan<float> frame = samples.Slice(f * channelCount, channelCount);
			Downmix(frame, out double l, out double r);
			bool silent = l == 0 && r == 0;
			if (!silent || !hpL.IsZero || !hpR.IsZero || !lp1L.IsZero || !lp1R.IsZero || !lp2L.IsZero || !lp2R.IsZero)
			{
				l = lp2L.Run(lp1L.Run(hpL.Run(l)));
				r = lp2R.Run(lp1R.Run(hpR.Run(r)));
				if (silent)
				{
					hpL.Settle(); hpR.Settle(); lp1L.Settle(); lp1R.Settle(); lp2L.Settle(); lp2R.Settle();
				}
				sumL += l;
				sumR += r;
			}
			sumCount++;
			phase += OutputRate;
			if (phase >= inputRate)
			{
				phase -= inputRate;
				double outL = sumL / sumCount, outR = sumR / sumCount;
				sumL = sumR = 0;
				sumCount = 0;
				Shape(outL, outR, k, open, close, out left[written], out right[written]);
				written++;
			}
		}
		return written;
	}

	// The 3 kHz stage: envelope, gate, compressor, strength and soft limit.
	private void Shape(double l, double r, double k, double openDb, double closeDb, out sbyte outL, out sbyte outR)
	{
		double peak = Math.Max(Math.Abs(l), Math.Abs(r));
		envelope += (peak > envelope ? attackCoef : releaseCoef) * (peak - envelope);
		if (envelope < Settled)
		{
			envelope = 0;
		}
		if (envelope > peakEnvelope)
		{
			peakEnvelope = envelope;
		}
		double db = envelope > 0 ? 20 * Math.Log10(envelope) : -200;
		if (db >= openDb)
		{
			gateOpen = true;
			holdFrames = holdLength;
		}
		else if (db < closeDb && gateOpen && --holdFrames <= 0)
		{
			gateOpen = false;
		}
		double target = gateOpen ? 1 : 0;
		gateGain += (target > gateGain ? gateInCoef : gateOutCoef) * (target - gateGain);
		if (!gateOpen && gateGain < GateClosed)
		{
			gateGain = 0;
		}
		if (gateGain == 0 || k == 0 || envelope == 0)
		{
			outL = outR = 0;
			return;
		}
		double reduction = db > ThresholdDb ? (db - ThresholdDb) * (1 - 1 / Ratio) : 0;
		double gain = Math.Pow(10, (MakeupDb - reduction) / 20) * gateGain * k;
		outL = ToSample(l * gain);
		outR = ToSample(r * gain);
	}

	private void Downmix(ReadOnlySpan<float> frame, out double l, out double r)
	{
		switch (frame.Length)
		{
			case 1:
				l = r = frame[0];
				return;
			case 2:
				l = frame[0];
				r = frame[1];
				return;
		}
		// FL, FR, FC, LFE, then pairs (back/side) left and right.
		const double Side = 0.7071;
		l = frame[0];
		r = frame[1];
		double centre = frame[2] * Side;
		l += centre;
		r += centre;
		if (frame.Length > 3)
		{
			double lfe = frame[3] * Side;
			l += lfe;
			r += lfe;
		}
		for (int c = 4; c < frame.Length; c++)
		{
			if ((c & 1) == 0)
			{
				l += frame[c] * Side;
			}
			else
			{
				r += frame[c] * Side;
			}
		}
	}

	private void Configure(int channelCount, int sampleRate, AudioHapticsBand wanted)
	{
		bool rateChanged = sampleRate != inputRate;
		channels = channelCount;
		inputRate = sampleRate;
		builtBand = wanted;
		double cutoff = wanted == AudioHapticsBand.Full ? FullCutoffHz : BassCutoffHz;
		// 4th-order Butterworth low-pass: two sections with Q 0.5412 and 1.3066.
		Biquad.LowPass(ref lp1L, sampleRate, cutoff, 0.54119610);
		Biquad.LowPass(ref lp2L, sampleRate, cutoff, 1.30656296);
		Biquad.HighPass(ref hpL, sampleRate, HighPassHz, 0.70710678);
		lp1R.CopyCoefficients(lp1L);
		lp2R.CopyCoefficients(lp2L);
		hpR.CopyCoefficients(hpL);
		if (!configured || rateChanged)
		{
			ResetState();
		}
		configured = true;
	}

	private void ResetState()
	{
		hpL.Clear(); hpR.Clear(); lp1L.Clear(); lp1R.Clear(); lp2L.Clear(); lp2R.Clear();
		sumL = sumR = 0;
		sumCount = 0;
		phase = 0;
		envelope = 0;
		gateGain = 0;
		gateOpen = false;
		holdFrames = 0;
	}

	private static double Coef(double ms) => 1 - Math.Exp(-1000.0 / (ms * OutputRate));

	// tanh keeps small signals linear and bends strong ones smoothly towards full scale; anything under one step is 0.
	private static sbyte ToSample(double value) => (sbyte)(int)(Math.Tanh(value) * 127);

	// A transposed direct form II biquad (RBJ cookbook coefficients).
	private struct Biquad
	{
		private double b0, b1, b2, a1, a2;
		private double z1, z2;

		public readonly bool IsZero => z1 == 0 && z2 == 0;

		public double Run(double x)
		{
			double y = b0 * x + z1;
			z1 = b1 * x - a1 * y + z2;
			z2 = b2 * x - a2 * y;
			return y;
		}

		public void Settle()
		{
			if (Math.Abs(z1) < Settled && Math.Abs(z2) < Settled)
			{
				z1 = z2 = 0;
			}
		}

		public void Clear() => z1 = z2 = 0;

		public void CopyCoefficients(in Biquad other)
		{
			(b0, b1, b2, a1, a2) = (other.b0, other.b1, other.b2, other.a1, other.a2);
		}

		public static void LowPass(ref Biquad f, double rate, double hz, double q)
		{
			double w = 2 * Math.PI * hz / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
			f.b0 = (1 - cos) / 2 / a0;
			f.b1 = (1 - cos) / a0;
			f.b2 = f.b0;
			f.a1 = -2 * cos / a0;
			f.a2 = (1 - alpha) / a0;
		}

		public static void HighPass(ref Biquad f, double rate, double hz, double q)
		{
			double w = 2 * Math.PI * hz / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
			f.b0 = (1 + cos) / 2 / a0;
			f.b1 = -(1 + cos) / a0;
			f.b2 = f.b0;
			f.a1 = -2 * cos / a0;
			f.a2 = (1 - alpha) / a0;
		}
	}
}
