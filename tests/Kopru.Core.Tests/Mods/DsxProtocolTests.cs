// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Kopru.Core.Mods;
using Kopru.Core.Protocol;

namespace Kopru.Core.Tests;

// The DSX mod messages (docs/DSX-COMPAT.md): parsing every documented instruction from hand-written JSON, the lenient
// shapes, malformed and oversized input, and the mapping to trigger effect bytes, light and LED state.
public class DsxProtocolTests
{
	private static DsxPacket Parse(string json)
	{
		(DsxPacket? packet, DsxParseError error) = DsxProtocol.Parse(Encoding.UTF8.GetBytes(json));
		Assert.Equal(DsxParseError.None, error);
		return packet!;
	}

	// The state after applying the packet's instructions in order, and the result of each.
	private static (DsxModState State, List<DsxApplyResult> Results) Apply(string json)
	{
		DsxModState state = new();
		List<DsxApplyResult> results = [];
		HashSet<int> changed = [];
		foreach (DsxInstruction i in Parse(json).Instructions)
		{
			results.Add(state.Apply(i, changed));
		}
		return (state, results);
	}

	private static string Packet(params string[] instructions) => "{\"instructions\":[" + string.Join(",", instructions) + "]}";

	private static string Trigger(int side, int mode, params double[] p) =>
		$"{{\"type\":1,\"parameters\":[0,{side},{mode}{string.Concat(p.Select(v => "," + v.ToString(System.Globalization.CultureInfo.InvariantCulture)))}]}}";

	[Fact]
	public void Parse_TheDocumentedShape()
	{
		DsxPacket packet = Parse("{\"instructions\":[{\"type\":1,\"parameters\":[0,2,13,0,8]}]}");
		DsxInstruction i = Assert.Single(packet.Instructions);
		Assert.Equal(DsxInstructionType.TriggerUpdate, i.Type);
		Assert.Equal(0, i.Index);
		Assert.Equal([2, 13, 0, 8], i.Args.Select(a => a.Int));
	}

	[Fact]
	public void Parse_IsLenient_KeysAnyCase_TypeByName_NullAndStringAndBoolParameters()
	{
		DsxPacket packet = Parse("{\"Instructions\":[{\"Type\":\"RGBUpdate\",\"Parameters\":[\"0\",255,\"128\",null]},"
			+ "{\"type\":0,\"parameters\":null},{\"type\":0},{\"type\":3,\"parameters\":[0,true,false,true,false,true]},"
			+ "{\"type\":\"triggerupdate\",\"parameters\":[0,\"Left\",\"Resistance\",2,5]}]}");
		Assert.Equal(5, packet.Instructions.Count);
		Assert.Equal(DsxInstructionType.RGBUpdate, packet.Instructions[0].Type);
		Assert.Equal([255, 128, 0], packet.Instructions[0].Args.Select(a => a.Int));
		Assert.Equal(DsxInstructionType.GetDSXStatus, packet.Instructions[1].Type);
		Assert.Empty(packet.Instructions[1].Parameters);
		Assert.Empty(packet.Instructions[2].Parameters);
		Assert.Equal([1, 0, 1, 0, 1], packet.Instructions[3].Args.Select(a => a.Int));
		Assert.Equal(DsxInstructionType.TriggerUpdate, packet.Instructions[4].Type);
		Assert.Equal("Left", packet.Instructions[4].Args[0].Name);
	}

	[Fact]
	public void Parse_UnknownTypes_AreKept_InstructionsWithoutAType_AreSkipped()
	{
		DsxPacket packet = Parse("{\"instructions\":[{\"type\":42,\"parameters\":[0,1]},{\"type\":\"HapticPulse\",\"parameters\":[0]},"
			+ "{\"parameters\":[0]},{\"type\":[1]},null,5]}");
		Assert.Equal(2, packet.Instructions.Count);
		Assert.All(packet.Instructions, i => Assert.Null(i.Type));
		Assert.Equal(["42", "HapticPulse"], packet.Instructions.Select(i => i.TypeText));
		Assert.Equal(4, packet.Skipped);
	}

