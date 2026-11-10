// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Kopru.Core.Audio;

// Stereo linear-interpolation resampler for 16-bit audio with state kept across calls. The controller takes one 480-sample
// Opus frame per 10.67 ms report, i.e. 45 000 samples/s, so the game's 48 kHz audio is resampled 512 -> 480.
public sealed class StereoResampler(int inputRate, int outputRate)
{
	private readonly double step = (double)inputRate / outputRate;

	private double position; // of the next output sample, relative to the previous input sample

	private short lastLeft, lastRight;

	public void Reset()
	{
		position = 0;
		lastLeft = lastRight = 0;
	}

	public void Process(short left, short right, Action<short, short> output)
	{
		// Output samples fall between the previous input (at 0) and this one (at 1).
		while (position <= 1)
		{
			double t = position;
			output((short)Math.Round(lastLeft + (left - lastLeft) * t), (short)Math.Round(lastRight + (right - lastRight) * t));
			position += step;
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
public sealed class SpeakerAudioSender : IDisposable
{
	public const int InputChannels = 4;
	public const int InputRate = 48000;
	public const int OpusRate = 48000;
	public const int FrameSamples = 480;
	public const int ReportRate = 45000; // 480 samples every 32/3000 s
	public const int OpusBitrate = SpeakerReports.OpusFrameBytes * 8 * OpusRate / FrameSamples;
	public const int MaxBufferedSamples = FrameSamples * 6; // ~64 ms; older audio is dropped

	private const int FrameBytes = InputChannels * 2;

	private readonly IOpusEncoder encoder;

	private readonly StereoResampler resampler = new(InputRate, ReportRate);

	private readonly object gate = new();

	private readonly short[] ring = new short[MaxBufferedSamples * 2];

	private readonly short[] frame = new short[FrameSamples * 2];

	private readonly byte[] partial = new byte[FrameBytes];

	private int partialLength;

	private int head, count;

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

	// audio: int16 LE interleaved, 4 channels at 48 kHz (any chunking).
	public void Push(ReadOnlySpan<byte> audio)
	{
		lock (gate)
		{
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

	// Encodes the next 480 samples (silence where none are buffered) into opus (200 bytes); false when nothing was
	// buffered, so the caller can fall back to the haptics-only report.
	public bool TakeFrame(Span<byte> opus)
	{
		bool sound;
		lock (gate)
		{
			sound = count > 0;
			if (!sound)
			{
				return false;
			}
			for (int i = 0; i < FrameSamples; i++)
			{
				if (count > 0)
				{
					frame[2 * i] = ring[2 * head];
					frame[2 * i + 1] = ring[2 * head + 1];
					head = (head + 1) % MaxBufferedSamples;
					count--;
				}
				else
				{
					frame[2 * i] = frame[2 * i + 1] = 0;
				}
			}
		}
		opus.Clear();
		encoder.Encode(frame, opus[..SpeakerReports.OpusFrameBytes]);
		return true;
	}

	public void Dispose() => encoder.Dispose();

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
		}
		int at = (head + count) % MaxBufferedSamples;
		ring[2 * at] = left;
		ring[2 * at + 1] = right;
		count++;
	}
}
