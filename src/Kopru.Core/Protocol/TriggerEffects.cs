// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Protocol;

// Adaptive trigger effects, 11 bytes each. The trigger's travel is split into ten zones.
public static class TriggerEffects
{
	public const int Size = 11;

	public static byte[] Off() => [0x05, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

	// Resistance from zone `position` (0-9) to the end; strength 1-8 (0 = off).
	public static byte[] Feedback(int position, int strength)
	{
		Check(position is >= 0 and <= 9 && strength is >= 0 and <= 8);
		if (strength == 0)
		{
			return Off();
		}
		(ushort active, uint packed) = Zones(position, strength - 1);
		return [0x21, (byte)active, (byte)(active >> 8), (byte)packed, (byte)(packed >> 8), (byte)(packed >> 16), (byte)(packed >> 24), 0, 0, 0, 0];
	}

	// A gun trigger: resistance between zone start (2-7) and end (start+1..8) that gives way with a click.
	public static byte[] Weapon(int start, int end, int strength)
	{
		Check(start is >= 2 and <= 7 && end > start && end <= 8 && strength is >= 0 and <= 8);
		if (strength == 0)
		{
			return Off();
		}
		int zones = (1 << start) | (1 << end);
		return [0x25, (byte)zones, (byte)(zones >> 8), (byte)(strength - 1), 0, 0, 0, 0, 0, 0, 0];
	}

	// The trigger vibrates from zone `position`: amplitude 1-8, frequency 1-255 Hz (0 = off).
	public static byte[] Vibration(int position, int amplitude, int frequency)
	{
		Check(position is >= 0 and <= 9 && amplitude is >= 0 and <= 8 && frequency is >= 0 and <= 255);
		if (amplitude == 0 || frequency == 0)
		{
			return Off();
		}
		(ushort active, uint packed) = Zones(position, amplitude - 1);
		return [0x26, (byte)active, (byte)(active >> 8), (byte)packed, (byte)(packed >> 8), (byte)(packed >> 16), (byte)(packed >> 24), 0, 0, (byte)frequency, 0];
	}

	// Effect mode bytes (byte 0 of the 11). Public DualSense output report facts:
	// 0x01 simple feedback / "continuous resistance": [1] start position 0-255, [2] force 0-255.
	// 0x02 simple weapon / "section resistance": [1] start position, [2] end position, [3] force 0-255.
	// 0x06 simple vibration: [1] frequency (Hz), [2] amplitude 0-255, [3] start position.
	// 0x21 feedback (also the multi-position and slope forms): [1-2] zone mask (bit n = zone n, 10 zones),
	//      [3-6] 32-bit packed strengths, 3 bits per zone (zone n at bits 3n..3n+2, value = strength 1-8 minus 1).
	// 0x22 bow: [1-2] mask with the start and end zone bits, [3] bits 0-2 strength - 1, bits 3-5 snap force - 1.
	// 0x23 galloping: [1-2] start/end zone mask, [3] foot timing ratios, [4] frequency (no force: not scaled).
	// 0x25 weapon: [1-2] mask with the start and end zone bits, [3] strength - 1 (0-7).
	// 0x26 vibration (also multi-position): [1-2] zone mask, [3-6] packed amplitudes like 0x21, [9] frequency (Hz).
	// 0x27 machine: [1-2] start/end zone mask, [3] bits 0-2 amplitude A, bits 3-5 amplitude B (0-7), [4] frequency,
	//      [5] period.
	public const byte SimpleFeedbackMode = 0x01;
	public const byte SimpleWeaponMode = 0x02;
	public const byte SimpleVibrationMode = 0x06;
	public const byte FeedbackMode = 0x21;
	public const byte BowMode = 0x22;
	public const byte GallopingMode = 0x23;
	public const byte WeaponMode = 0x25;
	public const byte VibrationMode = 0x26;
	public const byte MachineMode = 0x27;
	public const byte OffMode = 0x05;

	// True for an effect that does nothing (off, or mode 0 / reset).
	public static bool IsOff(ReadOnlySpan<byte> effect) => effect.Length == 0 || effect[0] is 0x00 or OffMode;

	// True when Scale understands the effect's mode (an unknown one is passed through unchanged).
	public static bool IsKnown(ReadOnlySpan<byte> effect) => effect.Length >= Size && effect[0] is 0x00 or OffMode
		or SimpleFeedbackMode or SimpleWeaponMode or SimpleVibrationMode or FeedbackMode or BowMode or GallopingMode
		or WeaponMode or VibrationMode or MachineMode;

	// Scales an effect's force/amplitude by k (0..1); positions, zone masks, frequencies and timings are never changed.
	// k >= 1 returns the bytes unchanged (bit-exact), k <= 0 returns Off(). In between a non-zero force stays at least
	// the smallest step (1-8 steps stay >= 1, 0-255 forces >= 1), so no zone ever drops out of its mask and light
	// effects do not vanish. Rounding is to nearest, halves up. Unknown modes (and short input) pass through unchanged.
	public static byte[] Scale(ReadOnlySpan<byte> effect, double k)
	{
		byte[] result = effect.ToArray();
		if (!(k < 1) || result.Length < Size)
		{
			return result;
		}
		if (k <= 0)
		{
			return Off();
		}
		switch (result[0])
		{
			case SimpleFeedbackMode:
				result[2] = (byte)ScaleLevel(result[2], k);
				break;
			case SimpleWeaponMode:
				result[3] = (byte)ScaleLevel(result[3], k);
				break;
			case SimpleVibrationMode:
				result[2] = (byte)ScaleLevel(result[2], k);
				break;
			case FeedbackMode or VibrationMode:
				{
					ushort active = (ushort)(result[1] | result[2] << 8);
					uint packed = (uint)(result[3] | result[4] << 8 | result[5] << 16 | result[6] << 24);
					for (int zone = 0; zone < 10; zone++)
					{
						if ((active & (1 << zone)) == 0)
						{
							continue;
						}
						int step = ScaleLevel((int)((packed >> (3 * zone)) & 0x07) + 1, k);
						packed = (packed & ~(0x07u << (3 * zone))) | (uint)(step - 1) << (3 * zone);
					}
					result[3] = (byte)packed;
					result[4] = (byte)(packed >> 8);
					result[5] = (byte)(packed >> 16);
					result[6] = (byte)(packed >> 24);
					break;
				}
			case BowMode:
				{
					int strength = ScaleLevel((result[3] & 0x07) + 1, k);
					int snap = ScaleLevel(((result[3] >> 3) & 0x07) + 1, k);
					result[3] = (byte)((result[3] & 0xC0) | (strength - 1) | (snap - 1) << 3);
					break;
				}
			case WeaponMode:
				result[3] = (byte)((result[3] & 0xF8) | (ScaleLevel((result[3] & 0x07) + 1, k) - 1));
				break;
			case MachineMode:
				{
					int a = ScaleLevel(result[3] & 0x07, k);
					int b = ScaleLevel((result[3] >> 3) & 0x07, k);
					result[3] = (byte)((result[3] & 0xC0) | a | b << 3);
					break;
				}
		}
		return result;
	}

	// v scaled by k (0 < k < 1), rounded halves up; a non-zero v stays at least 1.
	private static int ScaleLevel(int v, double k) =>
		v == 0 ? 0 : Math.Max(1, (int)Math.Round(v * k, MidpointRounding.AwayFromZero));

	private static (ushort Active, uint Packed) Zones(int position, int value)
	{
		ushort active = 0;
		uint packed = 0;
		for (int zone = position; zone < 10; zone++)
		{
			active |= (ushort)(1 << zone);
			packed |= (uint)(value & 0x07) << (3 * zone);
		}
		return (active, packed);
	}

	private static void Check(bool ok)
	{
		if (!ok)
		{
			throw new ArgumentOutOfRangeException(null, "trigger effect parameter out of range");
		}
	}
}
