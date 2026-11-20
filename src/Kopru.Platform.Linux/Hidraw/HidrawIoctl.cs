// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Platform.Linux.Hidraw;

// The hidraw ioctl request numbers, from the public <linux/hidraw.h> as documented in the kernel's
// Documentation/hid/hidraw.rst: HIDIOCSFEATURE(len) = _IOC(_IOC_WRITE|_IOC_READ, 'H', 0x06, len) and
// HIDIOCGFEATURE(len) = _IOC(_IOC_WRITE|_IOC_READ, 'H', 0x07, len). The _IOC layout is the generic one of
// <asm-generic/ioctl.h> (x86, x86-64, arm, arm64, riscv): nr in bits 0-7, type in 8-15, size in 16-29, direction in
// 30-31 with write = 1 and read = 2. (A few architectures such as powerpc and mips use another layout; not supported.)
public static class HidrawIoctl
{
	private const uint DirectionWrite = 1;
	private const uint DirectionRead = 2;
	private const uint Type = 'H';
	private const int MaxSize = (1 << 14) - 1;

	public const uint SetFeatureNumber = 0x06;
	public const uint GetFeatureNumber = 0x07;

	public static uint Request(uint direction, uint type, uint number, int size)
	{
		if (size < 0 || size > MaxSize)
		{
			throw new ArgumentOutOfRangeException(nameof(size), size, "ioctl size must fit in 14 bits");
		}
		return (direction << 30) | ((uint)size << 16) | (type << 8) | number;
	}

	// HIDIOCSFEATURE(len): sends a feature report; the buffer starts with the report ID.
	public static uint SetFeature(int length) => Request(DirectionWrite | DirectionRead, Type, SetFeatureNumber, length);

	// HIDIOCGFEATURE(len): reads a feature report; byte 0 of the buffer holds the report ID on the way in and out.
	public static uint GetFeature(int length) => Request(DirectionWrite | DirectionRead, Type, GetFeatureNumber, length);
}
