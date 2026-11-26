// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Kopru.Core.Audio;
using Xunit;

namespace Kopru.Core.Tests;

// The wired render stream's mode (exclusive by default, KOPRU_WIRED_AUDIO_MODE override, shared fallback), its
// exclusive format candidates and its sample writers.
public class WiredAudioModeTests
{
	[Theory]
	[InlineData(null, WiredAudioMode.Shared, null)]
	[InlineData("", WiredAudioMode.Shared, null)]
	[InlineData("  ", WiredAudioMode.Shared, null)]
	[InlineData("exclusive", WiredAudioMode.Exclusive, "KOPRU_WIRED_AUDIO_MODE=exclusive")]
	[InlineData(" Shared ", WiredAudioMode.Shared, "KOPRU_WIRED_AUDIO_MODE=shared")]
	[InlineData("SHARED", WiredAudioMode.Shared, "KOPRU_WIRED_AUDIO_MODE=shared")]
	[InlineData("loud", WiredAudioMode.Shared, "KOPRU_WIRED_AUDIO_MODE value not understood, using shared")]
	public void Parse_DefaultsToShared_ExclusiveOnlyWhenAsked(string? value, WiredAudioMode mode, string? note)
	{
		Assert.Equal((mode, note), WiredAudioModeChoice.Parse(value));
	}

	[Fact]
	public void Open_Exclusive_UsesExclusive_AndNeverOpensShared()
	{
		bool sharedOpened = false;
		string result = WiredAudioModeChoice.Open(WiredAudioMode.Exclusive, null, () => "ex", () =>
		{
			sharedOpened = true;
			return "sh";
		}, out string how);
		Assert.Equal("ex", result);
		Assert.Equal("exclusive mode", how);
		Assert.False(sharedOpened);
	}

	[Fact]
	public void Open_ExclusiveFailing_FallsBackToShared_AndSaysWhy_WithoutTheMessage()
	{
		string result = WiredAudioModeChoice.Open<string>(WiredAudioMode.Exclusive, "KOPRU_WIRED_AUDIO_MODE=exclusive",
			() => throw new COMException("C:\\Users\\someone\\device", unchecked((int)0x8889000A)), () => "sh", out string how);
		Assert.Equal("sh", result);
		Assert.Equal("shared mode, exclusive failed (0x8889000A); KOPRU_WIRED_AUDIO_MODE=exclusive", how);
		WiredAudioModeChoice.Open<string>(WiredAudioMode.Exclusive, null, () => throw new NotSupportedException("x"), () => "sh", out how);
		Assert.Equal("shared mode, exclusive failed (NotSupportedException)", how);
	}

	[Fact]
	public void Open_SharedAsked_NeverTriesExclusive()
	{
		bool exclusiveTried = false;
		WiredAudioModeChoice.Open(WiredAudioMode.Shared, "KOPRU_WIRED_AUDIO_MODE=shared", () =>
		{
			exclusiveTried = true;
			return "ex";
		}, () => "sh", out string how);
		Assert.False(exclusiveTried);
		Assert.Equal("shared mode (KOPRU_WIRED_AUDIO_MODE=shared)", how);
	}

	[Fact]
	public void Candidates_Start_With16BitPcm_OnTheEndpointsChannelsAndMask()
	{
		IReadOnlyList<WiredExclusiveFormat> all = WiredExclusiveFormat.Candidates(4, 0x33);
		Assert.Equal(["pcm16", "pcm24", "pcm24in32", "pcm32", "float"], all.Select(f => f.Name));
		Assert.All(all, f => Assert.Equal((4, 0x33u), (f.Channels, f.ChannelMask)));
	}

	[Fact]
	public void ExclusiveFormat_Bytes_AreAWaveFormatExtensible()
	{
		byte[] b = WiredExclusiveFormat.Candidates(4, 0x33)[0].ToBytes();
		Assert.Equal(40, b.Length);
		Assert.Equal(0xFFFE, BinaryPrimitives.ReadUInt16LittleEndian(b));
		Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2)));
		Assert.Equal(48000u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
		Assert.Equal(48000u * 8, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)));
		Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(12)));
		Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(14)));
		Assert.Equal(22, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(16)));
		Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(18)));
		Assert.Equal(0x33u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(20)));
		Assert.Equal(new Guid("00000001-0000-0010-8000-00AA00389B71"), new Guid(b.AsSpan(24, 16)));

		byte[] f = WiredExclusiveFormat.Candidates(4, 0x33)[^1].ToBytes();
		Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(12)));
		Assert.Equal(new Guid("00000003-0000-0010-8000-00AA00389B71"), new Guid(f.AsSpan(24, 16)));
		byte[] p24 = WiredExclusiveFormat.Candidates(4, 0x33)[2].ToBytes();
		Assert.Equal((32, 24), (BinaryPrimitives.ReadUInt16LittleEndian(p24.AsSpan(14)), BinaryPrimitives.ReadUInt16LittleEndian(p24.AsSpan(18))));
	}

	[Fact]
	public void Duration_OfAlignedFrames_RoundsTo100NsUnits()
	{
		Assert.Equal(100_000, WiredExclusiveFormat.Duration(480));
		Assert.Equal(1_000_000, WiredExclusiveFormat.Duration(4800));
		Assert.Equal(106_667, WiredExclusiveFormat.Duration(512));
	}

	[Fact]
	public void Samples_AreWrittenInEachLayout_Clamped()
	{
		float[] data = [0f, 0.5f, -1f, 2f];
		byte[] f32 = new byte[16];
		WiredAudioSamples.Write(data, f32, WiredSampleFormat.Float32);
		Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(f32.AsSpan(4)));

		byte[] p16 = new byte[8];
		WiredAudioSamples.Write(data, p16, WiredSampleFormat.Pcm16);
		Assert.Equal([0, 16383, -32767, 32767], Enumerable.Range(0, 4).Select(i => (int)BinaryPrimitives.ReadInt16LittleEndian(p16.AsSpan(2 * i))));

		byte[] p24 = new byte[12];
		WiredAudioSamples.Write(data, p24, WiredSampleFormat.Pcm24);
		static int Read24(byte[] b, int i) => (b[3 * i] | (b[3 * i + 1] << 8) | (b[3 * i + 2] << 16)) << 8 >> 8;
		Assert.Equal([0, 4194303, -8388607, 8388607], Enumerable.Range(0, 4).Select(i => Read24(p24, i)));

		byte[] p32 = new byte[16];
		WiredAudioSamples.Write(data, p32, WiredSampleFormat.Pcm32);
		Assert.Equal(int.MaxValue, BinaryPrimitives.ReadInt32LittleEndian(p32.AsSpan(12)));
		Assert.Equal(-int.MaxValue, BinaryPrimitives.ReadInt32LittleEndian(p32.AsSpan(8)));

		Assert.Equal([4, 2, 3, 4], new[] { WiredSampleFormat.Float32, WiredSampleFormat.Pcm16, WiredSampleFormat.Pcm24, WiredSampleFormat.Pcm32 }
			.Select(WiredAudioSamples.BytesPerSample));
	}
}
