// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;
using Xunit.Abstractions;

namespace Kopru.Core.Tests;

public class SpeakerEncoderTests(ITestOutputHelper output)
{
	private const int Frame = SpeakerAudioSender.FrameSamples;

	private static short[] Sine(int frames, double hz, double amplitude, int start = 0)
	{
		short[] pcm = new short[frames * 2];
		for (int i = 0; i < frames; i++)
		{
			short v = (short)Math.Round(amplitude * 32767 * Math.Sin(2 * Math.PI * hz * (start + i) / 48000));
			pcm[2 * i] = pcm[2 * i + 1] = v;
		}
		return pcm;
	}

#pragma warning disable CS0618 // the decoder constructor: managed on purpose, like the encoder
	private static Concentus.Structs.OpusDecoder Decoder() => new(48000, 2);
#pragma warning restore CS0618

	[Fact]
	public void Bitrate_FillsTheSlotExactly()
	{
		Assert.Equal(200, SpeakerReports.OpusFrameBytes);
		Assert.Equal(160_000, SpeakerAudioSender.OpusBitrate);
	}

	[Fact]
	public void EveryFrame_IsExactlyTheSlotSize_AndDecodes()
	{
		using ConcentusOpusEncoder encoder = new();
		var decoder = Decoder();
		byte[] packet = new byte[SpeakerReports.OpusFrameBytes];
		short[] decoded = new short[Frame * 2];
		for (int f = 0; f < 50; f++)
		{
			// Sound, silence and full-scale noise: CBR must keep the size in every case.
			short[] pcm = f < 20 ? Sine(Frame, 440, 0.5, f * Frame) : f < 35 ? new short[Frame * 2]
				: Enumerable.Range(0, Frame * 2).Select(i => (short)Random.Shared.Next(short.MinValue, short.MaxValue)).ToArray();
			Assert.Equal(SpeakerReports.OpusFrameBytes, encoder.Encode(pcm, packet));
			Assert.Equal(Frame, decoder.Decode(packet, decoded, Frame, false));
		}
	}

	[Fact]
	public void RoundTrip_SineKeepsAGoodSignalToNoiseRatio()
	{
		using ConcentusOpusEncoder encoder = new();
		var decoder = Decoder();
		const int frames = 100;
		short[] input = Sine(Frame * frames, 1000, 0.5);
		short[] result = new short[input.Length];
		byte[] packet = new byte[SpeakerReports.OpusFrameBytes];
		for (int f = 0; f < frames; f++)
		{
			encoder.Encode(input.AsSpan(f * Frame * 2, Frame * 2), packet);
			decoder.Decode(packet, result.AsSpan(f * Frame * 2, Frame * 2), Frame, false);
		}
		// The codec delays the signal by its look-ahead: compare at the best delay, skipping the start-up.
		double best = double.NegativeInfinity;
		for (int delay = 0; delay < 600; delay++)
		{
			double signal = 0, noise = 0;
			for (int i = Frame * 10; i < Frame * frames - 600; i++)
			{
				double x = input[2 * i];
				double y = result[2 * (i + delay)];
				signal += x * x;
				noise += (x - y) * (x - y);
			}
			best = Math.Max(best, 10 * Math.Log10(signal / Math.Max(noise, 1)));
		}
		output.WriteLine($"SNR {best:F1} dB");
		Assert.True(best > 20, $"SNR {best:F1} dB");
	}

	[Fact]
	public void Timing_TenSecondsEncodeWellUnderRealTime()
	{
		using ConcentusOpusEncoder encoder = new();
		const int frames = 1000; // 10 s
		short[] pcm = Sine(Frame * frames, 440, 0.4);
		byte[] packet = new byte[SpeakerReports.OpusFrameBytes];
		for (int f = 0; f < 20; f++)
		{
			encoder.Encode(pcm.AsSpan(f * Frame * 2, Frame * 2), packet); // warm-up (JIT)
		}
		Stopwatch clock = Stopwatch.StartNew();
		for (int f = 0; f < frames; f++)
		{
			encoder.Encode(pcm.AsSpan(f * Frame * 2, Frame * 2), packet);
		}
		double msPerFrame = clock.Elapsed.TotalMilliseconds / frames;
		output.WriteLine($"Concentus: {msPerFrame:F3} ms per 10 ms frame ({clock.Elapsed.TotalMilliseconds:F0} ms for 10 s)");
		Console.WriteLine($"[speaker encoder benchmark] Concentus {msPerFrame:F3} ms/frame");
		Assert.True(msPerFrame < 2.5, $"{msPerFrame:F3} ms per frame");
	}

