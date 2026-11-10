// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Kopru.Core.Protocol;

namespace Kopru.Core.Audio;

// Bluetooth mic reports, layout MEASURED on hardware (Kopru.Probe mic, 2026-11-10; see docs/research/bt-mic.md):
//   [0]     0x31 (the gamepad input report id)
//   [1]     high nibble a sequence (like gamepad input), LOW nibble the report type: 1 = gamepad input, 2 = mic
//   [2]     a counter (+1 per mic report, wraps)
//   [3..73] one Opus packet of 71 bytes (CBR, no length byte); its TOC byte [3] was 0xD4 in every report seen
//           (CELT-only, 10 ms frame, stereo flag, one frame): 480 samples at 48 kHz per report, ~100 reports/s
//   [74..77] CRC-32 with the input seed, valid like gamepad input
// The type nibble alone tells mic from input: every one of ~12,500 gamepad reports had type 1, every mic report type 2.
// Byte [3] is not used to recognise a report (the encoder could pick another TOC); a bad packet just fails to decode.
public static class MicPackets
{
	public const byte ReportId = BluetoothReports.InputId;
	public const int TypeOffset = 1;
	public const byte TypeMask = 0x0F;
	public const byte InputType = 0x01;
	public const byte MicType = 0x02;
	public const int CounterOffset = 2;
	// The Opus TOC byte seen in every hardware report (CELT-only, 10 ms, stereo flag, one frame).
	public const byte Marker = 0xD4;
	public const int PayloadOffset = 3;
	public const int Size = BluetoothReports.InputSize;
	public const int CrcOffset = Size - 4;
	public const int MaxPayload = CrcOffset - PayloadOffset;

	// Byte 4 of reports 0x32 / 0x36 (the 0x91 stream-control sub-packet): 0xFE mic off (what we always sent), 0xFF mic on.
	public const int StreamControlOffset = 4;
	public const byte StreamMicOff = 0xFE;
	public const byte StreamMicOn = 0xFF;

	public const int SampleRate = 48000;
	public const int FrameSamples = 480; // 10 ms mono

	// A mic report rather than gamepad input: id 0x31 with type 2 in the low nibble of byte 1 (the CRC is not required;
	// a report that is neither goes to the input CRC check and is dropped there).
	public static bool IsMicReport(ReadOnlySpan<byte> report) =>
		report.Length > TypeOffset && report[0] == ReportId && (report[TypeOffset] & TypeMask) == MicType;

	// The Opus packet of a full-size mic report (always MaxPayload bytes).
	public static bool TryGetOpus(ReadOnlySpan<byte> report, out ReadOnlySpan<byte> opus)
	{
		opus = default;
		if (!IsMicReport(report) || report.Length < CrcOffset)
		{
			return false;
		}
		opus = report[PayloadOffset..CrcOffset];
		return true;
	}

	// Builds a report in the measured layout (tests and synthetic data); a shorter packet is zero-padded.
	public static byte[] Build(ReadOnlySpan<byte> opus, byte sequence = 0, byte counter = 0)
	{
		if (opus.Length == 0 || opus.Length > MaxPayload)
		{
			throw new ArgumentException($"Opus packet must be 1..{MaxPayload} bytes", nameof(opus));
		}
		byte[] report = new byte[Size];
		report[0] = ReportId;
		report[TypeOffset] = (byte)((sequence << 4) | MicType);
		report[CounterOffset] = counter;
		opus.CopyTo(report.AsSpan(PayloadOffset));
		BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(CrcOffset), Crc.Compute(Crc.InputSeed, report.AsSpan(0, CrcOffset)));
		return report;
	}

	// Asks for (or stops) the mic in a built 0x32 / 0x36 report; the caller fixes the CRC (see BluetoothSession).
	public static void SetStreamControl(Span<byte> report, bool micOn) =>
		report[StreamControlOffset] = micOn ? StreamMicOn : StreamMicOff;
}

public interface IMicDecoder : IDisposable
{
	// Decodes one Opus packet into mono 48 kHz samples; returns the sample count (FrameSamples for 10 ms).
	int Decode(ReadOnlySpan<byte> packet, Span<short> pcm);

	// Concealment for a lost packet (FrameSamples of best-guess audio).
	int Conceal(Span<short> pcm);
}

// Concentus (managed Opus, BSD-3-Clause) decoder, 48 kHz mono.
public sealed class ConcentusMicDecoder : IMicDecoder
{
	private readonly Concentus.Structs.OpusDecoder decoder;

	public ConcentusMicDecoder()
	{
#pragma warning disable CS0618 // the constructor, not the factory: never switches to a native library on its own
		decoder = new Concentus.Structs.OpusDecoder(MicPackets.SampleRate, 1);
#pragma warning restore CS0618
	}

	public int Decode(ReadOnlySpan<byte> packet, Span<short> pcm) => decoder.Decode(packet, pcm, Math.Min(pcm.Length, 5760), false);

	public int Conceal(Span<short> pcm) => decoder.Decode(ReadOnlySpan<byte>.Empty, pcm, Math.Min(pcm.Length, MicPackets.FrameSamples), false);

	public void Dispose()
	{
		// Managed state only.
	}
}
