// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Protocol;

// Bluetooth reports carry a standard CRC-32 (reflected, polynomial 0xEDB88320) over one seed byte and the report.
public static class Crc
{
	public const byte InputSeed = 0xA1;

	public const byte OutputSeed = 0xA2;

	public const byte FeatureSeed = 0xA3;

	private static readonly uint[] Table = BuildTable();

	public static uint Compute(byte seed, ReadOnlySpan<byte> data)
	{
		uint crc = Update(0xFFFFFFFF, seed);
		foreach (byte b in data)
		{
			crc = Update(crc, b);
		}
		return ~crc;
	}

	public static uint Standard(ReadOnlySpan<byte> data)
	{
		uint crc = 0xFFFFFFFF;
		foreach (byte b in data)
		{
			crc = Update(crc, b);
		}
		return ~crc;
	}

	private static uint Update(uint crc, byte b) => Table[(crc ^ b) & 0xFF] ^ (crc >> 8);

	private static uint[] BuildTable()
	{
		uint[] table = new uint[256];
		for (uint i = 0; i < 256; i++)
		{
			uint c = i;
			for (int k = 0; k < 8; k++)
			{
				c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
			}
			table[i] = c;
		}
		return table;
	}
}