	[Theory]
	[InlineData(150)]
	[InlineData(198)]
	[InlineData(199)]
	[InlineData(10)]
	public void Padding_FillsTheSlot_AndDecodesTheSameAudio(int bytes)
	{
		using ConcentusOpusEncoder small = new(bytes * 8 * 100);
		short[] pcm = Sine(Frame, 440, 0.5);
		byte[] packet = new byte[bytes];
		int length = small.Encode(pcm, packet);
		Assert.Equal(bytes, length);

		byte[] slot = new byte[SpeakerReports.OpusFrameBytes];
		packet.CopyTo(slot, 0);
		slot.AsSpan(bytes).Fill(0xEE);
		Assert.Equal(slot.Length, OpusPadding.Pad(slot, bytes));
		Assert.Equal(3, slot[0] & 0x03);

		short[] a = new short[Frame * 2], b = new short[Frame * 2];
		Assert.Equal(Frame, Decoder().Decode(packet, a, Frame, false));
		Assert.Equal(Frame, Decoder().Decode(slot, b, Frame, false));
		Assert.Equal(a, b);
	}

	[Fact]
	public void Padding_LeavesAFullPacketAlone()
	{
		byte[] slot = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();
		byte[] copy = slot.ToArray();
		Assert.Equal(200, OpusPadding.Pad(slot, 200));
		Assert.Equal(copy, slot);
	}

	[Fact]
	public void Factory_DefaultsToConcentus()
	{
		Assert.False(OpusEncoders.NativeRequested && OpusEncoders.NativeAvailable, "test machine asks for native Opus");
		using IOpusEncoder encoder = OpusEncoders.Create();
		Assert.IsType<ConcentusOpusEncoder>(encoder);
	}

	[Fact]
	public void Sender_WithConcentus_HandsOutDecodableFrames()
	{
		using SpeakerAudioSender sender = new(new ConcentusOpusEncoder());
		sender.Push(HapticsConverterTests.Audio(4800, ch1: HapticsConverterTests.Tone(440, 0.5), ch2: HapticsConverterTests.Tone(440, 0.5)));
		byte[] opus = new byte[SpeakerReports.OpusFrameBytes];
		short[] decoded = new short[Frame * 2];
		var decoder = Decoder();
		int frames = 0;
		while (sender.TakeFrame(opus))
		{
			Assert.Equal(Frame, decoder.Decode(opus, decoded, Frame, false));
			frames++;
		}
		Assert.Equal(SpeakerAudioSender.MaxBufferedSamples / Frame, frames); // the newest ~64 ms are kept
	}
}

// The bridge side of the controller speaker.
public partial class BridgeTests
{
	private static readonly IsoPacket[] TenMs = Enumerable.Range(0, 10).Select(i => new IsoPacket(i * 384, 384, 0, 0)).ToArray();

	private static void SendAudio(VirtualDualSense pad, byte[] audio) =>
		pad.Submit(Command(7, UsbIpCodes.DirOut, VirtualDualSense.AudioOutEndpoint, audio, TenMs), _ => { });

	[Fact]
	public void Speaker_Off_GameSpeakerChannelsAreNotSent_HapticsStillAre()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { GameHapticsLevel = 1.0 };
		session.Speaker = new SpeakerAudioSender(new FakeOpusEncoder());

		SendAudio(pad, HapticsConverterTests.Audio(480, ch1: HapticsConverterTests.Tone(440, 0.5), ch3: HapticsConverterTests.Tone(100, 0.5)));

