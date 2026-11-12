// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using Concentus.Enums;
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Lighting;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;


// Counts packets; every packet decodes to 480 samples of its first byte * 100.
internal sealed class FakeMicDecoder : IMicDecoder
{
	public int Decoded;

	public int Decode(ReadOnlySpan<byte> packet, Span<short> pcm)
	{
		Decoded++;
		pcm[..480].Fill((short)(packet[0] * 100));
		return 480;
	}

	public int Conceal(Span<short> pcm)
	{
		pcm[..480].Clear();
		return 480;
	}

	public void Dispose()
	{
	}
}

public class MicPacketTests
{
	private static byte[] Sine(int samples, double hz, double amplitude = 0.4)
	{
		short[] pcm = new short[samples];
		for (int i = 0; i < samples; i++)
		{
			pcm[i] = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * hz * i / 48000));
		}
		byte[] bytes = new byte[samples * 2];
		Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
		return bytes;
	}

	// A gamepad input report as the controller sends it over Bluetooth: byte 1 = sequence << 4 | type 1.
	internal static byte[] RealInput(byte sequence, byte leftX = 0x80)
	{
		byte[] report = FakeLink.Input(0, sequence);
		report[1] = (byte)((sequence << 4) | MicPackets.InputType);
		report[2] = leftX;
		BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), Crc.Compute(Crc.InputSeed, report.AsSpan(0, 74)));
		return report;
	}

	[Fact]
	public void Report_RoundTripsThroughTheMeasuredLayout()
	{
		byte[] opus = [MicPackets.Marker, 2, 3, 4, 5];
		byte[] report = MicPackets.Build(opus, sequence: 0xA, counter: 0x91);
		Assert.Equal(78, report.Length);
		Assert.Equal(0x31, report[0]);
		Assert.Equal(0xA2, report[1]);
		Assert.Equal(0x91, report[2]);
		Assert.Equal(0xD4, report[3]);
		Assert.True(BluetoothReports.InputCrcValid(report)); // like the hardware: a valid input CRC
		Assert.True(MicPackets.IsMicReport(report));
		Assert.True(MicPackets.TryGetOpus(report, out ReadOnlySpan<byte> back));
		Assert.Equal(MicPackets.MaxPayload, back.Length);
		Assert.Equal(71, back.Length);
		Assert.Equal(opus, back[..opus.Length].ToArray());
		Assert.All(back[opus.Length..].ToArray(), b => Assert.Equal(0, b));
		Assert.Throws<ArgumentException>(() => MicPackets.Build(new byte[MicPackets.MaxPayload + 1]));
	}

	// Every sequence value and every first body byte: type 1 is never mic, type 2 always is (whatever byte 3 holds).
	[Fact]
	public void TypeNibble_AloneTellsMicFromInput()
	{
		for (int sequence = 0; sequence < 16; sequence++)
		{
			for (int x = 0; x < 256; x++)
			{
				byte[] input = RealInput((byte)sequence, (byte)x);
				input[3] = MicPackets.Marker; // even a gamepad report with 0xD4 where the mic TOC sits
				Assert.False(MicPackets.IsMicReport(input));
				Assert.False(MicPackets.TryGetOpus(input, out _));
			}
			byte[] mic = MicPackets.Build([0x12], (byte)sequence);
			mic[3] = 0x00;
			Assert.True(MicPackets.IsMicReport(mic));
		}
		Assert.False(MicPackets.IsMicReport(FakeLink.Input(10, 1)));
		Assert.False(MicPackets.TryGetOpus(MicPackets.Build([9]).AsSpan(0, 40), out _)); // truncated
	}

	// CBR 71-byte CELT packets in the report, as the controller sends them.
	[Fact]
	public void ConcentusDecoder_DecodesRealOpusMono()
	{
#pragma warning disable CS0618
		var encoder = new Concentus.Structs.OpusEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY)
		{
			Bitrate = MicPackets.MaxPayload * 8 * 100,
			UseVBR = false
		};