	[Theory]
	[InlineData("")]
	[InlineData("not json")]
	[InlineData("{\"instructions\":")]
	[InlineData("[1,2,3]")]
	[InlineData("{\"something\":[]}")]
	[InlineData("{\"instructions\":{\"type\":1}}")]
	[InlineData("{\"instructions\":[[[[[[[[[[[[1]]]]]]]]]]]]}")]
	public void Parse_Malformed_IsReported_NeverThrown(string json)
	{
		(DsxPacket? packet, DsxParseError error) = DsxProtocol.Parse(Encoding.UTF8.GetBytes(json));
		Assert.Null(packet);
		Assert.Equal(DsxParseError.Malformed, error);
	}

	[Fact]
	public void Parse_Oversized_IsRefusedBeforeParsing()
	{
		string big = Packet(Enumerable.Repeat("{\"type\":0,\"parameters\":null}", 400).ToArray());
		Assert.True(big.Length > DsxProtocol.MaxPacketBytes);
		(DsxPacket? packet, DsxParseError error) = DsxProtocol.Parse(Encoding.UTF8.GetBytes(big));
		Assert.Null(packet);
		Assert.Equal(DsxParseError.Oversized, error);
	}

	[Fact]
	public void Parse_UnknownSample_IsShort()
	{
		string longType = "{\"type\":99,\"parameters\":[" + string.Join(",", Enumerable.Range(0, 200)) + "]}";
		DsxInstruction i = Assert.Single(Parse(Packet(longType)).Instructions);
		Assert.True(i.Sample.Length <= DsxProtocol.SampleLength + 3);
	}

	// Every trigger mode with documented parameters, as our trigger effect bytes.
	public static TheoryData<int, double[], byte[]> TriggerModes => new()
	{
		{ 0, [], TriggerEffects.Off() },
		{ 20, [], TriggerEffects.Off() },
		{ 13, [2, 5], TriggerEffects.Feedback(2, 5) },
		{ 21, [0, 8], TriggerEffects.Feedback(0, 8) },
		{ 14, [1, 6, 7, 4], TriggerEffects.Bow(1, 6, 7, 4) },
		{ 15, [1, 9, 2, 6, 25], TriggerEffects.Galloping(1, 9, 2, 6, 25) },
		{ 16, [2, 6, 8], TriggerEffects.Weapon(2, 6, 8) },
		{ 22, [3, 5, 4], TriggerEffects.Weapon(3, 5, 4) },
		{ 17, [0, 8, 30], TriggerEffects.Vibration(0, 8, 30) },
		{ 23, [4, 3, 12], TriggerEffects.Vibration(4, 3, 12) },
		{ 18, [1, 8, 3, 6, 20, 1.5], TriggerEffects.Machine(1, 8, 3, 6, 20, 15) },
		{ 8, [40], TriggerEffects.Vibration(0, 8, 40) },
		{ 8, [], TriggerEffects.Vibration(0, 8, DsxTriggers.DefaultVibrateFrequency) },
		{ 19, [], TriggerEffects.Vibration(0, 8, 10) },
		{ 24, [1, 8, 1, 8], TriggerEffects.SlopeFeedback(1, 8, 1, 8) },
		{ 25, [0, 1, 2, 3, 4, 5, 6, 7, 8, 0], TriggerEffects.MultiplePositionFeedback([0, 1, 2, 3, 4, 5, 6, 7, 8, 0]) },
		{ 26, [30, 8, 0, 8, 0, 8, 0, 8, 0, 8, 0], TriggerEffects.MultiplePositionVibration(30, [8, 0, 8, 0, 8, 0, 8, 0, 8, 0]) },
		{ 12, [1, 10, 20, 30, 40, 50, 60, 70], [0x01, 10, 20, 30, 40, 50, 60, 0, 0, 70, 0] },
		{ 12, [8, 255], [0x26, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0] },
		{ 12, [13, 1, 2], [0x27, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0] },
	};

	[Theory]
	[MemberData(nameof(TriggerModes))]
	public void TriggerUpdate_MapsEveryModeToEffectBytes(int mode, double[] p, byte[] expected)
	{
		(DsxModState state, List<DsxApplyResult> results) = Apply(Packet(Trigger(2, mode, p), Trigger(1, mode, p)));
		Assert.All(results, r => Assert.Equal(DsxApplyResult.Applied, r));
		Assert.Equal(expected, state[0]!.RightTrigger);
		Assert.Equal(expected, state[0]!.LeftTrigger);
	}

