// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Protocol;

namespace Kopru.Core.Tests;

public class TriggerScaleTests
{
	// An 11-byte effect: the mode and the bytes after it, zero-padded.
	private static byte[] E(params byte[] bytes)
	{
		byte[] effect = new byte[TriggerEffects.Size];
		bytes.CopyTo(effect, 0);
		return effect;
	}

	// A zoned effect (0x21 / 0x26): strengths[zone] 1-8, 0 = zone not active.
	internal static byte[] Zoned(byte mode, int[] strengths, byte frequency = 0)
	{
		ushort active = 0;
		uint packed = 0;
		for (int zone = 0; zone < 10; zone++)
		{
			if (strengths[zone] != 0)
			{
				active |= (ushort)(1 << zone);
				packed |= (uint)(strengths[zone] - 1) << (3 * zone);
			}
		}
		byte[] effect = E(mode, (byte)active, (byte)(active >> 8), (byte)packed, (byte)(packed >> 8), (byte)(packed >> 16), (byte)(packed >> 24));
		effect[9] = frequency;
		return effect;
	}

	private static (ushort Active, int[] Strengths) Unpack(byte[] effect)
	{
		ushort active = (ushort)(effect[1] | effect[2] << 8);
		uint packed = (uint)(effect[3] | effect[4] << 8 | effect[5] << 16 | effect[6] << 24);
		int[] strengths = new int[10];
		for (int zone = 0; zone < 10; zone++)
		{
			strengths[zone] = (active & (1 << zone)) != 0 ? (int)((packed >> (3 * zone)) & 0x07) + 1 : 0;
		}
		return (active, strengths);
	}

	private static readonly int[] Slope = [0, 0, 1, 2, 3, 4, 5, 6, 7, 8];