		Assert.False(bridge.SpeakerEnabled);
		Assert.Equal(0, bridge.SpeakerBytes);
		Assert.Equal(0, session.Speaker!.Buffered);
		WaitUntil(() => link.Writes.Any(w => w[0] == 0x32 && w.AsSpan(13, 64).ToArray().Any(s => s != 0)));
		Assert.DoesNotContain(link.Writes, w => w[0] == SpeakerReports.ReportId);
	}

	[Fact]
	public void Speaker_On_Channels12GoToTheSpeaker_Channels34StayHaptics()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { GameHapticsLevel = 1.0, SpeakerEnabled = true };
		FakeOpusEncoder encoder = new();
		session.Speaker = new SpeakerAudioSender(encoder);

		byte[] audio = HapticsConverterTests.Audio(480, ch1: HapticsConverterTests.Tone(440, 0.5), ch2: HapticsConverterTests.Tone(440, 0.5),
			ch3: HapticsConverterTests.Tone(100, 0.5), ch4: HapticsConverterTests.Tone(100, 0.5));
		for (int i = 0; i < 5; i++)
		{
			SendAudio(pad, audio);
		}

		Assert.Equal(5 * audio.Length, bridge.SpeakerBytes);
		// Haptics ride in the speaker reports.
		WaitUntil(() => link.Writes.Any(w => w[0] == SpeakerReports.ReportId && w.AsSpan(13, 64).ToArray().Any(s => s != 0)));
		Assert.Contains(encoder.Frames.SelectMany(f => f), s => Math.Abs((int)s) > 1000); // the 440 Hz tone, not the haptics
	}

	[Fact]
	public void Speaker_OnlyHapticsChannels_SendNothingToTheSpeaker()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { SpeakerEnabled = true };
		session.Speaker = new SpeakerAudioSender(new FakeOpusEncoder());

		SendAudio(pad, HapticsConverterTests.Audio(480, ch3: HapticsConverterTests.Tone(100, 0.5), ch4: HapticsConverterTests.Tone(100, 0.5)));

		Assert.Equal(0, bridge.SpeakerBytes);
	}

	[Fact]
	public void Speaker_SilenceLongerThanTheTail_StopsSpeakerAudio_AndTheStreamFallsBackToHaptics()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { SpeakerEnabled = true };
		session.Speaker = new SpeakerAudioSender(new FakeOpusEncoder());

		SendAudio(pad, HapticsConverterTests.Audio(480, ch1: HapticsConverterTests.Tone(440, 0.5)));
		byte[] silence = HapticsConverterTests.Audio(480, ch3: HapticsConverterTests.Tone(100, 0.3));
		for (int i = 0; i < 40; i++) // 400 ms of speaker silence (haptics keep going)
		{
			SendAudio(pad, silence);
		}

		// The sound plus 200 ms of tail (21 chunks of 10 ms: the one that crosses 200 ms is still sent).
		int tailChunks = ControllerBridge.SpeakerTailFrames / 480 + 1;
		Assert.Equal((1 + tailChunks) * 480 * 8, bridge.SpeakerBytes);
		WaitUntil(() => link.Writes.Any(w => w[0] == SpeakerReports.ReportId));
		WaitUntil(() => session.Speaker!.Buffered == 0);
		// The last frame leaves the buffer before its report is written: wait until the stream (one thread, in order) has
		// written a haptics report after the last speaker report, so no speaker report is still on its way.
		WaitUntil(() =>
		{
			byte[][] writes = link.Writes.ToArray();
			int lastSpeaker = Array.FindLastIndex(writes, w => w[0] == SpeakerReports.ReportId);
			return Array.FindIndex(writes, lastSpeaker + 1, w => w[0] == BluetoothReports.HapticsId) >= 0;
		});
		long before = link.Writes.Count;
		SendAudio(pad, silence);
		WaitUntil(() => link.Writes.Skip((int)before).Any(w => w[0] == BluetoothReports.HapticsId));
		Assert.DoesNotContain(link.Writes.Skip((int)before), w => w[0] == SpeakerReports.ReportId);

		// Sound again: straight back to the speaker.
		SendAudio(pad, HapticsConverterTests.Audio(480, ch2: HapticsConverterTests.Tone(440, 0.5)));
		Assert.Equal((2 + tailChunks) * 480 * 8, bridge.SpeakerBytes);
	}

	[Fact]
	public void Speaker_On_GameOutputKeepsTheSpeakerRoute()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { SpeakerEnabled = true };
		session.Speaker = new SpeakerAudioSender(new FakeOpusEncoder()) { Volume = 1 };

		SendOutput(pad, b => OutputBlock.SetLightbar(b, 1, 2, 3));

		byte[] sent = Block(link.Writes.Last(w => w[0] == 0x31));
		Assert.Equal(AudioOutput.PathSpeaker, sent[AudioOutput.AudioControl] & AudioOutput.PathMask);
		Assert.Equal(AudioOutput.MaxSpeakerVolume, sent[AudioOutput.SpeakerVolume]);
		Assert.NotEqual(0, sent[OutputBlock.Flag0] & AudioOutput.AudioControlEnable);
	}
}