	[Fact]
	public void TriggerUpdate_NamedPresets_GiveARealEffect_AndGetStronger()
	{
		int[] presets = [2, 3, 10, 4, 5, 6, 7];
		int last = 0;
		foreach (int mode in presets)
		{
			byte[] effect = Apply(Packet(Trigger(2, mode))).State[0]!.RightTrigger!;
			Assert.Equal(TriggerEffects.FeedbackMode, effect[0]);
			int strength = (effect[3] & 0x07) + 1;
			Assert.True(strength > last, $"mode {mode}");
			last = strength;
		}
		foreach (int mode in new[] { 1, 9, 11 })
		{
			byte[] effect = Apply(Packet(Trigger(2, mode))).State[0]!.RightTrigger!;
			Assert.False(TriggerEffects.IsOff(effect), $"mode {mode}");
			Assert.True(TriggerEffects.IsKnown(effect), $"mode {mode}");
		}
	}

	[Theory]
	[InlineData(13, new double[] { 10, 5 })] // start out of range
	[InlineData(13, new double[] { 2 })] // missing force
	[InlineData(16, new double[] { 1, 6, 8 })] // weapon start below 2
	[InlineData(14, new double[] { 5, 3, 4, 4 })] // bow end before start
	[InlineData(15, new double[] { 1, 9, 6, 2, 20 })] // second foot before the first
	[InlineData(25, new double[] { 1, 2, 3 })] // too few zones
	[InlineData(12, new double[] { 17, 1 })] // unknown custom mode
	[InlineData(12, new double[] { 1, 300 })] // custom value out of range
	[InlineData(99, new double[] { })] // unknown mode
	public void TriggerUpdate_Unusable_IsInvalid_AndChangesNothing(int mode, double[] p)
	{
		(DsxModState state, List<DsxApplyResult> results) = Apply(Packet(Trigger(2, 13, 1, 3), Trigger(2, mode, p)));
		Assert.Equal([DsxApplyResult.Applied, DsxApplyResult.Invalid], results);
		Assert.Equal(TriggerEffects.Feedback(1, 3), state[0]!.RightTrigger);
	}

	[Fact]
	public void TriggerUpdate_BadSide_IsInvalid()
	{
		(DsxModState state, List<DsxApplyResult> results) = Apply(Packet(Trigger(3, 13, 1, 3)));
		Assert.Equal([DsxApplyResult.Invalid], results);
		Assert.Null(state[0]);
	}

	[Fact]
	public void RgbUpdate_WithAndWithoutBrightness()
	{
		Assert.Equal(((byte)255, (byte)128, (byte)0), Apply(Packet("{\"type\":2,\"parameters\":[0,255,128,0]}")).State[0]!.Lightbar);
		Assert.Equal(((byte)128, (byte)64, (byte)0), Apply(Packet("{\"type\":2,\"parameters\":[0,255,128,0,128]}")).State[0]!.Lightbar);
		Assert.Equal([DsxApplyResult.Invalid], Apply(Packet("{\"type\":2,\"parameters\":[0,256,0,0]}")).Results);
	}

	[Fact]
	public void PlayerLeds_BothRevisions()
	{
		Assert.Equal((byte)0b10101, Apply(Packet("{\"type\":3,\"parameters\":[0,true,false,true,false,true]}")).State[0]!.PlayerLeds);
		Assert.Equal((byte)0b00110, Apply(Packet("{\"type\":3,\"parameters\":[0,0,1,1,0,0]}")).State[0]!.PlayerLeds);
		byte[] patterns = [0x04, 0x0A, 0x15, 0x1B, 0x1F, 0x00];
		for (int i = 0; i < patterns.Length; i++)
		{
			Assert.Equal(patterns[i], Apply(Packet($"{{\"type\":6,\"parameters\":[0,{i}]}}")).State[0]!.PlayerLeds);
		}
		Assert.Equal([DsxApplyResult.Invalid], Apply(Packet("{\"type\":6,\"parameters\":[0,6]}")).Results);
	}

	[Theory]
	[InlineData(0, OutputBlock.MuteLedOn)]
	[InlineData(1, 2)]
	[InlineData(2, OutputBlock.MuteLedOff)]
	public void MicLed_MapsToTheReportValue(int dsx, byte report) =>
		Assert.Equal(report, Apply(Packet($"{{\"type\":5,\"parameters\":[0,{dsx}]}}")).State[0]!.MicLed);

