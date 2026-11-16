// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Protocol;

namespace Kopru.Core.Mods;

// TriggerUpdate's mode and parameters as our trigger effect bytes (TriggerEffects). Parameter order and ranges follow
// docs/DSX-COMPAT.md; missing or out-of-range parameters give null (the trigger keeps its previous effect). The named
// presets were never published: they are our own approximations.
public static class DsxTriggers
{
	// VibrateTrigger's frequency when the mod sends none.
	public const int DefaultVibrateFrequency = 10;

	public static byte[]? Effect(DsxTriggerMode mode, IReadOnlyList<DsxParam> p)
	{
		try
		{
			return mode switch
			{
				DsxTriggerMode.Normal or DsxTriggerMode.OFF => TriggerEffects.Off(),
				DsxTriggerMode.GameCube => TriggerEffects.Weapon(6, 8, 6),
				DsxTriggerMode.VerySoft => TriggerEffects.Feedback(0, 1),
				DsxTriggerMode.Soft => TriggerEffects.Feedback(0, 2),
				DsxTriggerMode.Medium => TriggerEffects.Feedback(0, 4),
				DsxTriggerMode.Hard => TriggerEffects.Feedback(0, 5),
				DsxTriggerMode.VeryHard => TriggerEffects.Feedback(0, 6),
				DsxTriggerMode.Hardest => TriggerEffects.Feedback(0, 7),
				DsxTriggerMode.Rigid => TriggerEffects.Feedback(0, 8),
				DsxTriggerMode.Choppy => TriggerEffects.MultiplePositionFeedback([0, 6, 0, 6, 0, 6, 0, 6, 0, 6]),
				DsxTriggerMode.VibrateTriggerPulse => TriggerEffects.MultiplePositionVibration(DefaultVibrateFrequency, [0, 8, 0, 8, 0, 8, 0, 8, 0, 8]),
				DsxTriggerMode.VibrateTrigger => TriggerEffects.Vibration(0, 8, p.Count > 0 ? p[0].Int : DefaultVibrateFrequency),
				DsxTriggerMode.VIBRATE_TRIGGER_10Hz => TriggerEffects.Vibration(0, 8, 10),
				DsxTriggerMode.CustomTriggerValue => Custom(p),
				DsxTriggerMode.Resistance or DsxTriggerMode.FEEDBACK => Need(p, 2) ? TriggerEffects.Feedback(p[0].Int, p[1].Int) : null,
				DsxTriggerMode.Bow => Need(p, 4) ? TriggerEffects.Bow(p[0].Int, p[1].Int, p[2].Int, p[3].Int) : null,
				DsxTriggerMode.Galloping => Need(p, 5) ? TriggerEffects.Galloping(p[0].Int, p[1].Int, p[2].Int, p[3].Int, p[4].Int) : null,
				DsxTriggerMode.SemiAutomaticGun or DsxTriggerMode.WEAPON => Need(p, 3) ? TriggerEffects.Weapon(p[0].Int, p[1].Int, p[2].Int) : null,
				DsxTriggerMode.AutomaticGun or DsxTriggerMode.VIBRATION => Need(p, 3) ? TriggerEffects.Vibration(p[0].Int, p[1].Int, p[2].Int) : null,
				// The period comes in seconds with decimals (0-2): sent in tenths.
				DsxTriggerMode.Machine => Need(p, 6)
					? TriggerEffects.Machine(p[0].Int, p[1].Int, p[2].Int, p[3].Int, p[4].Int, (int)Math.Round(p[5].Number * 10, MidpointRounding.AwayFromZero))
					: null,
				DsxTriggerMode.SLOPE_FEEDBACK => Need(p, 4) ? TriggerEffects.SlopeFeedback(p[0].Int, p[1].Int, p[2].Int, p[3].Int) : null,
				DsxTriggerMode.MULTIPLE_POSITION_FEEDBACK => Need(p, 10) ? TriggerEffects.MultiplePositionFeedback(Ints(p, 0, 10)) : null,
				DsxTriggerMode.MULTIPLE_POSITION_VIBRATION => Need(p, 11) ? TriggerEffects.MultiplePositionVibration(p[0].Int, Ints(p, 1, 10)) : null,
				_ => null
			};
		}
		catch (ArgumentOutOfRangeException)
		{
			return null;
		}
	}

	// CustomTriggerValue's first value: the custom mode (0-16), which picks the effect's mode byte.
	private static readonly byte[] CustomModeBytes =
	[
		0x00, 0x01, 0x21, 0x05, 0x25, 0x02, 0x22, 0x06, 0x26, // Off, Rigid, RigidA, RigidB, RigidAB, Pulse, PulseA, PulseB, PulseAB
		0x06, 0x26, 0x06, 0x26, // VibrateResistance, A, B, AB
		0x27, 0x27, 0x27, 0x27 // VibratePulse, A, B, AB
	];

	// The custom mode, then up to seven values 0-255: values 1-6 go to effect bytes 1-6, value 7 to byte 9 (missing = 0).
	private static byte[]? Custom(IReadOnlyList<DsxParam> p)
	{
		if (p.Count < 1 || p[0].Int is < 0 or > 16)
		{
			return null;
		}
		byte[] effect = new byte[TriggerEffects.Size];
		effect[0] = CustomModeBytes[p[0].Int];
		for (int i = 1; i <= 7; i++)
		{
			int v = i < p.Count ? p[i].Int : 0;
			if (v is < 0 or > 255)
			{
				return null;
			}
			effect[i <= 6 ? i : 9] = (byte)v;
		}
		return effect;
	}

	private static bool Need(IReadOnlyList<DsxParam> p, int count) => p.Count >= count;

	private static int[] Ints(IReadOnlyList<DsxParam> p, int from, int count)
	{
		int[] values = new int[count];
		for (int i = 0; i < count; i++)
		{
			values[i] = p[from + i].Int;
		}
		return values;
	}
}
