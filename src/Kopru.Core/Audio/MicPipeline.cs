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

// Controller microphone path: controller reports -> Opus decode -> jitter buffer -> gain, noise gate -> the virtual pad's
// mic endpoint (mono duplicated to every channel). Off is handled by switching the whole feature off (the mic button).
// Reports lost on the way (a gap in the report counter, byte 2) are filled with the decoder's packet loss concealment, so
// the timing stays right and the jitter buffer does not run dry; a gap longer than MaxConcealedPackets is a restart of
// the stream, not loss (the buffer conceals and refills).
// The level meter is measured on both sides: as the controller's packets arrive (after decode and gain) and as the virtual
// pad's endpoint is read. Windows reads that endpoint only while some app records from it, so the arrival side keeps the
// meter moving when nothing records; while something does, the endpoint side (what games get: gate, mute, test tone) wins.
public sealed class MicPipeline : IMicFeed, IDisposable
{
	// Lost reports in a row concealed at most (50 ms); a longer gap is not filled.
	public const int MaxConcealedPackets = 5;

	public const double MaxGain = 4;
	// Noise gate: blocks quieter than this (peak, 0..1 of full scale, about -46 dBFS) are silenced after the hold time.
	public const double GateThreshold = 0.005;
	public const int GateHoldSamples = 48 * 200;

	private readonly IMicDecoder? decoder;

	private readonly short[] decoded = new short[5760];

	private short[] mono = new short[480];

	private double gain = 1;

	private int gateQuiet = GateHoldSamples;

	// The level meter: a side not updated for this long reads as silence (the mic went off, the stream stalled, the game
	// stopped recording), so the meter never freezes on its last value.
	public static readonly TimeSpan LevelTimeout = TimeSpan.FromMilliseconds(200);

	// Each new block's peak against the last level times this (the meter falls off slowly).
	private const double LevelDecay = 0.9;

	private const long NotYet = long.MinValue;

	private readonly TimeProvider time;

	// Peak with decay (0..1) and when it was last updated (time's timestamp; NotYet: never). One writer per side: the
	// endpoint's reads (fillLevel) and the session's read thread (inputLevel); any thread reads.
	private double fillLevel, inputLevel;

	private long fillLevelAt = NotYet, inputLevelAt = NotYet;

	public MicPipeline(IMicDecoder? decoder, MicJitterBuffer? buffer = null, TimeProvider? time = null)
	{
		this.decoder = decoder;
		this.time = time ?? TimeProvider.System;
		Buffer = buffer ?? new MicJitterBuffer();
	}

	public MicJitterBuffer Buffer { get; }

	public volatile bool NoiseGate;

	// Games get silence while set (the mic button in "Mute" mode); the stream and the level meter keep running.
	public volatile bool Muted;

	// Replaces the controller's audio (test tone) while set.
	public volatile IMicSource? Source;

	public double Gain
	{
		get => Volatile.Read(ref gain);
		set => Volatile.Write(ref gain, Math.Clamp(value, 0, MaxGain));
	}

	// The level meter (0..1 after gain, falls off slowly; 0 after LevelTimeout without audio): what the endpoint gave while
	// it is being read, otherwise what the controller sends.
	public double Level
	{
		get
		{
			if (Fresh(ref fillLevelAt))
			{
				return Volatile.Read(ref fillLevel);
			}
			return Fresh(ref inputLevelAt) ? Volatile.Read(ref inputLevel) : 0;
		}
	}

	private bool Fresh(ref long at)
	{
		long t = Volatile.Read(ref at);
		return t != NotYet && time.GetElapsedTime(t) < LevelTimeout;
	}

	// The peak of a block against the decayed last level (a stale level counts as silence); value first, then the time.
	private void UpdateLevel(ref double value, ref long at, double peak)
	{
		double last = Fresh(ref at) ? Volatile.Read(ref value) : 0;
		Volatile.Write(ref value, Math.Max(peak, last * LevelDecay));
		Volatile.Write(ref at, time.GetTimestamp());
	}

	// The arrival side of the meter: the block's peak after gain (the gate and mute are the endpoint's).
	private void MeasureInput(ReadOnlySpan<short> pcm)
	{
		int peak = 0;
		foreach (short v in pcm)
		{
			peak = Math.Max(peak, Math.Abs((int)v));
		}
		UpdateLevel(ref inputLevel, ref inputLevelAt, Math.Min(1, peak * Gain / 32768.0));
	}