#pragma warning restore CS0618
		using ConcentusMicDecoder decoder = new();
		short[] pcm = new short[480];
		Buffer.BlockCopy(Sine(480, 1000), 0, pcm, 0, 960);
		byte[] packet = new byte[MicPackets.MaxPayload];
		short[] output = new short[5760];
		double energy = 0;
		for (int f = 0; f < 10; f++)
		{
			int n = encoder.Encode(pcm, 480, packet, packet.Length);
			Assert.Equal(MicPackets.MaxPayload, n);
			// Through the report layout and the pipeline's parser.
			Assert.True(MicPackets.TryGetOpus(MicPackets.Build(packet.AsSpan(0, n), (byte)f, (byte)f), out ReadOnlySpan<byte> opus));
			Assert.Equal(480, decoder.Decode(opus, output));
			energy = output.Take(480).Max(s => Math.Abs((int)s));
		}
		Assert.True(energy > 5000, $"peak {energy}");
		Assert.Equal(480, decoder.Conceal(output));
	}
	[Fact]
	public void MonoResampler_ConvertsRates()
	{
		MonoResampler up = new(16000, 48000);
		List<short> output = [];
		up.Process(Enumerable.Repeat((short)1000, 160).ToArray(), output);
		Assert.InRange(output.Count, 478, 482);
		Assert.All(output.Skip(3), s => Assert.Equal(1000, s));

		MonoResampler same = new(48000, 48000);
		List<short> copy = [];
		same.Process([0, 10, 20, 30], copy);
		Assert.Equal([0, 0, 10, 20, 30], copy); // one sample of start-up delay
	}

	[Fact]
	public void JitterBuffer_Prefills_DropsOldest_AndRePrimesAfterUnderrun()
	{
		MicJitterBuffer buffer = new(prefill: 10, capacity: 20);
		short[] out5 = new short[5];
		buffer.Write([1, 2, 3, 4, 5]);
		Assert.Equal(0, buffer.Read(out5)); // still priming
		Assert.All(out5, s => Assert.Equal(0, s));
		buffer.Write([6, 7, 8, 9, 10]);
		Assert.Equal(5, buffer.Read(out5));
		Assert.Equal([1, 2, 3, 4, 5], out5);

		short[] out8 = new short[8];
		Assert.Equal(5, buffer.Read(out8)); // underrun: rest is silence
		Assert.Equal([6, 7, 8, 9, 10, 0, 0, 0], out8);
		Assert.Equal(1, buffer.Underruns);
		buffer.Write([1, 1, 1]);
		Assert.Equal(0, buffer.Read(out5)); // priming again

		buffer.Write(Enumerable.Range(0, 30).Select(i => (short)i).ToArray());
		Assert.Equal(20, buffer.Buffered);
		Assert.Equal(13, buffer.Dropped);
	}

	[Fact]
	public void Pipeline_DecodesToBothChannels_WithGain()
	{
		FakeMicDecoder decoder = new();
		using MicPipeline pipeline = new(decoder, new MicJitterBuffer(prefill: 48));
		pipeline.Gain = 2;
		pipeline.PushReport(MicPackets.Build([10]));
		pipeline.PushReport(FakeLink.Input(0, 0)); // gamepad input: ignored
		Assert.Equal(1, decoder.Decoded);
		Assert.Equal(1, pipeline.Packets);

		byte[] pcm = new byte[VirtualDualSense.MicBytesPerFrame];
		pipeline.Fill(pcm, VirtualDualSense.MicChannels);
		for (int i = 0; i < 48; i++)
		{
			Assert.Equal(2000, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(4 * i)));
			Assert.Equal(2000, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(4 * i + 2)));
		}
		Assert.InRange(pipeline.Level, 0.06, 0.07);
	}

	[Fact]
	public void Pipeline_NoiseGate_SilencesFaintSound_AfterTheHold_ButNotSpeech()
	{
		using MicPipeline pipeline = new(null) { NoiseGate = true, Source = new ConstantSource(50) };
		byte[] pcm = new byte[VirtualDualSense.MicBytesPerFrame];
		for (int i = 0; i < MicPipeline.GateHoldSamples / 48 + 1; i++)
		{
			pipeline.Fill(pcm, 2);
		}
		Assert.All(pcm, b => Assert.Equal(0, b));
		pipeline.Source = new ConstantSource(5000);
		pipeline.Fill(pcm, 2);
		Assert.Equal(5000, BinaryPrimitives.ReadInt16LittleEndian(pcm));
	}

	[Fact]
	public void TestTone_AndWav()
	{
		short[] tone = new short[480];
		new TestToneMicSource(1000, 0.5).Read(tone);
		Assert.InRange(tone.Max(), 16000, 16400);
		byte[] wav = WavWriter.Build(tone, 48000, 1);
		Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
		Assert.Equal(44 + 960, wav.Length);
		Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
	}

	private sealed class ConstantSource(short value) : IMicSource
	{
		public void Read(Span<short> mono) => mono.Fill(value);
	}
}

