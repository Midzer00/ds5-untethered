// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Protocol;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

public class HapticsConverterTests
{
	// 4-channel 48 kHz int16 audio: each channel given as a function of the frame index (amplitude -1..1).
	internal static byte[] Audio(int frames, Func<int, double>? ch1 = null, Func<int, double>? ch2 = null, Func<int, double>? ch3 = null, Func<int, double>? ch4 = null)
	{
		byte[] audio = new byte[frames * 8];
		Func<int, double>?[] channels = [ch1, ch2, ch3, ch4];
		for (int f = 0; f < frames; f++)
		{
			for (int c = 0; c < 4; c++)
			{
				short v = (short)Math.Round((channels[c]?.Invoke(f) ?? 0) * 32767);
				BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(f * 8 + c * 2), v);
			}
		}
		return audio;
	}

	internal static Func<int, double> Tone(double hz, double amplitude) => f => amplitude * Math.Sin(2 * Math.PI * hz * f / 48000);

	private static List<(sbyte L, sbyte R)> Run(HapticsConverter converter, byte[] audio)
	{
		List<(sbyte, sbyte)> frames = [];
		converter.Convert(audio, (l, r) => frames.Add((l, r)));
		return frames;
	}

	[Fact]
	public void ToneOnChannel3_IsLeftOnly_AndSpeakerIgnored()
	{
		HapticsConverter converter = new() { Level = 1.0 };
		List<(sbyte L, sbyte R)> frames = Run(converter, Audio(4800, ch1: Tone(100, 0.9), ch2: Tone(300, 0.9), ch3: Tone(100, 0.5)));

		Assert.Equal(300, frames.Count);
		Assert.All(frames, f => Assert.Equal(0, f.R));
		int peak = frames.Skip(30).Max(f => Math.Abs((int)f.L));
		Assert.InRange(peak, 55, 64); // 0.5 x 127 through tanh, a little lost in the filter
	}

	[Fact]
	public void Level_Scales_AndStrongSignalsAreSoftLimited()
	{
		int Peak(double level, double amplitude) =>
			Run(new HapticsConverter { Level = level }, Audio(4800, ch4: Tone(100, amplitude))).Skip(30).Max(f => Math.Abs((int)f.R));

		int one = Peak(1.0, 0.1);
		int oneAndHalf = Peak(1.5, 0.1);
		Assert.InRange(oneAndHalf, one * 1.4, one * 1.55);

		int loud = Peak(3.0, 0.9);
		Assert.InRange(loud, 120, 127);
		Assert.True(Peak(1.5, 0.9) < loud);
	}

	[Fact]
	public void ChunksConvertLikeOnePiece()
	{
		byte[] audio = Audio(4800, ch3: Tone(100, 0.4), ch4: Tone(250, 0.3));
		List<(sbyte L, sbyte R)> whole = Run(new HapticsConverter(), audio);

		HapticsConverter split = new();
		List<(sbyte L, sbyte R)> pieces = [];
		Random random = new(7);
		for (int at = 0; at < audio.Length;)
		{
			int n = Math.Min(random.Next(1, 700), audio.Length - at);
			split.Convert(audio.AsSpan(at, n), (l, r) => pieces.Add((l, r)));
			at += n;
		}

		Assert.Equal(whole.Count, pieces.Count);
		for (int i = 0; i < whole.Count; i++)
		{
			Assert.InRange(pieces[i].L - whole[i].L, -1, 1);
			Assert.InRange(pieces[i].R - whole[i].R, -1, 1);
		}
	}

	[Fact]
	public void Silence_StaysZero_AndRateIsOneSixteenth()
	{
		List<(sbyte L, sbyte R)> frames = Run(new HapticsConverter(), Audio(1600, ch3: _ => 0.0001));
		Assert.Equal(100, frames.Count);
		Assert.All(frames, f => Assert.Equal((0, 0), ((int)f.L, (int)f.R)));
	}
}

public class BridgeTests
{
	internal static readonly byte[] Device = [18, 1, 0x00, 0x02, 0, 0, 0, 64, 0x4C, 0x05, 0xE6, 0x0C, 0x00, 0x01, 1, 2, 0, 1];

