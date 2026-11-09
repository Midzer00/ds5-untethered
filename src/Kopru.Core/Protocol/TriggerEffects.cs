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

	public const byte FeedbackMode = 0x21;
	public const byte WeaponMode = 0x25;
	public const byte VibrationMode = 0x26;
	public const byte OffMode = 0x05;

	// True for an effect that does nothing (off, or mode 0 / reset).
	public static bool IsOff(ReadOnlySpan<byte> effect) => effect.Length == 0 || effect[0] is 0x00 or OffMode;

	// Scales an effect's force by k (0..1). Scaled: feedback (0x21) and vibration (0x26) per-zone strength/amplitude
	// (3-bit values 1-8 in bytes 3-6, a zone dropping to 0 is switched off), weapon (0x25) strength (byte 3). Kept as
	// is: positions, zones, vibration frequency and every other mode (passed through unchanged). k <= 0 turns the
	// trigger off; k >= 1 returns the effect unchanged.
	public static byte[] Scale(ReadOnlySpan<byte> effect, double k)
	{
		byte[] result = effect.ToArray();
		if (k >= 1 || result.Length < Size)
		{
			return result;
		}
		if (k <= 0)
		{
			return Off();
		}
		switch (result[0])
		{
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
						int step = (int)Math.Round((((packed >> (3 * zone)) & 0x07) + 1) * k);
						packed &= ~(0x07u << (3 * zone));
						if (step == 0)
						{
							active &= (ushort)~(1 << zone);
						}
						else
						{
							packed |= (uint)(step - 1) << (3 * zone);
						}
					}
					if (active == 0)
					{
						return Off();
					}
					result[1] = (byte)active;
					result[2] = (byte)(active >> 8);
					result[3] = (byte)packed;
					result[4] = (byte)(packed >> 8);
					result[5] = (byte)(packed >> 16);
					result[6] = (byte)(packed >> 24);
					return result;
				}
			case WeaponMode:
				{
					int step = (int)Math.Round(((result[3] & 0x07) + 1) * k);
					if (step == 0)
					{
						return Off();
					}
					result[3] = (byte)(step - 1);
					return result;
				}
			default:
				return result;
		}
	}

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
