// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Kopru.Core.Audio;

// Fills the virtual pad's isochronous mic packets (16-bit LE interleaved PCM, `channels` channels at 48 kHz).
public interface IMicFeed
{
	void Fill(Span<byte> pcm, int channels);
}

// A pull source of mono 48 kHz audio (the test tone; replaces the controller's packets when set).
public interface IMicSource
{
	void Read(Span<short> mono);
}

// Mono linear-interpolation resampler, state kept across calls.
public sealed class MonoResampler(int inputRate, int outputRate)
{
	private readonly double step = (double)inputRate / outputRate;

	private double position;

	private short last;

	public void Process(ReadOnlySpan<short> input, List<short> output)
	{
		foreach (short sample in input)
		{
			while (position <= 1)
			{
				output.Add((short)Math.Round(last + (sample - last) * position));
				position += step;
			}
			position -= 1;
			last = sample;
		}
	}
}

// Mono sample FIFO between the controller's packets (bursty, Bluetooth) and the host's 1 ms isochronous reads. Reads
// return silence until Prefill samples are buffered (again after every underrun); beyond Capacity the oldest are dropped.
public sealed class MicJitterBuffer(int prefill = MicJitterBuffer.DefaultPrefill, int capacity = MicJitterBuffer.DefaultCapacity)
{
	public const int DefaultPrefill = 48 * 30;   // 30 ms
	public const int DefaultCapacity = 48 * 120; // 120 ms

	private readonly short[] ring = new short[capacity];

	private readonly object gate = new();

	private int head, count;

	private bool primed;

	public int Prefill { get; } = Math.Clamp(prefill, 0, capacity);

	public int Capacity => ring.Length;

	public long Underruns { get; private set; }

	public long Dropped { get; private set; }

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
					Dropped++;
				}
				ring[(head + count) % ring.Length] = s;
				count++;
			}
		}
	}

	// Fills output (silence where nothing is buffered); returns the real samples given.
	public int Read(Span<short> output)
	{
		lock (gate)
		{
			if (!primed)
			{
				if (count < Prefill || count == 0)
				{
					output.Clear();
					return 0;
				}
				primed = true;
			}
			int n = Math.Min(count, output.Length);
			for (int i = 0; i < n; i++)
			{
				output[i] = ring[head];
				head = (head + 1) % ring.Length;
			}
			count -= n;
			if (n < output.Length)
			{
				output[n..].Clear();
				primed = false;
				Underruns++;
			}
			return n;
		}
	}

	public void Clear()
	{
		lock (gate)
		{
			head = count = 0;
			primed = false;
		}
	}
}

// Controller microphone path: controller reports -> Opus decode -> jitter buffer -> gain, noise gate -> the virtual pad's
// mic endpoint (mono duplicated to every channel). Off is handled by switching the whole feature off (the mic button).
public sealed class MicPipeline : IMicFeed, IDisposable
{
	public const double MaxGain = 4;
	// Noise gate: blocks quieter than this (peak, 0..1 of full scale, about -46 dBFS) are silenced after the hold time.
	public const double GateThreshold = 0.005;
	public const int GateHoldSamples = 48 * 200;

	private readonly IMicDecoder? decoder;

	private readonly short[] decoded = new short[5760];

	private short[] mono = new short[480];

	private double gain = 1;

	private int gateQuiet = GateHoldSamples;

	private double level;

	public MicPipeline(IMicDecoder? decoder, MicJitterBuffer? buffer = null)
	{
		this.decoder = decoder;
		Buffer = buffer ?? new MicJitterBuffer();
	}

	public MicJitterBuffer Buffer { get; }

	public volatile bool NoiseGate;

	// Replaces the controller's audio (test tone) while set.
	public volatile IMicSource? Source;

	public double Gain
	{
		get => Volatile.Read(ref gain);
		set => Volatile.Write(ref gain, Math.Clamp(value, 0, MaxGain));
	}

	// Peak of the last filled block after gain (0..1, falls off slowly), for the level meter.
	public double Level => Volatile.Read(ref level);

	public long Packets => Interlocked.Read(ref packets);

	public long BadPackets => Interlocked.Read(ref badPackets);

	private long packets, badPackets;

	// A raw input report from the session (non-mic reports are ignored).
	public void PushReport(ReadOnlySpan<byte> report)
	{
		if (MicPackets.TryGetOpus(report, out ReadOnlySpan<byte> opus))
		{
			PushOpus(opus);
		}
	}