	public static TheoryData<string, byte[], double, byte[]> Cases() => new()
	{
		// Off and reset.
		{ "off 75", TriggerEffects.Off(), 0.75, TriggerEffects.Off() },
		{ "reset 50", E(0x00), 0.5, E(0x00) },
		{ "reset 0", E(0x00), 0, TriggerEffects.Off() },

		// 0x01 simple feedback: [1] start, [2] force 0-255.
		{ "simple feedback 100", E(0x01, 0x40, 200), 1, E(0x01, 0x40, 200) },
		{ "simple feedback 75", E(0x01, 0x40, 200), 0.75, E(0x01, 0x40, 150) },
		{ "simple feedback 50", E(0x01, 0x40, 255), 0.5, E(0x01, 0x40, 128) },
		{ "simple feedback 50 min", E(0x01, 0x40, 1), 0.5, E(0x01, 0x40, 1) },
		{ "simple feedback 0", E(0x01, 0x40, 200), 0, TriggerEffects.Off() },

		// 0x02 simple weapon: [1] start, [2] end, [3] force.
		{ "simple weapon 75", E(0x02, 0x20, 0x90, 255), 0.75, E(0x02, 0x20, 0x90, 191) },
		{ "simple weapon 50", E(0x02, 0x20, 0x90, 200), 0.5, E(0x02, 0x20, 0x90, 100) },
		{ "simple weapon 0", E(0x02, 0x20, 0x90, 200), 0, TriggerEffects.Off() },

		// 0x06 simple vibration: [1] frequency, [2] amplitude, [3] start; only the amplitude moves.
		{ "simple vibration 75", E(0x06, 40, 200, 0x30), 0.75, E(0x06, 40, 150, 0x30) },
		{ "simple vibration 50", E(0x06, 40, 200, 0x30), 0.5, E(0x06, 40, 100, 0x30) },
		{ "simple vibration 0", E(0x06, 40, 200, 0x30), 0, TriggerEffects.Off() },

		// 0x21 feedback, single position and slope / multi-position.
		{ "feedback 100", TriggerEffects.Feedback(2, 8), 1, TriggerEffects.Feedback(2, 8) },
		{ "feedback 75", TriggerEffects.Feedback(2, 8), 0.75, TriggerEffects.Feedback(2, 6) },
		{ "feedback 50", TriggerEffects.Feedback(2, 8), 0.5, TriggerEffects.Feedback(2, 4) },
		{ "feedback 0", TriggerEffects.Feedback(2, 8), 0, TriggerEffects.Off() },
		{ "slope feedback 75", Zoned(0x21, Slope), 0.75, Zoned(0x21, [0, 0, 1, 2, 2, 3, 4, 5, 5, 6]) },
		{ "slope feedback 50", Zoned(0x21, Slope), 0.5, Zoned(0x21, [0, 0, 1, 1, 2, 2, 3, 3, 4, 4]) },
		{ "multi feedback 50", Zoned(0x21, [8, 0, 8, 0, 3, 0, 1, 0, 0, 5]), 0.5, Zoned(0x21, [4, 0, 4, 0, 2, 0, 1, 0, 0, 3]) },

		// 0x25 weapon: strength in byte 3; zones untouched.
		{ "weapon 100", TriggerEffects.Weapon(3, 7, 8), 1, TriggerEffects.Weapon(3, 7, 8) },
		{ "weapon 75", TriggerEffects.Weapon(3, 7, 8), 0.75, TriggerEffects.Weapon(3, 7, 6) },
		{ "weapon 50", TriggerEffects.Weapon(3, 7, 8), 0.5, TriggerEffects.Weapon(3, 7, 4) },
		{ "weapon 0", TriggerEffects.Weapon(3, 7, 8), 0, TriggerEffects.Off() },

		// 0x26 vibration: amplitudes scaled, frequency kept.
		{ "vibration 75", TriggerEffects.Vibration(1, 8, 60), 0.75, TriggerEffects.Vibration(1, 6, 60) },
		{ "vibration 50", TriggerEffects.Vibration(1, 8, 60), 0.5, TriggerEffects.Vibration(1, 4, 60) },
		{ "vibration 0", TriggerEffects.Vibration(1, 8, 60), 0, TriggerEffects.Off() },
		{ "multi vibration 75", Zoned(0x26, Slope, 200), 0.75, Zoned(0x26, [0, 0, 1, 2, 2, 3, 4, 5, 5, 6], 200) },

		// 0x22 bow: [3] bits 0-2 strength-1, bits 3-5 snap force-1.
		{ "bow 75", E(0x22, 0x24, 0x00, 0x1F), 0.75, E(0x22, 0x24, 0x00, 0x15) },
		{ "bow 50", E(0x22, 0x24, 0x00, 0x1F), 0.5, E(0x22, 0x24, 0x00, 0x0B) },
		{ "bow 0", E(0x22, 0x24, 0x00, 0x1F), 0, TriggerEffects.Off() },

		// 0x27 machine: [3] amplitudes A (bits 0-2) and B (bits 3-5) 0-7; frequency and period kept; 0 stays 0.
		{ "machine 75", E(0x27, 0x24, 0x00, 0x17, 30, 10), 0.75, E(0x27, 0x24, 0x00, 0x15, 30, 10) },
		{ "machine 50", E(0x27, 0x24, 0x00, 0x17, 30, 10), 0.5, E(0x27, 0x24, 0x00, 0x0C, 30, 10) },
		{ "machine 50 B off", E(0x27, 0x24, 0x00, 0x07, 30, 10), 0.5, E(0x27, 0x24, 0x00, 0x04, 30, 10) },
		{ "machine 0", E(0x27, 0x24, 0x00, 0x17, 30, 10), 0, TriggerEffects.Off() },

		// 0x23 galloping has no force: kept until 0 %.
		{ "galloping 50", E(0x23, 0x24, 0x00, 0x0A, 5), 0.5, E(0x23, 0x24, 0x00, 0x0A, 5) },
		{ "galloping 0", E(0x23, 0x24, 0x00, 0x0A, 5), 0, TriggerEffects.Off() },
	};

	[Theory]
	[MemberData(nameof(Cases))]
	public void Scale_PerMode(string name, byte[] effect, double k, byte[] expected)
	{
		Assert.True(TriggerEffects.IsKnown(effect), name);
		Assert.Equal(expected, TriggerEffects.Scale(effect, k));
	}