public class MicEndpointTests
{
	private static VirtualDualSense Pad() => new(DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration, [0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
		new Dictionary<int, string> { [1] = "S", [2] = "D" }, new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] }));

	[Fact]
	public void MicEndpoint_PlaysTheFeed_OnBothChannels()
	{
		using VirtualDualSense pad = Pad();
		using MicPipeline pipeline = new(null) { Source = new TestToneMicSource(1000, 0.5) };
		pad.Mic = pipeline;
		IsoPacket[] packets = Enumerable.Range(0, 4).Select(i => new IsoPacket(i * 196, 196, 0, 0)).ToArray();
		SubmitResult? result = null;
		pad.Submit(new SubmitCommand(3, 0, UsbIpCodes.DirIn, VirtualDualSense.MicInEndpoint, 0, 4 * 196, 0, 4, 1, new byte[8], [], packets), r => result = r);
		Assert.True(SpinWait.SpinUntil(() => result != null, 2000));
		Assert.Equal(4 * 192, result!.Data.Length);
		short[] left = Enumerable.Range(0, 192).Select(i => BinaryPrimitives.ReadInt16LittleEndian(result.Data.AsSpan(4 * i))).ToArray();
		short[] right = Enumerable.Range(0, 192).Select(i => BinaryPrimitives.ReadInt16LittleEndian(result.Data.AsSpan(4 * i + 2))).ToArray();
		Assert.Equal(left, right);
		Assert.True(left.Max() > 15000);
	}
}

public class MicButtonTests
{
	private static byte[] Body(bool down)
	{
		byte[] body = new byte[InputState.BodySize];
		body[MicButton.ButtonByte] = (byte)(down ? MicButton.ButtonBit | 0x01 : 0x01);
		return body;
	}

	[Fact]
	public void OnePressPerEdge_NotWhileHeld_NotWithinTheDebounce_AndTheBitIsAlwaysCleared()
	{
		StepClock clock = new();
		MicButton button = new(clock);
		int presses = 0;
		button.Pressed += () => presses++;

		byte[] down = Body(true);
		button.Process(down);
		Assert.Equal(1, presses);
		Assert.Equal(0x01, down[MicButton.ButtonByte]); // touchpad bit kept, mic bit gone
		button.Process(Body(true)); // held
		clock.Advance(TimeSpan.FromSeconds(2));
		button.Process(Body(true)); // still held
		Assert.Equal(1, presses);

		button.Process(Body(false));
		button.Process(Body(true));
		Assert.Equal(2, presses);

		// A bounce: released and pressed again within the debounce counts once.
		clock.Advance(TimeSpan.FromMilliseconds(100));
		button.Process(Body(false));
		button.Process(Body(true));
		Assert.Equal(2, presses);
		clock.Advance(MicButton.Debounce);
		button.Process(Body(false));
		button.Process(Body(true));
		Assert.Equal(3, presses);
	}
}

public class MicHostTests
{
	private readonly QueueControllers controllers = new();