	public long Packets => Interlocked.Read(ref packets);

	public long BadPackets => Interlocked.Read(ref badPackets);

	// Reports missing from the counter sequence, and how many of them were concealed (MaxConcealedPackets at most a gap).
	public long LostPackets => Interlocked.Read(ref lostPackets);

	public long ConcealedPackets => Interlocked.Read(ref concealedPackets);

	private long packets, badPackets, lostPackets, concealedPackets;

	// The last report counter seen (-1: none yet). Only touched on the session's read thread.
	private int lastCounter = -1;

	// A raw input report from the session (non-mic reports are ignored).
	public void PushReport(ReadOnlySpan<byte> report)
	{
		if (MicPackets.TryGetOpus(report, out ReadOnlySpan<byte> opus))
		{
			ConcealLost(report[MicPackets.CounterOffset]);
			PushOpus(opus);
		}
	}

	// Fills the reports missing before this counter with the decoder's concealment (a gap of more than half the counter
	// range is a repeat or a restart: nothing to fill).
	private void ConcealLost(byte counter)
	{
		int last = lastCounter;
		lastCounter = counter;
		if (last < 0 || decoder == null)
		{
			return;
		}
		int gap = (counter - last - 1) & 0xFF;
		if (gap == 0 || gap > 128)
		{
			return;
		}
		Interlocked.Add(ref lostPackets, gap);
		if (gap > MaxConcealedPackets)
		{
			return;
		}
		for (int i = 0; i < gap; i++)
		{
			Buffer.Write(decoded.AsSpan(0, SafeConceal()));
		}
		Interlocked.Add(ref concealedPackets, gap);
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
			MeasureInput(decoded.AsSpan(0, n));
			Buffer.Write(decoded.AsSpan(0, n));
		}
	}

	public void PushPcm(ReadOnlySpan<short> mono48k)
	{
		Interlocked.Increment(ref packets);
		MeasureInput(mono48k);
		Buffer.Write(mono48k);
	}

	// The stream starts again (the mic switched back on): the report counter starts over and the buffer is emptied
	// without counting an underrun. Call while no reports arrive (before hooking PushReport).
	public void Restart()
	{
		lastCounter = -1;
		Buffer.Clear();
	}

	// What happened since the last call, one log line: "1000 packets (2 lost, 2 concealed, 0 bad), 1 underruns (10 ms
	// concealed), 0 ms dropped, buffer 31/50/62 ms min/target/max, rate trim +0.00 %".
	public string TakeSummary()
	{
		MicStats now = new(Packets, LostPackets, ConcealedPackets, BadPackets, Buffer.Underruns, Buffer.Concealed, Buffer.Dropped);
		MicStats d = now - summarised;
		summarised = now;
		(int Min, int Max)? levels = Buffer.TakeLevels();
		string fill = levels is { } l ? $"{Ms(l.Min)}/{Ms(Buffer.Target)}/{Ms(l.Max)}" : $"-/{Ms(Buffer.Target)}/-";
		return string.Create(System.Globalization.CultureInfo.InvariantCulture,
			$"{d.Packets} packets ({d.Lost} lost, {d.Concealed} concealed, {d.Bad} bad), {d.Underruns} underruns ({Ms(d.UnderrunConcealed)} ms concealed), {Ms(d.Dropped)} ms dropped, buffer {fill} ms min/target/max, rate trim {Buffer.Trim * 100:+0.00;-0.00;0.00} %");
	}

	// Anything worth a line since the last summary (packets arrived or the buffer had trouble).
	public bool HasNews => Packets != summarised.Packets || Buffer.Underruns != summarised.Underruns;

	private MicStats summarised;

	private static long Ms(long samples) => samples / MicJitterBuffer.SamplesPerMs;

	private readonly record struct MicStats(long Packets, long Lost, long Concealed, long Bad, long Underruns, long UnderrunConcealed, long Dropped)
	{
		public static MicStats operator -(MicStats a, MicStats b) => new(a.Packets - b.Packets, a.Lost - b.Lost,
			a.Concealed - b.Concealed, a.Bad - b.Bad, a.Underruns - b.Underruns, a.UnderrunConcealed - b.UnderrunConcealed,
			a.Dropped - b.Dropped);
	}

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
		UpdateLevel(ref fillLevel, ref fillLevelAt, Process(m));
		pcm.Clear();
		if (Muted)
		{
			// "Mute" mode of the mic button: the stream keeps running, games get silence (the meter still moves).
			return;
		}
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
