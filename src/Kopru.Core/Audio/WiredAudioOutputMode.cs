// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Kopru.Core.Audio;

// How the wired render stream opens the controller's USB audio endpoint. Shared (the default) goes through the
// audio engine like any player. Exclusive bypasses the engine (no session volume, ducking or effects) but is not
// proven to play on the controller (hardware 2026-11-24: neither haptics nor speaker heard), so it is opt-in.
public enum WiredAudioMode
{
	Exclusive,
	Shared
}

// The mode choice: the environment override (no rebuild needed to compare both) and the fallback to shared mode when
// exclusive cannot be opened (the endpoint is in use, exclusive control is not allowed, no format fits).
public static class WiredAudioModeChoice
{
	public const string EnvironmentVariable = "KOPRU_WIRED_AUDIO_MODE";

	// "shared" or "exclusive" (any case, trimmed); anything else, or nothing, is shared. Note: why, for the open log
	// line, when the value was set.
	public static (WiredAudioMode Mode, string? Note) Parse(string? value)
	{
		string v = (value ?? string.Empty).Trim();
		if (v.Length == 0)
		{
			return (WiredAudioMode.Shared, null);
		}
		if (v.Equals("shared", StringComparison.OrdinalIgnoreCase))
		{
			return (WiredAudioMode.Shared, EnvironmentVariable + "=shared");
		}
		if (v.Equals("exclusive", StringComparison.OrdinalIgnoreCase))
		{
			return (WiredAudioMode.Exclusive, EnvironmentVariable + "=exclusive");
		}
		return (WiredAudioMode.Shared, EnvironmentVariable + " value not understood, using shared");
	}

	public static (WiredAudioMode Mode, string? Note) FromEnvironment() =>
		Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

	// Opens in the wanted mode; exclusive falls back to shared on any failure. how: the mode in use and why, for the
	// open log line (no exception messages: they may carry paths).
	public static T Open<T>(WiredAudioMode wanted, string? note, Func<T> exclusive, Func<T> shared, out string how)
	{
		string why = note is null ? string.Empty : " (" + note + ")";
		// Shared without a note: the plain default.
		if (wanted == WiredAudioMode.Shared)
		{
			T s = shared();
			how = "shared mode" + why;
			return s;
		}
		string failure;
		try
		{
			T e = exclusive();
			how = "exclusive mode" + why;
			return e;
		}
		catch (Exception e)
		{
			failure = WiredAudio.Describe(e);
		}
		T fallback = shared();
		how = "shared mode, exclusive failed (" + failure + ")" + (note is null ? string.Empty : "; " + note);
		return fallback;
	}
}

// The sample layouts the wired render stream writes.
public enum WiredSampleFormat
{
	Float32,
	Pcm16,
	// 24-bit packed in 3 bytes.
	Pcm24,
	// 24 or 32 valid bits in a 32-bit container (left-justified, so the same scaling).
	Pcm32
}

// One exclusive-mode format candidate: 48 kHz, the endpoint's channels and mask, WAVEFORMATEXTENSIBLE.
public readonly record struct WiredExclusiveFormat(WiredSampleFormat Sample, int Channels, int ContainerBits, int ValidBits, uint ChannelMask)
{
	public const int Size = 40;
	private const ushort FormatExtensible = 0xFFFE;
	private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");
	private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");

	public int BlockAlign => Channels * ContainerBits / 8;

	public string Name => Sample switch
	{
		WiredSampleFormat.Float32 => "float",
		WiredSampleFormat.Pcm16 => "pcm16",
		WiredSampleFormat.Pcm24 => "pcm24",
		_ => ValidBits == 32 ? "pcm32" : "pcm24in32"
	};

	// The WAVEFORMATEXTENSIBLE bytes (little endian, 40 bytes).
	public byte[] ToBytes()
	{
		byte[] b = new byte[Size];
		Span<byte> s = b;
		BinaryPrimitives.WriteUInt16LittleEndian(s, FormatExtensible);
		BinaryPrimitives.WriteUInt16LittleEndian(s[2..], (ushort)Channels);
		BinaryPrimitives.WriteUInt32LittleEndian(s[4..], WiredAudioFrames.Rate);
		BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)(WiredAudioFrames.Rate * BlockAlign));
		BinaryPrimitives.WriteUInt16LittleEndian(s[12..], (ushort)BlockAlign);
		BinaryPrimitives.WriteUInt16LittleEndian(s[14..], (ushort)ContainerBits);
		BinaryPrimitives.WriteUInt16LittleEndian(s[16..], 22);
		BinaryPrimitives.WriteUInt16LittleEndian(s[18..], (ushort)ValidBits);
		BinaryPrimitives.WriteUInt32LittleEndian(s[20..], ChannelMask);
		(Sample == WiredSampleFormat.Float32 ? SubtypeFloat : SubtypePcm).TryWriteBytes(s[24..]);
		return b;
	}

	// The candidates in order: 16-bit PCM first (the controller's own default format), then the wider ones.
	public static IReadOnlyList<WiredExclusiveFormat> Candidates(int channels, uint mask) =>
	[
		new(WiredSampleFormat.Pcm16, channels, 16, 16, mask),
		new(WiredSampleFormat.Pcm24, channels, 24, 24, mask),
		new(WiredSampleFormat.Pcm32, channels, 32, 24, mask),
		new(WiredSampleFormat.Pcm32, channels, 32, 32, mask),
		new(WiredSampleFormat.Float32, channels, 32, 32, mask),
	];

	// A buffer duration (100 ns units) for a frame count, rounded as the documentation's alignment example does.
	public static long Duration(uint frames) => (long)(10_000_000.0 * frames / WiredAudioFrames.Rate + 0.5);
}

public static class WiredAudioSamples
{
	public static int BytesPerSample(WiredSampleFormat format) => format switch
	{
		WiredSampleFormat.Pcm16 => 2,
		WiredSampleFormat.Pcm24 => 3,
		_ => 4
	};

	// Float samples (-1..1) into the endpoint's layout; destination holds exactly data.Length samples.
	public static void Write(ReadOnlySpan<float> data, Span<byte> destination, WiredSampleFormat format)
	{
		switch (format)
		{
			case WiredSampleFormat.Float32:
				MemoryMarshal.AsBytes(data).CopyTo(destination);
				break;
			case WiredSampleFormat.Pcm16:
				for (int i = 0; i < data.Length; i++)
				{
					BinaryPrimitives.WriteInt16LittleEndian(destination[(2 * i)..], (short)Math.Clamp(data[i] * 32767f, -32768f, 32767f));
				}
				break;
			case WiredSampleFormat.Pcm24:
				for (int i = 0; i < data.Length; i++)
				{
					int v = (int)Math.Clamp(data[i] * 8388607.0, -8388608.0, 8388607.0);
					destination[3 * i] = (byte)v;
					destination[3 * i + 1] = (byte)(v >> 8);
					destination[3 * i + 2] = (byte)(v >> 16);
				}
				break;
			default:
				for (int i = 0; i < data.Length; i++)
				{
					BinaryPrimitives.WriteInt32LittleEndian(destination[(4 * i)..], (int)Math.Clamp(data[i] * 2147483647.0, -2147483648.0, 2147483647.0));
				}
				break;
		}
	}
}
