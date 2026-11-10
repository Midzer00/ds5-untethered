// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Concentus.Enums;

namespace Kopru.Core.Audio;

// The default speaker encoder: Concentus, a managed Opus implementation (BSD-3-Clause, no native library needed).
// 48 kHz stereo, 10 ms frames (480 samples per channel), CBR at the bitrate that fills SpeakerReports.OpusFrameBytes
// per frame, restricted low-delay (CELT only), complexity 5 for low CPU, no DTX, no in-band FEC.
public sealed class ConcentusOpusEncoder : IOpusEncoder
{
	public const int Complexity = 5;

	private readonly Concentus.Structs.OpusEncoder encoder;

	public ConcentusOpusEncoder(int bitrate = SpeakerAudioSender.OpusBitrate)
	{
		// The constructor (not OpusCodecFactory) on purpose: the factory may switch to a native libopus when one is found.
#pragma warning disable CS0618
		encoder = new Concentus.Structs.OpusEncoder(SpeakerAudioSender.OpusRate, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY)
		{
			Bitrate = bitrate,
			UseVBR = false,
			UseConstrainedVBR = false,
			Complexity = Complexity,
			UseDTX = false,
			UseInbandFEC = false,
			PacketLossPercent = 0,
			SignalType = OpusSignal.OPUS_SIGNAL_MUSIC
		};
#pragma warning restore CS0618
	}

	public int Encode(ReadOnlySpan<short> pcm, Span<byte> output)
	{
		int written = encoder.Encode(pcm, pcm.Length / 2, output, output.Length);
		if (written < 0)
		{
			throw new InvalidOperationException($"Opus encode failed ({written})");
		}
		return OpusPadding.Pad(output, written);
	}

	public void Dispose()
	{
		// Managed state only.
	}
}

// Fills an Opus packet up to its slot so the controller always gets exactly SpeakerReports.OpusFrameBytes. CBR normally
// fills the slot already; when it does not, the packet is padded the way RFC 6716 section 3.2.5 defines (what libopus'
// opus_packet_pad does): a one-frame packet (code 0) becomes a code 3 packet with a frame count byte, the padding length
// bytes (each 255 = 254 more padding bytes and another length byte follows) and zero padding after the frame. Decoders
// skip the padding, so the audio is unchanged. Anything that cannot be re-framed is left as is with zeros after it.
public static class OpusPadding
{
	// Returns the packet length now in packet (packet.Length when padded).
	public static int Pad(Span<byte> packet, int length)
	{
		int target = packet.Length;
		if (length <= 0 || length >= target || (packet[0] & 0x03) != 0)
		{
			packet[Math.Max(length, 0)..].Clear();
			return Math.Max(length, 0);
		}
		int extra = target - length; // >= 1
		Span<byte> frame = stackalloc byte[length - 1];
		packet[1..length].CopyTo(frame);
		packet[0] |= 0x03; // code 3: arbitrary number of frames
		if (extra == 1)
		{
			packet[1] = 0x01; // one frame, CBR, no padding
			frame.CopyTo(packet[2..]);
			return target;
		}
		packet[1] = 0x41; // one frame, padding flag
		int at = 2;
		int remaining = extra - 1; // length bytes plus padding bytes
		while (remaining > 255)
		{
			packet[at++] = 255;
			remaining -= 255;
		}
		packet[at++] = (byte)(remaining - 1);
		frame.CopyTo(packet[at..]);
		packet[(at + frame.Length)..].Clear();
		return target;
	}
}

// Picks the speaker encoder: Concentus unless KOPRU_OPUS=native asks for libopus and opus.dll can be loaded.
public static class OpusEncoders
{
	public const string EnvironmentVariable = "KOPRU_OPUS";

	public static bool NativeRequested => string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "native", StringComparison.OrdinalIgnoreCase);

	public static bool NativeAvailable
	{
		get
		{
			if (!NativeLibrary.TryLoad("opus", typeof(OpusEncoders).Assembly, null, out IntPtr handle))
			{
				return false;
			}
			NativeLibrary.Free(handle);
			return true;
		}
	}

	public static IOpusEncoder Create()
	{
		if (NativeRequested && NativeAvailable)
		{
			try
			{
				return new NativeOpusEncoder();
			}
			catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
			{
				// Fall back to the managed encoder.
			}
		}
		return new ConcentusOpusEncoder();
	}
}
