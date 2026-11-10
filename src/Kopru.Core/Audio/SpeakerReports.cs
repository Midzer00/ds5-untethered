// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Kopru.Core.Protocol;

namespace Kopru.Core.Audio;

// EXPERIMENTAL, not verified on hardware (see docs/research/bt-audio.md). Speaker / headset-jack audio over Bluetooth
// goes in output report 0x36 (398 B), which carries the same stream-control (0x11) and haptics (0x12) sub-packets as
// report 0x32 plus one Opus frame (48 kHz stereo, CBR, 200 B) in sub-packet 0x13 (speaker) or 0x16 (headset jack).
// While the speaker plays, 0x36 replaces 0x32 on the same 10.67 ms clock, so haptics keep playing.
public static class SpeakerReports
{
	public const byte ReportId = 0x36;
	public const int Size = 398;
	public const int CrcOffset = Size - 4;

	public const byte SpeakerPacket = 0x13;
	public const byte HeadsetPacket = 0x16;

	public const int OpusFrameBytes = 200;
	public const int OpusHeaderOffset = BluetoothReports.HapticsSamplesOffset + BluetoothReports.HapticsFrames * 2;
	public const int OpusOffset = OpusHeaderOffset + 2;

	public static void Build(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right, ReadOnlySpan<byte> opus, bool headset,
		byte sequence, byte counter, byte bufferDepth, Span<byte> report)
	{
		if (opus.Length != OpusFrameBytes)
		{
			throw new ArgumentException($"need an Opus frame of {OpusFrameBytes} bytes", nameof(opus));
		}
		if (report.Length < Size)
		{
			throw new ArgumentException($"need at least {Size} bytes", nameof(report));
		}
		// Bytes 0..76 are laid out exactly like report 0x32; only the id and the CRC position differ.
		Span<byte> haptics = stackalloc byte[BluetoothReports.HapticsSize];
		BluetoothReports.BuildHaptics(left, right, sequence, counter, bufferDepth, haptics);
		report.Clear();
		haptics[..OpusHeaderOffset].CopyTo(report);
		report[0] = ReportId;
		report[OpusHeaderOffset] = (byte)(0x80 | (headset ? HeadsetPacket : SpeakerPacket));
		report[OpusHeaderOffset + 1] = OpusFrameBytes;
		opus.CopyTo(report[OpusOffset..]);
		BinaryPrimitives.WriteUInt32LittleEndian(report[CrcOffset..], Crc.Compute(Crc.OutputSeed, report[..CrcOffset]));
	}
}

// Audio fields of the 47-byte output block (offsets and bits as documented by the Linux hid-playstation driver).
public static class AudioOutput
{
	public const int HeadphoneVolume = 4;
	public const int SpeakerVolume = 5;
	public const int MicVolume = 6;
	public const int AudioControl = 7;
	public const int AudioControl2 = 37;

	// Flag0
	public const byte HeadphoneVolumeEnable = 0x10; // uncertain: not named by the kernel driver
	public const byte SpeakerVolumeEnable = 0x20;
	public const byte MicVolumeEnable = 0x40;
	public const byte AudioControlEnable = 0x80;

	// Flag1
	public const byte AudioControl2Enable = 0x80;

	public const byte PathMask = 0x30;
	public const byte PathHeadphones = 0x00; // L+R to the jack, speaker muted
	public const byte PathSpeaker = 0x30;    // right channel to the speaker
	public const byte PreampMask = 0x07;

	public const byte MaxHeadphoneVolume = 0x7F;
	// The kernel driver reports the speaker reacting in about 0x3D..0x64; it uses 0x64 with preamp 2.
	public const byte MinSpeakerVolume = 0x3D;
	public const byte MaxSpeakerVolume = 0x64;
	public const byte DefaultPreamp = 2;

	// volume: 0..1 of the usable range for the chosen output; 0 mutes.
	public static void SetRoute(Span<byte> block, bool headset, double volume, byte preamp = DefaultPreamp)
	{
		volume = Math.Clamp(volume, 0, 1);
		block[OutputBlock.Flag0] |= AudioControlEnable | SpeakerVolumeEnable | HeadphoneVolumeEnable;
		block[OutputBlock.Flag1] |= AudioControl2Enable;
		block[AudioControl] = (byte)((block[AudioControl] & ~PathMask) | (headset ? PathHeadphones : PathSpeaker));
		block[AudioControl2] = (byte)((block[AudioControl2] & ~PreampMask) | (preamp & PreampMask));
		block[HeadphoneVolume] = headset ? (byte)Math.Round(volume * MaxHeadphoneVolume) : (byte)0;
		block[SpeakerVolume] = headset || volume == 0 ? (byte)0
			: (byte)Math.Round(MinSpeakerVolume + volume * (MaxSpeakerVolume - MinSpeakerVolume));
	}

	// Speaker and jack silent, audio fields back to "not set".
	public static void Clear(Span<byte> block)
	{
		block[OutputBlock.Flag0] &= unchecked((byte)~(AudioControlEnable | SpeakerVolumeEnable | HeadphoneVolumeEnable));
		block[OutputBlock.Flag1] &= unchecked((byte)~AudioControl2Enable);
		block[HeadphoneVolume] = block[SpeakerVolume] = block[AudioControl] = block[AudioControl2] = 0;
	}

	// Status byte 1 of the 63-byte input body (body[53]): bit 0 headphones, bit 1 microphone.
	public const int StatusPlugged = 53;

	public static bool HeadphonesPlugged(ReadOnlySpan<byte> body) => body.Length > StatusPlugged && (body[StatusPlugged] & 0x01) != 0;
}