public class SpeakerSessionTests
{
	[Fact]
	public void Headphones_AreTrackedWithoutTheSpeaker_AndPickTheHeadphoneVolume()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		session.Start();
		byte[] input = FakeLink.Input(0, 1);
		input[2 + AudioOutput.StatusPlugged] |= 0x01;
		System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(74), Crc.Compute(Crc.InputSeed, input.AsSpan(0, 74)));
		link.Feed(input);
		Assert.True(SpinWait.SpinUntil(() => session.HeadphonesPlugged, 3000));

		session.Speaker = new SpeakerAudioSender(new FakeOpusEncoder()) { Volume = 1, HeadphoneVolume = 0.5 };

		Assert.True(session.Speaker.Headset);
		byte[] block = link.Writes.Last(w => w[0] == 0x31).AsSpan(3, OutputBlock.Size).ToArray();
		Assert.Equal(AudioOutput.PathHeadphones, block[AudioOutput.AudioControl] & AudioOutput.PathMask);
		Assert.Equal((byte)Math.Round(0.5 * AudioOutput.MaxHeadphoneVolume), block[AudioOutput.HeadphoneVolume]);
		Assert.Equal(0, block[AudioOutput.SpeakerVolume]);
	}
}

public class SpeakerHostTests
{
	private readonly QueueControllers controllers = new();

	private readonly ConcurrentQueue<string> log = new();

	private BridgeHost Host() =>
		new(controllers, new FakeIdentity(DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
				[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00], new Dictionary<int, string> { [1] = "S", [2] = "D" },
				new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] })), new FakeAttacher(log))
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
			SpeakerEncoderFactory = () => new FakeOpusEncoder()
		};

	[Fact]
	public async Task ApplySpeaker_TurnsTheSpeakerOnAndOff_WithTheVolumes()
	{
		await using BridgeHost host = Host();
		FakeLink link = new();
		controllers.Links.Enqueue(link);
		await host.StartAsync();
		Assert.True(SpinWait.SpinUntil(() => host.State == BridgeState.Running, 5000));
		Assert.Equal(SpeakerSettings.Off, host.Speaker);

		host.ApplySpeaker(new SpeakerSettings(true, 1, 0.5));
		byte[] on = link.Writes.Last(w => w[0] == 0x31).AsSpan(3, OutputBlock.Size).ToArray();
		Assert.Equal(AudioOutput.PathSpeaker, on[AudioOutput.AudioControl] & AudioOutput.PathMask);
		Assert.Equal(AudioOutput.MaxSpeakerVolume, on[AudioOutput.SpeakerVolume]);

		host.ApplySpeaker(new SpeakerSettings(true, 0, 0.5));
		byte[] quiet = link.Writes.Last(w => w[0] == 0x31).AsSpan(3, OutputBlock.Size).ToArray();
		Assert.Equal(0, quiet[AudioOutput.SpeakerVolume]);

		host.ApplySpeaker(SpeakerSettings.Off);
		byte[] off = link.Writes.Last(w => w[0] == 0x31).AsSpan(3, OutputBlock.Size).ToArray();
		Assert.Equal(0, off[OutputBlock.Flag0] & AudioOutput.AudioControlEnable);
		Assert.False(host.HeadphonesPlugged);
	}
}