	internal static readonly byte[] Configuration =
	[
		9, 2, 59, 0, 3, 1, 0, 0xC0, 0xFA,
		9, 4, 0, 0, 0, 1, 1, 0, 0,
		9, 4, 1, 1, 1, 1, 2, 0, 0,
		7, 5, 0x01, 9, 0x88, 0x01, 4,
		9, 4, 3, 0, 2, 3, 0, 0, 0,
		9, 0x21, 0x11, 0x01, 0, 1, 0x22, 0x08, 0x00,
		7, 5, 0x84, 3, 64, 0, 6,
	];

	private static VirtualDualSense Pad() => new(DeviceIdentity.Create(Device, Configuration, [0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0xC0, 0x00],
		new Dictionary<int, string> { [1] = "Sony Interactive Entertainment", [2] = "DualSense Wireless Controller" },
		new Dictionary<byte, byte[]> { [0x05] = [0x05, 1, 2, 3] }));

	private static SubmitCommand Command(uint seq, uint direction, uint endpoint, byte[]? data = null, IsoPacket[]? packets = null) =>
		new(seq, 0, direction, endpoint, 0, data?.Length ?? 64, 0, packets?.Length ?? -1, 1, new byte[8], data ?? [], packets ?? []);

	private static void WaitUntil(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, 3000), "timed out");

	private static void SendOutput(VirtualDualSense pad, Action<byte[]> change)
	{
		byte[] block = new byte[OutputBlock.Size];
		change(block);
		byte[] report = new byte[UsbReports.OutputSize];
		UsbReports.BuildOutput(block, report);
		pad.Submit(Command(5, UsbIpCodes.DirOut, VirtualDualSense.HidOutEndpoint, report), _ => { });
	}

	private static byte[] Block(byte[] write) => write.AsSpan(3, OutputBlock.Size).ToArray();

	[Fact]
	public void Input_RawBodyReachesTheGame_ByteForByte()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		session.Start();

		byte[] bt = FakeLink.Input(77, 3);
		for (int i = 15; i < 50; i++)
		{
			bt[2 + i] = (byte)(i * 7); // gyro, accelerometer, touch
		}
		BinaryPrimitives.WriteUInt32LittleEndian(bt.AsSpan(74), Crc.Compute(Crc.InputSeed, bt.AsSpan(0, 74)));
		SubmitResult? result = null;
		pad.Submit(Command(9, UsbIpCodes.DirIn, VirtualDualSense.HidInEndpoint), r => result = r);
		link.Feed(bt);
		WaitUntil(() => result != null);
		Assert.Equal(64, result!.Data.Length);
		Assert.Equal(0x01, result.Data[0]);
		Assert.Equal(bt.AsSpan(2, 63).ToArray(), result.Data[1..]);
	}

	[Fact]
	public void Output_TriggersAndLightsReachTheController_RumbleNever_AndRepeatsAreSkipped()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		byte[] right = TriggerEffects.Feedback(2, 7);

		SendOutput(pad, b =>
		{
			OutputBlock.SetLightbar(b, 10, 20, 30);
			OutputBlock.SetTriggers(b, right, right);
			OutputBlock.SetRumble(b, 200, 100);
		});
		SendOutput(pad, b =>
		{
			OutputBlock.SetLightbar(b, 10, 20, 30);
			OutputBlock.SetTriggers(b, right, right);
			OutputBlock.SetRumble(b, 200, 100);
		});

		byte[][] writes = link.Writes.Where(w => w[0] == 0x31).ToArray();
		Assert.Single(writes);
		Assert.Equal(1, bridge.Outputs);
		byte[] sent = Block(writes[0]);
		Assert.Equal((10, 20, 30), (sent[OutputBlock.Red], sent[OutputBlock.Red + 1], sent[OutputBlock.Red + 2]));
		Assert.Equal(right, sent.AsSpan(OutputBlock.RightTrigger, right.Length).ToArray());
		Assert.Equal(right, sent.AsSpan(OutputBlock.LeftTrigger, right.Length).ToArray());
		Assert.Equal(0, sent[OutputBlock.Flag0] & 0x03);
		Assert.Equal((0, 0), (sent[OutputBlock.MotorLeft], sent[OutputBlock.MotorRight]));
		Assert.Equal(((byte)200, (byte)100), bridge.Motors);
	}

	[Fact]
	public void Rumble_PlaysAsHaptics_UntilTheMotorsStop()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);

		SendOutput(pad, b => OutputBlock.SetRumble(b, 255, 0));
		WaitUntil(() => link.Writes.Any(w => w[0] == 0x32 && w.AsSpan(13, 64).ToArray().Any(s => s != 0)));
		byte[] haptics = link.Writes.First(w => w[0] == 0x32 && w.AsSpan(13, 64).ToArray().Any(s => s != 0));
		for (int i = 0; i < 32; i++)
		{
			Assert.Equal(0, haptics[13 + 2 * i + 1]); // right motor off: right side silent
		}

		SendOutput(pad, b => OutputBlock.SetRumble(b, 0, 0));
		Assert.Equal(((byte)0, (byte)0), bridge.Motors);
		Assert.All(link.Writes.Where(w => w[0] == 0x31), w => Assert.Equal(0, w[3 + OutputBlock.Flag0] & 0x03));
	}

	[Fact]
	public void Audio_ReachesTheHapticsMixer()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { GameHapticsLevel = 1.0 };
		byte[] audio = HapticsConverterTests.Audio(480, ch3: HapticsConverterTests.Tone(100, 0.5));
		IsoPacket[] packets = Enumerable.Range(0, 10).Select(i => new IsoPacket(i * 384, 384, 0, 0)).ToArray();

		pad.Submit(Command(7, UsbIpCodes.DirOut, VirtualDualSense.AudioOutEndpoint, audio, packets), _ => { });

		Assert.Equal(audio.Length, bridge.AudioBytes);
		WaitUntil(() => link.Writes.Any(w => w[0] == 0x32 && w.AsSpan(13, 64).ToArray().Any(s => s != 0)));
	}

	[Fact]
	public void Profile_ScalesGameTriggers_KeepsUserLight_AndReportsSignals()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		bridge.ApplyProfile(new BridgeProfile(TriggerIntensity: 0.5, LightFromGame: false));

		SendOutput(pad, b =>
		{
			OutputBlock.SetLightbar(b, 10, 20, 30);
			OutputBlock.SetTriggers(b, TriggerEffects.Weapon(2, 6, 8), TriggerEffects.Feedback(1, 8));
			OutputBlock.SetRumble(b, 50, 0);
		});
		byte[] sent = Block(link.Writes.Last(w => w[0] == 0x31));
		Assert.Equal(TriggerEffects.Weapon(2, 6, 4), sent.AsSpan(OutputBlock.RightTrigger, TriggerEffects.Size).ToArray());
		Assert.Equal(TriggerEffects.Feedback(1, 4), sent.AsSpan(OutputBlock.LeftTrigger, TriggerEffects.Size).ToArray());
		Assert.Equal(0, sent[OutputBlock.Flag1] & OutputBlock.LightbarControl);
		Assert.Equal(new GameSignals(false, true, true), bridge.TakeGameSignals());
		Assert.Equal(default, bridge.TakeGameSignals());
	}

	[Fact]
	public void Profile_FallbackTrigger_WhenTheGameSetsNone()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		byte[] fallback = TriggerEffects.Feedback(2, 5);
		bridge.ApplyProfile(new BridgeProfile(FallbackTrigger: fallback));

		SendOutput(pad, b => OutputBlock.SetRumble(b, 100, 100));
		byte[] sent = Block(link.Writes.Last(w => w[0] == 0x31));
		Assert.Equal(fallback, sent.AsSpan(OutputBlock.RightTrigger, TriggerEffects.Size).ToArray());
		Assert.Equal(fallback, sent.AsSpan(OutputBlock.LeftTrigger, TriggerEffects.Size).ToArray());
		Assert.NotEqual(0, sent[OutputBlock.Flag0] & OutputBlock.RightTriggerEffect);
		Assert.Equal(new GameSignals(false, false, true), bridge.TakeGameSignals());
	}

	[Fact]
	public void Audio_NonSilent_IsANativeSignal_SilenceIsNot()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		IsoPacket[] packets = Enumerable.Range(0, 10).Select(i => new IsoPacket(i * 384, 384, 0, 0)).ToArray();

		pad.Submit(Command(7, UsbIpCodes.DirOut, VirtualDualSense.AudioOutEndpoint, HapticsConverterTests.Audio(480, ch1: HapticsConverterTests.Tone(100, 0.9)), packets), _ => { });
		Assert.False(bridge.TakeGameSignals().Haptics);
		pad.Submit(Command(8, UsbIpCodes.DirOut, VirtualDualSense.AudioOutEndpoint, HapticsConverterTests.Audio(480, ch4: HapticsConverterTests.Tone(100, 0.2)), packets), _ => { });
		Assert.True(bridge.TakeGameSignals().IsNative);
	}
}
