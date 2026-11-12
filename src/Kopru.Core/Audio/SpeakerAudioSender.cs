// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Kopru.Core.Audio;

// Stereo linear-interpolation resampler for 16-bit audio with state kept across calls. The controller takes one 480-sample
// Opus frame per 10.67 ms report, i.e. 45 000 samples/s, so the game's 48 kHz audio is resampled 512 -> 480.
// Output samples lie between two input samples, so they never leave the input's range (no overshoot, no clipping).
public sealed class StereoResampler(int inputRate, int outputRate)
{
	private readonly double step = (double)inputRate / outputRate;

	private double position; // of the next output sample, relative to the previous input sample

	private short lastLeft, lastRight;

	// Small rate correction: +0.01 makes 1 % fewer output samples (the buffer drains faster), -0.01 1 % more.
	public double Trim { get; set; }

	public void Reset()
	{
		position = 0;
		lastLeft = lastRight = 0;
	}

	public void Process(short left, short right, Action<short, short> output)
	{
		double advance = step * (1 + Trim);
		// Output samples fall between the previous input (at 0) and this one (at 1).
		while (position <= 1)
		{
			double t = position;
			output((short)Math.Round(lastLeft + (left - lastLeft) * t), (short)Math.Round(lastRight + (right - lastRight) * t));
			position += advance;
		}
		position -= 1;
		lastLeft = left;
		lastRight = right;
	}
}

public interface IOpusEncoder : IDisposable
{
	// pcm: interleaved stereo, FrameSamples per channel; returns the bytes written (exactly output.Length when CBR).
	int Encode(ReadOnlySpan<short> pcm, Span<byte> output);
}

// Optional fallback: libopus through P/Invoke (opus.dll next to the program; BSD-3-Clause, not shipped). Used only when
// KOPRU_OPUS=native and opus.dll loads (OpusEncoders.Create); ConcentusOpusEncoder is the default. Same settings.
public sealed class NativeOpusEncoder : IOpusEncoder
{
	private const int ApplicationRestrictedLowDelay = 2051;
	private const int SetBitrate = 4002;
	private const int SetVbr = 4006;

	private IntPtr state;

	public NativeOpusEncoder(int bitrate = SpeakerAudioSender.OpusBitrate)
	{
		state = opus_encoder_create(SpeakerAudioSender.OpusRate, 2, ApplicationRestrictedLowDelay, out int error);
		if (error != 0 || state == IntPtr.Zero)
		{
			throw new InvalidOperationException($"opus_encoder_create failed ({error})");
		}
		opus_encoder_ctl(state, SetVbr, 0);
		opus_encoder_ctl(state, SetBitrate, bitrate);
	}

	public int Encode(ReadOnlySpan<short> pcm, Span<byte> output)
	{
		int written = opus_encode(state, in MemoryMarshal.GetReference(pcm), pcm.Length / 2, ref MemoryMarshal.GetReference(output), output.Length);
		if (written < 0)
		{
			throw new InvalidOperationException($"opus_encode failed ({written})");
		}
		// CBR normally fills the frame already; padding keeps it a valid Opus packet if it does not.
		if (written < output.Length && opus_packet_pad(ref MemoryMarshal.GetReference(output), written, output.Length) == 0)
		{
			written = output.Length;
		}
		return written;
	}

	public void Dispose()
	{
		if (state != IntPtr.Zero)
		{
			opus_encoder_destroy(state);
			state = IntPtr.Zero;
		}
	}