	public void PushOpus(ReadOnlySpan<byte> packet)
	{
		if (decoder == null)
		{
			return;
		}
		int n;
		try
		{
			n = decoder.Decode(packet, decoded);
		}
		catch (Exception)
		{
			// A packet we could not read: conceal it so timing stays right.
			Interlocked.Increment(ref badPackets);
			n = SafeConceal();
		}
		Interlocked.Increment(ref packets);
		if (n > 0)
		{
			Buffer.Write(decoded.AsSpan(0, n));
		}
	}

	public void PushPcm(ReadOnlySpan<short> mono48k) => Buffer.Write(mono48k);

	public void Fill(Span<byte> pcm, int channels)
	{
		channels = Math.Max(1, channels);
		int samples = pcm.Length / (2 * channels);
		if (mono.Length < samples)
		{
			mono = new short[samples];
		}
		Span<short> m = mono.AsSpan(0, samples);
		IMicSource? source = Source;
		if (source != null)
		{
			source.Read(m);
		}
		else
		{
			Buffer.Read(m);
		}
		double peak = Process(m);
		Volatile.Write(ref level, Math.Max(peak, Level * 0.9));
		pcm.Clear();
		for (int i = 0; i < samples; i++)
		{
			for (int c = 0; c < channels; c++)
			{
				BinaryPrimitives.WriteInt16LittleEndian(pcm[(2 * (i * channels + c))..], m[i]);
			}
		}
	}

	public void Dispose() => decoder?.Dispose();

	// Gain and gate in place; returns the block's peak (0..1).
	private double Process(Span<short> m)
	{
		double g = Gain;
		int peak = 0;
		for (int i = 0; i < m.Length; i++)
		{
			int v = (int)Math.Clamp(Math.Round(m[i] * g), short.MinValue, short.MaxValue);
			m[i] = (short)v;
			peak = Math.Max(peak, Math.Abs(v));
		}
		double p = peak / 32768.0;
		if (NoiseGate)
		{
			gateQuiet = p < GateThreshold ? Math.Min(gateQuiet + m.Length, GateHoldSamples) : 0;
			if (gateQuiet >= GateHoldSamples)
			{
				m.Clear();
				return 0;
			}
		}
		return p;
	}

	private int SafeConceal()
	{
		try
		{
			return decoder!.Conceal(decoded.AsSpan(0, MicPackets.FrameSamples));
		}
		catch (Exception)
		{
			decoded.AsSpan(0, MicPackets.FrameSamples).Clear();
			return MicPackets.FrameSamples;
		}
	}
}

// A sine for checking the virtual mic without the controller's audio (probe and tests).
public sealed class TestToneMicSource(double hz = 440, double amplitude = 0.3) : IMicSource
{
	private double phase;

	public void Read(Span<short> mono)
	{
		double step = 2 * Math.PI * hz / MicPackets.SampleRate;
		for (int i = 0; i < mono.Length; i++)
		{
			mono[i] = (short)Math.Round(amplitude * short.MaxValue * Math.Sin(phase));
			phase = (phase + step) % (2 * Math.PI);
		}
	}
}

// 16-bit PCM WAV (probe recordings).
public static class WavWriter
{
	public static byte[] Build(ReadOnlySpan<short> samples, int rate, int channels)
	{
		int dataBytes = samples.Length * 2;
		byte[] wav = new byte[44 + dataBytes];
		Span<byte> w = wav;
		"RIFF"u8.CopyTo(w);
		BinaryPrimitives.WriteInt32LittleEndian(w[4..], 36 + dataBytes);
		"WAVEfmt "u8.CopyTo(w[8..]);
		BinaryPrimitives.WriteInt32LittleEndian(w[16..], 16);
		BinaryPrimitives.WriteInt16LittleEndian(w[20..], 1);
		BinaryPrimitives.WriteInt16LittleEndian(w[22..], (short)channels);
		BinaryPrimitives.WriteInt32LittleEndian(w[24..], rate);
		BinaryPrimitives.WriteInt32LittleEndian(w[28..], rate * channels * 2);
		BinaryPrimitives.WriteInt16LittleEndian(w[32..], (short)(channels * 2));
		BinaryPrimitives.WriteInt16LittleEndian(w[34..], 16);
		"data"u8.CopyTo(w[36..]);
		BinaryPrimitives.WriteInt32LittleEndian(w[40..], dataBytes);
		for (int i = 0; i < samples.Length; i++)
		{
			BinaryPrimitives.WriteInt16LittleEndian(w[(44 + 2 * i)..], samples[i]);
		}
		return wav;
	}
}