	private readonly ConcurrentQueue<string> log = new();

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 3000), "timed out");

	private BridgeHost Host(TimeProvider? time = null) =>
		new(controllers, new FakeIdentity(DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
				[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00], new Dictionary<int, string> { [1] = "S", [2] = "D" },
				new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] })), new FakeAttacher(log), time)
		{
			PollInterval = TimeSpan.FromMilliseconds(10),
			MonitorInterval = TimeSpan.FromMilliseconds(10),
			NoInputTimeout = Timeout.InfiniteTimeSpan,
			SpeakerEncoderFactory = () => new FakeOpusEncoder(),
			MicDecoderFactory = () => new FakeMicDecoder()
		};

	private static byte[] Input(bool mute, byte sequence)
	{
		byte[] report = FakeLink.Input(0, sequence);
		if (mute)
		{
			report[2 + MicButton.ButtonByte] |= MicButton.ButtonBit;
		}
		BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), Crc.Compute(Crc.InputSeed, report.AsSpan(0, 74)));
		return report;
	}

	// The app's part in these tests: a press switches the controller mic ("Turn Off" mode with the controller mic selected).
	private static void ActLikeTheApp(BridgeHost host, ConcurrentQueue<bool> asked)
	{
		host.MicButtonPressed += () =>
		{
			bool on = !host.Mic.Enabled;
			asked.Enqueue(on);
			_ = Task.Run(() => host.ApplyMic(host.Mic with { Enabled = on }));
		};
	}

	private static Func<long> CountInputs(BridgeHost host)
	{
		long count = 0;
		host.RawInput += _ => Interlocked.Increment(ref count);
		return () => Interlocked.Read(ref count);
	}

	// Each press is reported (the app switches the mic), the LED is lit while the mic is on (no light setting needed) and
	// the stream asks for the mic while on.
	[Fact]
	public async Task MicButton_IsReported_TheAppSwitchesTheMic_LedFollows()
	{
		StepClock clock = new();
		await using BridgeHost host = Host(clock);
		ConcurrentQueue<bool> asked = new();
		ActLikeTheApp(host, asked);
		Func<long> inputs = CountInputs(host);
		FakeLink link = new();
		controllers.Links.Enqueue(link);
		host.ApplyMic(new MicSettings(false));
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);
		Assert.Null(host.MicPipeline);
		WaitUntil(() => link.Writes.Any(w => w[0] == 0x31 && (w[3 + OutputBlock.Flag1] & OutputBlock.MuteLedControl) != 0
			&& w[3 + OutputBlock.MuteLed] == OutputBlock.MuteLedOff));

		// Press: on.
		link.Feed(Input(false, 1));
		link.Feed(Input(true, 2));
		WaitUntil(() => host.Mic.Enabled && host.MicPipeline != null);
		Assert.Equal([true], asked);
		WaitUntil(() => link.Writes.Any(w => w[0] == BluetoothReports.HapticsId && w[MicPackets.StreamControlOffset] == MicPackets.StreamMicOn));
		byte[] stream = link.Writes.First(w => w[0] == BluetoothReports.HapticsId && w[MicPackets.StreamControlOffset] == MicPackets.StreamMicOn);
		Assert.Equal(Crc.Compute(Crc.OutputSeed, stream.AsSpan(0, 138)), BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(138)));
		WaitUntil(() => link.Writes.Last(w => w[0] == 0x31)[3 + OutputBlock.MuteLed] == OutputBlock.MuteLedOn);

		// Mic packets reach the pipeline (not the game as input).
		link.Feed(MicPackets.Build([7]));
		WaitUntil(() => host.MicPipeline!.Packets == 1);

		// Held, then a bounce within the debounce: nothing more.
		link.Feed(Input(true, 3));
		link.Feed(Input(false, 4));
		link.Feed(Input(true, 5));
		link.Feed(Input(false, 6));
		WaitUntil(() => inputs() == 6);
		Assert.Single(asked);
		clock.Advance(MicButton.Debounce);

		// Press: off.
		link.Feed(Input(true, 7));
		WaitUntil(() => !host.Mic.Enabled);
		Assert.Equal([true, false], asked);
		WaitUntil(() => link.Writes.Last(w => w[0] == 0x31)[3 + OutputBlock.MuteLed] == OutputBlock.MuteLedOff);

		// A selected PC microphone decides the LED (Live), whatever the controller mic does.
		host.ApplyMic(new MicSettings(false) { Live = true });
		WaitUntil(() => link.Writes.Last(w => w[0] == 0x31)[3 + OutputBlock.MuteLed] == OutputBlock.MuteLedOn);
		host.ApplyMic(new MicSettings(true) { Live = false });
		WaitUntil(() => link.Writes.Last(w => w[0] == 0x31)[3 + OutputBlock.MuteLed] == OutputBlock.MuteLedOff);

		// "Mute" mode: the stream stays, the pipeline feeds silence, the LED is dark.
		host.ApplyMic(new MicSettings(true, Muted: true));
		WaitUntil(() => host.MicPipeline is { Muted: true });
		Assert.False(host.Mic.IsLive);
		host.ApplyMic(new MicSettings(true));
		WaitUntil(() => host.MicPipeline is { Muted: false });
		WaitUntil(() => link.Writes.Last(w => w[0] == 0x31)[3 + OutputBlock.MuteLed] == OutputBlock.MuteLedOn);
	}

	private static void GameOutput(BridgeHost host, byte trigger, bool gameMuteLed)
	{
		byte[] block = new byte[OutputBlock.Size];
		// A game (or Windows / Steam on the virtual DualSense) setting the light bar and its own mute LED.
		OutputBlock.SetLightbar(block, 0x11, 0x22, 0x33);
		OutputBlock.SetMuteLed(block, gameMuteLed ? OutputBlock.MuteLedOn : OutputBlock.MuteLedOff);
		block[OutputBlock.RightTrigger] = trigger;
		byte[] report = new byte[UsbReports.OutputSize];
		UsbReports.BuildOutput(block, report);
		host.VirtualDualSense!.Submit(new SubmitCommand(5, 0, UsbIpCodes.DirOut, VirtualDualSense.HidOutEndpoint, 0, report.Length, 0, -1, 1,
			new byte[8], report, []), _ => { });
	}

	private static (bool Valid, byte Value) LastMuteLed(FakeLink link)
	{
		byte[] w = link.Writes.Last(w => w[0] == BluetoothReports.OutputId);
		return ((w[3 + OutputBlock.Flag1] & OutputBlock.MuteLedControl) != 0, w[3 + OutputBlock.MuteLed]);
	}

	// The owner's setup (0.1.4): the Windows default microphone is a PC (webcam) microphone, the button in "Kapat" (turn off)
	// mode, the controller microphone feature off, the light at its defaults (the old "Mikrofon LED'i: Kapalı" setting),
	// the bridge running with the virtual DualSense and output relayed (here with a light bar, so the light effect sender
	// pauses for it). Each press makes the app mute / unmute the PC microphone and push MicSettings.Live; the bytes going to
	// the controller must carry the mute LED (valid bit set) lit while that microphone is live and dark while muted, also
	// in the relayed output. Before the fix the LED value came from the light setting ("Off": always dark) and was only
	// written by the light sender, which also stops while a game drives the light.
	[Fact]
	public async Task OwnerSetup_PcMicrophone_TurnOffMode_MuteLedFollowsEveryPress()
	{
		ConcurrentQueue<string> lines = new();
		void OnLine(string line)
		{
			if (line.StartsWith("mute led", StringComparison.Ordinal))
			{
				lines.Enqueue(line);
			}
		}
		Kopru.Core.Diagnostics.AppLog.Written += OnLine;
		try
		{
			StepClock clock = new();
			await using BridgeHost host = Host(clock);
			FakeLink link = new();
			controllers.Links.Enqueue(link);
			host.ApplyLight(LightSettings.FromRgb(0x2F6BFF, 0.8));
			// PC microphone selected and not muted: MicEnabled false, Live true.
			host.ApplyMic(new MicSettings(false) { Live = true });
			await host.StartAsync();
			WaitUntil(() => host.State == BridgeState.Running);
			WaitUntil(() => LastMuteLed(link) == (true, OutputBlock.MuteLedOn));

			// Output relayed from the virtual pad, with the game's own (dark) mute LED: ours stays.
			GameOutput(host, 1, gameMuteLed: false);
			WaitUntil(() => link.Writes.Any(w => w[0] == BluetoothReports.OutputId && w[3 + OutputBlock.Red] == 0x11 && w[3 + OutputBlock.RightTrigger] == 1));
			Assert.Equal((true, OutputBlock.MuteLedOn), LastMuteLed(link));

			// Press: PC microphone muted.
			host.ApplyMic(new MicSettings(false) { Live = false });
			WaitUntil(() => LastMuteLed(link) == (true, OutputBlock.MuteLedOff));
			GameOutput(host, 2, gameMuteLed: true);
			WaitUntil(() => link.Writes.Any(w => w[0] == BluetoothReports.OutputId && w[3 + OutputBlock.RightTrigger] == 2));
			Assert.Equal((true, OutputBlock.MuteLedOff), LastMuteLed(link));

			// Press: unmuted again.
			host.ApplyMic(new MicSettings(false) { Live = true });
			WaitUntil(() => LastMuteLed(link) == (true, OutputBlock.MuteLedOn));
			GameOutput(host, 3, gameMuteLed: false);
			WaitUntil(() => link.Writes.Any(w => w[0] == BluetoothReports.OutputId && w[3 + OutputBlock.RightTrigger] == 3));
			Assert.Equal((true, OutputBlock.MuteLedOn), LastMuteLed(link));

			// The same value again writes nothing new and logs nothing.
			int writes = link.Writes.Count(w => w[0] == BluetoothReports.OutputId);
			host.ApplyMic(new MicSettings(false) { Live = true });
			Assert.Equal(writes, link.Writes.Count(w => w[0] == BluetoothReports.OutputId));

			// A light change (any effect) never touches it.
			host.ApplyLight(LightSettings.FromRgb(0xFF0000) with { Options = new LightOptions(LightMode.Rainbow, RainbowSpeed: 10) });
			await Task.Delay(100);
			Assert.Equal((true, OutputBlock.MuteLedOn), LastMuteLed(link));
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= OnLine;
		}
		// One line per change (other tests may log at the same time, so only presence is checked).
		Assert.Contains("mute led: on (mic settings: PC microphone live)", lines);
		Assert.Contains("mute led: off (mic settings: PC microphone muted)", lines);
	}

	// Low Power keeps the button (the app may still mute a PC microphone; it does not turn the controller mic on); the
	// other pad types get the button and nothing is reported; back on the virtual DualSense it is reported again.
	[Fact]
	public async Task MicButton_IsReportedUnderLowPower_NotWithAnotherPadType()
	{
		StepClock clock = new();
		await using BridgeHost host = Host(clock);
		ConcurrentQueue<int> asked = new();
		host.MicButtonPressed += () => asked.Enqueue(1);
		Func<long> inputs = CountInputs(host);
		FakeLink link = new();
		controllers.Links.Enqueue(link);
		host.ApplyMic(LowPower.Apply(new ControllerSettings { Mic = new MicSettings(true) }, true).Mic);
		Assert.True(host.Mic.Button);
		Assert.False(host.Mic.Enabled);
		await host.StartAsync();
		WaitUntil(() => host.State == BridgeState.Running);
		link.Feed(Input(false, 1));
		link.Feed(Input(true, 2));
		link.Feed(Input(false, 3));
		WaitUntil(() => inputs() == 3);
		WaitUntil(() => asked.Count == 1);
		clock.Advance(MicButton.Debounce);

		host.ApplyMic(new MicSettings(false));
		host.UsePad(Kopru.Core.Virtual.VirtualPadType.Xbox360);
		WaitUntil(() => log.Count(e => e.StartsWith("attach", StringComparison.Ordinal)) == 2);
		link.Feed(Input(true, 4));
		link.Feed(Input(false, 5));
		WaitUntil(() => inputs() == 5);
		clock.Advance(MicButton.Debounce);
		Assert.Single(asked);

		host.UsePad(Kopru.Core.Virtual.VirtualPadType.DualSense);
		WaitUntil(() => log.Count(e => e.StartsWith("attach", StringComparison.Ordinal)) == 3);
		link.Feed(Input(true, 6));
		WaitUntil(() => asked.Count == 2);
	}
	// Measured: a controller asked for its mic once keeps sending mic reports later, also into a session that never asked.
	// They are never gamepad input; while the mic is off they make the session send "mic off" (bounded).
	[Fact]
	public void MicReports_AreNeverInput_AndWhileOffTheSessionSendsMicOff()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		ConcurrentQueue<byte> inputs = new();
		session.InputBodyReceived += body => inputs.Enqueue(body[0]);
		int mic = 0;
		session.MicReportReceived += _ => Interlocked.Increment(ref mic);
		session.Start();

		// Never asked for: still not input; counted, and "mic off" goes out in stream reports.
		link.Feed(MicPackets.Build([MicPackets.Marker, 1], 3, 0));
		link.Feed(MicPacketTests.RealInput(4, 0x11));
		WaitUntil(() => inputs.Count == 1);
		Assert.Equal(0x11, inputs.Single());
		Assert.Equal(1, session.StrayMicReports);
		WaitUntil(() => session.MicOffSent == BluetoothSession.MicOffReports && !session.IsHapticsStreaming);
		Assert.Equal(BluetoothSession.MicOffReports, link.Writes.Count(w => w[0] == BluetoothReports.HapticsId));
		Assert.All(link.Writes.Where(w => w[0] == BluetoothReports.HapticsId), w => Assert.Equal(MicPackets.StreamMicOff, w[MicPackets.StreamControlOffset]));

		session.MicStreamEnabled = true;
		link.Feed(MicPackets.Build([MicPackets.Marker, 2], 5, 1));
		WaitUntil(() => mic == 1);
		// Long enough on that the stream's silence count is past SilentReportsBeforeStop (the old code stopped at once then).
		WaitUntil(() => link.Writes.Count(w => w[0] == BluetoothReports.HapticsId && w[MicPackets.StreamControlOffset] == MicPackets.StreamMicOn)
			> BluetoothSession.SilentReportsBeforeStop + 2);

		// Switched off: the controller must hear it. MicOffReports reports with the off value follow the last "on" one
		// (before the fix the stream stopped at once and sent none).
		long offBefore = session.MicOffSent;
		session.MicStreamEnabled = false;
		WaitUntil(() => session.MicOffSent == offBefore + BluetoothSession.MicOffReports && !session.IsHapticsStreaming);
		byte[][] stream = [.. link.Writes.Where(w => w[0] == BluetoothReports.HapticsId)];
		int lastOn = Array.FindLastIndex(stream, w => w[MicPackets.StreamControlOffset] == MicPackets.StreamMicOn);
		Assert.True(stream.Length - 1 - lastOn >= BluetoothSession.MicOffReports, $"{stream.Length - 1 - lastOn} off reports after the last on");
		Assert.All(stream.Skip(lastOn + 1), w => Assert.Equal(MicPackets.StreamMicOff, w[MicPackets.StreamControlOffset]));

		link.Feed(MicPackets.Build([MicPackets.Marker, 3], 6, 2));
		link.Feed(MicPacketTests.RealInput(7, 0x77));
		WaitUntil(() => inputs.Count == 2);
		Assert.Equal(0x77, inputs.Last());
		Assert.Equal(1, mic);
		Assert.Equal(2, session.StrayMicReports);
		Assert.Equal(1, session.MicReports);
	}

	// The probe's control run: mic reports are counted but nothing is sent.
	[Fact]
	public void StrayMic_WithStopStrayMicOff_SendsNothing()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link) { StopStrayMic = false };
		ConcurrentQueue<byte> inputs = new();
		session.InputBodyReceived += body => inputs.Enqueue(body[0]);
		session.Start();
		link.Feed(MicPackets.Build([MicPackets.Marker], 1, 0));
		link.Feed(MicPacketTests.RealInput(2, 0x22));
		WaitUntil(() => inputs.Count == 1);
		Assert.Equal(1, session.StrayMicReports);
		Assert.Equal(0, session.MicOffPending);
		Assert.DoesNotContain(link.Writes, w => w[0] == BluetoothReports.HapticsId);
	}
	// Once the mic was asked for, the host logs what came back after MicCheckDelay (mic reports, inputs, rejected ones).
	[Fact]
	public async Task MicOn_LogsOneCheck_WithWhatTheControllerSent()
	{
		ConcurrentQueue<string> lines = new();
		void Watch(string line)
		{
			if (line.StartsWith("mic check", StringComparison.Ordinal) && line.Contains("17 inputs", StringComparison.Ordinal))
			{
				lines.Enqueue(line);
			}
		}
		Kopru.Core.Diagnostics.AppLog.Written += Watch;
		try
		{
			ManualTime time = new();
			await using BridgeHost host = new(controllers, new FakeIdentity(DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration,
				[0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00], new Dictionary<int, string> { [1] = "S", [2] = "D" },
				new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] })), new FakeAttacher(log), time)
			{
				PollInterval = TimeSpan.FromMilliseconds(10),
				MonitorInterval = TimeSpan.FromMilliseconds(10),
				NoInputTimeout = Timeout.InfiniteTimeSpan,
				HapticsStallTimeout = Timeout.InfiniteTimeSpan,
				MicDecoderFactory = () => new FakeMicDecoder()
			};
			FakeLink link = new();
			controllers.Links.Enqueue(link);
			await host.StartAsync();
			WaitUntil(() => host.State == BridgeState.Running);

			host.ApplyMic(new MicSettings(true));
			for (byte i = 0; i < 17; i++)
			{
				link.Feed(Input(false, i));
			}
			link.Feed(MicPackets.Build([5]));
			link.Feed(MicPackets.Build([6]));
			byte[] broken = FakeLink.Input(0, 99);
			broken[74] ^= 0xFF;
			link.Feed(broken);
			WaitUntil(() => host.MicPipeline!.Packets == 2);
			Assert.Empty(lines);

			time.Advance(BridgeHost.MicCheckDelay);
			WaitUntil(() => !lines.IsEmpty);
			Assert.Contains("2 mic reports, 17 inputs, 1 rejected, decoded 2", lines.Single(), StringComparison.Ordinal);
		}
		finally
		{
			Kopru.Core.Diagnostics.AppLog.Written -= Watch;
		}
	}

	[Fact]
	public void Bridge_ConsumesTheMicButton_OnlyWhileItIsHandled()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = new(DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration, [0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
			new Dictionary<int, string> { [1] = "S", [2] = "D" }, new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] }));
		MicButton button = new();
		int presses = 0;
		button.Pressed += () => presses++;
		using ControllerBridge bridge = new(session, pad) { MicButton = button };
		session.Start();

		byte[] Next(byte[] report)
		{
			SubmitResult? result = null;
			pad.Submit(new SubmitCommand(9, 0, UsbIpCodes.DirIn, VirtualDualSense.HidInEndpoint, 0, 64, 0, -1, 1, new byte[8], [], []), r => result = r);
			link.Feed(report);
			WaitUntil(() => result != null);
			return result!.Data;
		}

		Assert.Equal(0, Next(Input(true, 1))[1 + MicButton.ButtonByte] & MicButton.ButtonBit);
		Assert.Equal(1, presses);
		bridge.MicButton = null;
		Assert.NotEqual(0, Next(Input(true, 2))[1 + MicButton.ButtonByte] & MicButton.ButtonBit);
	}

	[Theory]
	// The game sets its own mute LED, with and without the valid bit, and with the light switched off for games.
	[InlineData(OutputBlock.MuteLedOn, true, true)]
	[InlineData(OutputBlock.MuteLedOff, true, true)]
	[InlineData(OutputBlock.MuteLedOn, false, true)]
	[InlineData(OutputBlock.MuteLedOn, true, false)]
	public void Bridge_ReplacesTheGamesMuteLed_WithTheAppsOne(byte gameValue, bool gameValid, bool lightFromGame)
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = new(DeviceIdentity.Create(BridgeTests.Device, BridgeTests.Configuration, [0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
			new Dictionary<int, string> { [1] = "S", [2] = "D" }, new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] }));
		using ControllerBridge bridge = new(session, pad) { AppMuteLed = OutputBlock.MuteLedOff, LightFromGame = lightFromGame };
		void Send(byte trigger)
		{
			byte[] block = new byte[OutputBlock.Size];
			OutputBlock.SetMuteLed(block, gameValue);
			if (!gameValid)
			{
				block[OutputBlock.Flag1] &= unchecked((byte)~OutputBlock.MuteLedControl);
			}
			OutputBlock.SetLightbar(block, 1, 2, 3);
			block[OutputBlock.RightTrigger] = trigger;
			byte[] report = new byte[UsbReports.OutputSize];
			UsbReports.BuildOutput(block, report);
			pad.Submit(new SubmitCommand(5, 0, UsbIpCodes.DirOut, VirtualDualSense.HidOutEndpoint, 0, report.Length, 0, -1, 1, new byte[8], report, []), _ => { });
		}
		Send(1);
		WaitUntil(() => bridge.Outputs == 1);
		byte[] sent = link.Writes.Last(w => w[0] == 0x31);
		Assert.NotEqual(0, sent[3 + OutputBlock.Flag1] & OutputBlock.MuteLedControl);
		Assert.Equal(OutputBlock.MuteLedOff, sent[3 + OutputBlock.MuteLed]);

		bridge.AppMuteLed = OutputBlock.MuteLedOn;
		Send(2);
		WaitUntil(() => bridge.Outputs == 2);
		sent = link.Writes.Last(w => w[0] == 0x31);
		Assert.NotEqual(0, sent[3 + OutputBlock.Flag1] & OutputBlock.MuteLedControl);
		Assert.Equal(OutputBlock.MuteLedOn, sent[3 + OutputBlock.MuteLed]);

		// The game owns the button: its own mute LED goes through untouched.
		bridge.AppMuteLed = ControllerBridge.NoAppMuteLed;
		Send(3);
		WaitUntil(() => bridge.Outputs == 3);
		sent = link.Writes.Last(w => w[0] == 0x31);
		if (lightFromGame)
		{
			Assert.Equal(gameValid ? OutputBlock.MuteLedControl : 0, sent[3 + OutputBlock.Flag1] & OutputBlock.MuteLedControl);
			Assert.Equal(gameValue, sent[3 + OutputBlock.MuteLed]);
		}
	}
}