	[DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr opus_encoder_create(int fs, int channels, int application, out int error);

	// Variadic in C; with one int argument the Windows x64 calling convention is the same as a fixed signature.
	[DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
	private static extern int opus_encoder_ctl(IntPtr st, int request, int value);

	[DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
	private static extern int opus_encode(IntPtr st, in short pcm, int frameSize, ref byte data, int maxDataBytes);

	[DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
	private static extern int opus_packet_pad(ref byte data, int len, int newLen);

	[DllImport("opus", CallingConvention = CallingConvention.Cdecl)]
	private static extern void opus_encoder_destroy(IntPtr st);
}

// EXPERIMENTAL (off unless a BluetoothSession is given one): takes the game's speaker channels (1-2 of the virtual pad's
// 4-channel 48 kHz audio), resamples them to the controller's 45 kHz report clock, and hands out one 200-byte Opus frame
// per report. On the built-in speaker (mono, right channel) both channels carry L+R; on headphones stereo is kept.
// Push and TakeFrame may be called from different threads.
//
// The game's audio (USB isochronous transfers, handed over in bursts) and the reports (our own timer) do not run on one
// clock, and neither is smooth. The sender keeps a cushion of about TargetSamples between them and holds it there: the
// buffer level, averaged over a few reports, trims the resampling ratio by up to MaxTrim (a slow PI control, inaudible
// for game effects) so a source that runs slightly fast or slow neither drains the buffer nor fills it up. Should the
// buffer run dry anyway, the sound fades out over FadeSamples instead of stopping dead, and fades in again when it resumes.
// Nothing here adds gain: the mono mix halves the sum, the resampler interpolates between samples and fades only
// attenuate, so no sample can exceed the game's own full scale (the volume is a setting sent to the controller).
public sealed class SpeakerAudioSender : IDisposable
{
	public const int InputChannels = 4;
	public const int InputRate = 48000;
	public const int OpusRate = 48000;
	public const int FrameSamples = 480;
	public const int ReportRate = 45000; // 480 samples every 32/3000 s
	public const int OpusBitrate = SpeakerReports.OpusFrameBytes * 8 * OpusRate / FrameSamples;

	// ~213 ms; only audio beyond this is dropped (oldest first). Room for a burst at the start of a stream or a stalled
	// report thread; before it was ~64 ms and the first sound of a session overflowed it.
	public const int MaxBufferedSamples = FrameSamples * 20;

	// The cushion the buffer is held at (~53 ms, measured before each frame is taken). The game's audio comes in USB
	// chunks of ~10 ms, sometimes held back by 20-30 ms, while frames leave every 10.67 ms (with stalls of their own); a
	// ~21 ms cushion was not enough, and in the simulation (SpeakerClockTests) ~43 ms still ran dry at a sound's start.
	public const int TargetSamples = FrameSamples * 5;

	// A sound starts (and starts again after running dry) only once the cushion is there.
	public const int PrefillSamples = TargetSamples;

	// Fade length (~2.8 ms) when the buffer runs dry and when the sound resumes after that.
	public const int FadeSamples = 128;

	// Reports in a row without new audio after which the game counts as having stopped sending (~32 ms); before that a
	// gap is just a late USB burst.
	public const int StoppedAfterTakes = 3;

	// Largest resampling-ratio correction (2 %, a third of a semitone at the very most; ~0.5 % holds a 0.5 % clock
	// difference).
	public const double MaxTrim = 0.02;

	// Control: proportional gain per unit of relative level error, integral gain per report, integral limit, and the
	// level averaging (per report; ~16 reports = 170 ms).
	private const double ProportionalGain = 0.04;
	private const double IntegralGain = 0.0002;
	private const double MaxIntegral = 0.01;
	private const double LevelSmoothing = 1.0 / 16;

	private const int FrameBytes = InputChannels * 2;

	private readonly IOpusEncoder encoder;

	private readonly StereoResampler resampler = new(InputRate, ReportRate);

	private readonly object gate = new();

	private readonly short[] ring = new short[MaxBufferedSamples * 2];

	private readonly short[] frame = new short[FrameSamples * 2];

	private readonly byte[] partial = new byte[FrameBytes];

	private int partialLength;

	private int head, count;

	// Frames are going out (the prefill was reached, or the game stopped sending before it was).
	private bool playing;

	// Push was called since the last TakeFrame; reports in a row without a Push.
	private bool pushedSinceTake;
	private int takesSincePush;

	// Samples of fade-in still to apply (after running dry while the game kept sending).
	private int fadeIn;

	// Ran dry with the game seemingly still sending: an underrun if more audio follows before the game counts as stopped
	// (otherwise it was just the end of the sound arriving in a late burst).
	private bool ranDry;

	// The last sample handed to the encoder (a fade-out starts from it when nothing is left).
	private short lastLeft, lastRight;

	// Averaged level and the integral part of the trim (kept across sounds: the clocks stay the same).
	private double level, integral;

	// Buffer level (before each frame) while the game was sending, since ReadLevels; int.MaxValue / -1 when none.
	private int minLevel = int.MaxValue, maxLevel = -1;

	private long underruns, droppedSamples;

	public SpeakerAudioSender(IOpusEncoder encoder) => this.encoder = encoder;

	// Speaker volume 0..1 (sent in report 0x31 by the session while nothing is in the headphone jack).
	public double Volume { get; set; } = 0.8;

	// Headphone volume 0..1 (sent instead of Volume while headphones are plugged in).
	public double HeadphoneVolume { get; set; } = 0.6;

	// The volume for the current output.
	public double OutputVolume => Headset ? HeadphoneVolume : Volume;

	// Updated by the session from the input report's headphone bit; picks the jack (0x16) over the speaker (0x13).
	public bool Headset { get; set; }

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

	// The current resampling-ratio correction (positive: the game's audio arrives faster than the reports take it).
	public double Trim
	{
		get
		{
			lock (gate)
			{
				return resampler.Trim;
			}
		}
	}

	// Times the buffer ran dry while the game was still sending (faded out: an audible gap). Counted when the game's
	// next audio arrives within StoppedAfterTakes reports, so the end of a sound is never one. Diagnostics.
	public long Underruns => Interlocked.Read(ref underruns);

	// Samples dropped because the buffer was full (the game ran ahead of the reports). Diagnostics.
	public long DroppedSamples => Interlocked.Read(ref droppedSamples);

	// Lowest and highest buffer level (samples, before a frame is taken) while the game was sending, since the last call;
	// null when no frame went out in that time. Diagnostics.
	public (int Min, int Max)? ReadLevels()
	{
		lock (gate)
		{
			(int, int)? levels = maxLevel < 0 ? null : (minLevel, maxLevel);
			minLevel = int.MaxValue;
			maxLevel = -1;
			return levels;
		}
	}

	// Samples to milliseconds at the report rate.
	public static int Milliseconds(int samples) => (int)Math.Round(samples * 1000.0 / ReportRate);

	// audio: int16 LE interleaved, 4 channels at 48 kHz (any chunking).
	public void Push(ReadOnlySpan<byte> audio)
	{
		lock (gate)
		{
			if (ranDry && !audio.IsEmpty)
			{
				Interlocked.Increment(ref underruns);
				fadeIn = FadeSamples;
				ranDry = false;
			}
			pushedSinceTake = true;
			if (partialLength > 0)
			{
				int take = Math.Min(FrameBytes - partialLength, audio.Length);
				audio[..take].CopyTo(partial.AsSpan(partialLength));
				partialLength += take;
				audio = audio[take..];
				if (partialLength < FrameBytes)
				{
					return;
				}
				Input(partial);
				partialLength = 0;
			}
			while (audio.Length >= FrameBytes)
			{
				Input(audio[..FrameBytes]);
				audio = audio[FrameBytes..];
			}
			audio.CopyTo(partial);
			partialLength = audio.Length;
		}
	}

	// Encodes the next 480 samples into opus (200 bytes); false when there is nothing to send yet, so the caller can fall
	// back to the haptics-only report. A sound waits for PrefillSamples, unless the game stopped sending (no Push for
	// StoppedAfterTakes calls): then what is left goes out, faded and padded with silence at the end.
	public bool TakeFrame(Span<byte> opus)
	{
		lock (gate)
		{
			takesSincePush = pushedSinceTake ? 0 : Math.Min(takesSincePush + 1, StoppedAfterTakes);
			pushedSinceTake = false;
			bool sending = takesSincePush < StoppedAfterTakes;
			ranDry &= sending;
			if (!playing)
			{
				if (count == 0 || (count < PrefillSamples && sending))
				{
					return false;
				}
				playing = true;
				level = count;
			}
			if (sending)
			{
				minLevel = Math.Min(minLevel, count);
				maxLevel = Math.Max(maxLevel, count);
				Steer();
			}
			FillFrame(sending);
		}
		opus.Clear();
		encoder.Encode(frame, opus[..SpeakerReports.OpusFrameBytes]);
		return true;
	}

	public void Dispose() => encoder.Dispose();

	// Moves the resampling ratio so the averaged level heads for TargetSamples (PI control, called once per report).
	private void Steer()
	{
		level += (count - level) * LevelSmoothing;
		double error = (level - TargetSamples) / TargetSamples;
		integral = Math.Clamp(integral + error * IntegralGain, -MaxIntegral, MaxIntegral);
		resampler.Trim = Math.Clamp(error * ProportionalGain + integral, -MaxTrim, MaxTrim);
	}

	private void FillFrame(bool sending)
	{
		int available = Math.Min(count, FrameSamples);
		for (int i = 0; i < available; i++)
		{
			short left = ring[2 * head], right = ring[2 * head + 1];
			if (fadeIn > 0)
			{
				double gain = (double)(FadeSamples - fadeIn) / FadeSamples;
				left = (short)Math.Round(left * gain);
				right = (short)Math.Round(right * gain);
				fadeIn--;
			}
			frame[2 * i] = left;
			frame[2 * i + 1] = right;
			head = (head + 1) % MaxBufferedSamples;
		}
		count -= available;
		if (available < FrameSamples)
		{
			// Runs dry: fade out (over the last samples there are, or from the last sample sent when there are too few),
			// pad with silence, and let the next sound build its cushion again.
			playing = false;
			ranDry = sending;
			int end = available;
			if (available >= FadeSamples)
			{
				for (int i = 0; i < FadeSamples; i++)
				{
					int at = available - FadeSamples + i;
					double gain = (double)(FadeSamples - 1 - i) / FadeSamples;
					frame[2 * at] = (short)Math.Round(frame[2 * at] * gain);
					frame[2 * at + 1] = (short)Math.Round(frame[2 * at + 1] * gain);
				}
			}
			else
			{
				short fromLeft = available > 0 ? frame[2 * available - 2] : lastLeft;
				short fromRight = available > 0 ? frame[2 * available - 1] : lastRight;
				for (int i = 0; i < FadeSamples; i++)
				{
					double gain = (double)(FadeSamples - 1 - i) / FadeSamples;
					frame[2 * (available + i)] = (short)Math.Round(fromLeft * gain);
					frame[2 * (available + i) + 1] = (short)Math.Round(fromRight * gain);
				}
				end = available + FadeSamples;
			}
			frame.AsSpan(2 * end).Clear();
		}
		lastLeft = frame[^2];
		lastRight = frame[^1];
	}

	private void Input(ReadOnlySpan<byte> f)
	{
		short left = BinaryPrimitives.ReadInt16LittleEndian(f);
		short right = BinaryPrimitives.ReadInt16LittleEndian(f[2..]);
		if (!Headset)
		{
			left = right = (short)((left + right) / 2);
		}
		resampler.Process(left, right, Store);
	}

	private void Store(short left, short right)
	{
		if (count == MaxBufferedSamples)
		{
			head = (head + 1) % MaxBufferedSamples;
			count--;
			Interlocked.Increment(ref droppedSamples);
		}
		int at = (head + count) % MaxBufferedSamples;
		ring[2 * at] = left;
		ring[2 * at + 1] = right;
		count++;
	}
}