	[Fact]
	public void Scale_AtFullStrength_IsBitExact_ForAnyBytes()
	{
		Random random = new(11);
		byte[] modes = [0x00, 0x01, 0x02, 0x05, 0x06, 0x21, 0x22, 0x23, 0x25, 0x26, 0x27, 0x7E, 0xFC];
		for (int i = 0; i < 500; i++)
		{
			byte[] effect = new byte[TriggerEffects.Size];
			random.NextBytes(effect);
			effect[0] = modes[i % modes.Length];
			Assert.Equal(effect, TriggerEffects.Scale(effect, 1));
			Assert.Equal(effect, TriggerEffects.Scale(effect, 1.5));
			Assert.Equal(effect, TriggerEffects.Scale(effect, double.NaN));
		}
	}

	[Fact]
	public void Scale_PackedStrengths_RoundTrip_AndOnlyForceBytesChange()
	{
		Random random = new(5);
		double[] ks = [0.01, 0.25, 0.5, 0.75, 0.99];
		foreach (byte mode in new byte[] { 0x21, 0x26 })
		{
			for (int i = 0; i < 200; i++)
			{
				int[] strengths = Enumerable.Range(0, 10).Select(_ => random.Next(0, 9)).ToArray();
				byte[] effect = Zoned(mode, strengths, (byte)random.Next(256));
				effect[7] = (byte)random.Next(256); // bytes outside the force fields stay as they are
				effect[10] = (byte)random.Next(256);
				Assert.Equal(strengths, Unpack(effect).Strengths); // the helper round-trips
				foreach (double k in ks)
				{
					byte[] scaled = TriggerEffects.Scale(effect, k);
					(ushort active, int[] got) = Unpack(scaled);
					Assert.Equal(Unpack(effect).Active, active); // the zone mask never changes
					for (int zone = 0; zone < 10; zone++)
					{
						int want = strengths[zone] == 0 ? 0 : Math.Max(1, (int)Math.Round(strengths[zone] * k, MidpointRounding.AwayFromZero));
						Assert.Equal(want, got[zone]);
					}
					Assert.Equal(effect[0], scaled[0]);
					Assert.Equal(effect.AsSpan(7).ToArray(), scaled.AsSpan(7).ToArray()); // frequency and the rest
				}
			}
		}
	}

	[Fact]
	public void Scale_NoZoneDropsOut_ExceptAtZero_WhichIsOff()
	{
		byte[] light = Zoned(0x21, [1, 1, 2, 0, 0, 0, 0, 0, 0, 1]);
		byte[] scaled = TriggerEffects.Scale(light, 0.01);
		// Every active zone keeps its bit and its smallest strength: mask and packed values stay consistent.
		Assert.Equal(Zoned(0x21, [1, 1, 1, 0, 0, 0, 0, 0, 0, 1]), scaled);
		Assert.Equal(light[1..3], scaled[1..3]);
		// 0 %: the whole effect is off, not a feedback effect with zero zones.
		Assert.Equal(TriggerEffects.Off(), TriggerEffects.Scale(light, 0));
		Assert.Equal(TriggerEffects.Off(), TriggerEffects.Scale(light, -1));
		Assert.Equal(TriggerEffects.Weapon(2, 6, 1), TriggerEffects.Scale(TriggerEffects.Weapon(2, 6, 1), 0.01));
		Assert.Equal(TriggerEffects.Vibration(0, 1, 5), TriggerEffects.Scale(TriggerEffects.Vibration(0, 1, 5), 0.01));
	}

	[Theory]
	[InlineData(0x7E)]
	[InlineData(0x11)]
	[InlineData(0xFC)]
	[InlineData(0x24)]
	public void Scale_UnknownMode_PassesThrough(byte mode)
	{
		byte[] effect = E(mode, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x11, 0x22);
		Assert.False(TriggerEffects.IsKnown(effect));
		Assert.Equal(effect, TriggerEffects.Scale(effect, 0.5));
		Assert.Equal(effect, TriggerEffects.Scale(effect, 0.75));
		Assert.Equal(TriggerEffects.Off(), TriggerEffects.Scale(effect, 0)); // 0 % still means the triggers are off
	}

	[Fact]
	public void Scale_ShortInput_PassesThrough()
	{
		byte[] effect = [0x21, 0xFC, 0x03];
		Assert.Equal(effect, TriggerEffects.Scale(effect, 0.5));
		Assert.False(TriggerEffects.IsKnown(effect));
	}
}