	[Fact]
	public void TriggerThreshold_PerSide()
	{
		ModOutput o = Apply(Packet("{\"type\":4,\"parameters\":[0,1,120]}", "{\"type\":4,\"parameters\":[0,2,200]}")).State[0]!;
		Assert.Equal((byte)120, o.LeftThreshold);
		Assert.Equal((byte)200, o.RightThreshold);
		Assert.Equal([DsxApplyResult.Invalid], Apply(Packet("{\"type\":4,\"parameters\":[0,1,256]}")).Results);
	}

	[Fact]
	public void Reset_EndsTheControllersOutput_AndStatusChangesNothing()
	{
		(DsxModState state, List<DsxApplyResult> results) = Apply(Packet(Trigger(2, 13, 1, 3), "{\"type\":2,\"parameters\":[0,1,2,3]}",
			"{\"type\":0,\"parameters\":[0]}", "{\"type\":7,\"parameters\":[0]}"));
		Assert.Equal([DsxApplyResult.Applied, DsxApplyResult.Applied, DsxApplyResult.Status, DsxApplyResult.Reset], results);
		Assert.Null(state[0]);
		Assert.False(state.Any);
	}

	[Fact]
	public void Index_NegativeMeansAll_TooHighIsNoController()
	{
		(DsxModState state, List<DsxApplyResult> results) = Apply(Packet("{\"type\":2,\"parameters\":[-1,9,9,9]}", "{\"type\":2,\"parameters\":[7,1,1,1]}"));
		Assert.Equal([DsxApplyResult.Applied, DsxApplyResult.NoController], results);
		for (int i = 0; i < DsxModState.MaxControllers; i++)
		{
			Assert.Equal(((byte)9, (byte)9, (byte)9), state[i]!.Lightbar);
		}
	}

	[Fact]
	public void UnknownInstruction_IsUnknown()
	{
		Assert.Equal([DsxApplyResult.Unknown], Apply(Packet("{\"type\":42,\"parameters\":[0]}")).Results);
	}

	[Fact]
	public void ModOutput_WritesItsLightWithTheValidFlags()
	{
		ModOutput o = new() { Lightbar = (1, 2, 3), PlayerLeds = 0x15, MicLed = 2 };
		byte[] block = new byte[OutputBlock.Size];
		o.WriteLight(block);
		Assert.Equal((1, 2, 3), (block[OutputBlock.Red], block[OutputBlock.Red + 1], block[OutputBlock.Red + 2]));
		Assert.Equal(0x15, block[OutputBlock.PlayerLeds]);
		Assert.Equal(2, block[OutputBlock.MuteLed]);
		Assert.Equal(OutputBlock.LightbarControl | OutputBlock.PlayerLedControl | OutputBlock.MuteLedControl, block[OutputBlock.Flag1]);
	}

	[Fact]
	public void Reply_HasTheDsxFields_AndNoMacAddress()
	{
		byte[] reply = DsxListener.Reply([new DsxDevice(0, false, 70)], new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
		using var doc = System.Text.Json.JsonDocument.Parse(reply);
		var root = doc.RootElement;
		Assert.Equal(DsxListener.ReplyStatus, root.GetProperty("Status").GetString());
		Assert.True(root.GetProperty("isControllerConnected").GetBoolean());
		Assert.Equal(70, root.GetProperty("BatteryLevel").GetInt32());
		Assert.Equal("2026-11-16 12:00:00.000", root.GetProperty("TimeReceived").GetString());
		var device = Assert.Single(root.GetProperty("Devices").EnumerateArray().ToArray());
		Assert.Equal(0, device.GetProperty("Index").GetInt32());
		Assert.Equal(0, device.GetProperty("DeviceType").GetInt32());
		Assert.Equal(1, device.GetProperty("ConnectionType").GetInt32());
		Assert.Equal("00:00:00:00:00:00", device.GetProperty("MacAddress").GetString());
		Assert.True(device.GetProperty("IsSupportAT").GetBoolean());

		using var none = System.Text.Json.JsonDocument.Parse(DsxListener.Reply([], DateTimeOffset.UnixEpoch));
		Assert.False(none.RootElement.GetProperty("isControllerConnected").GetBoolean());
		Assert.Empty(none.RootElement.GetProperty("Devices").EnumerateArray());
	}
}
