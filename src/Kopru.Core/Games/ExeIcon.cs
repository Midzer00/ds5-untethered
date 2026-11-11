// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Kopru.Core.Games;

// Straight-alpha BGRA pixels, top row first.
public sealed record IconPixels(int Width, int Height, byte[] Bgra);

// Reads the main icon of an exe (its first icon resource, at the asked size) as BGRA pixels, with Win32 only.
public static class ExeIcon
{
	// The icon at about size x size pixels, or null when the file has none or can't be read.
	public static IconPixels? Read(string path, int size)
	{
		if (string.IsNullOrWhiteSpace(path) || !OperatingSystem.IsWindows() || !File.Exists(path))
		{
			return null;
		}
		IntPtr[] icons = new IntPtr[1];
		uint count = PrivateExtractIcons(path, 0, size, size, icons, IntPtr.Zero, 1, 0);
		if (count == 0 || count == uint.MaxValue || icons[0] == IntPtr.Zero)
		{
			return null;
		}
		try
		{
			return ToPixels(icons[0]);
		}
		finally
		{
			DestroyIcon(icons[0]);
		}
	}

	private static IconPixels? ToPixels(IntPtr icon)
	{
		if (!GetIconInfo(icon, out IconInfo info))
		{
			return null;
		}
		try
		{
			if (info.Color == IntPtr.Zero || GetObject(info.Color, Marshal.SizeOf<BitmapHeader>(), out BitmapHeader header) == 0)
			{
				return null;
			}
			int width = header.Width;
			int height = Math.Abs(header.Height);
			if (width <= 0 || height <= 0 || width > 256 || height > 256)
			{
				return null;
			}
			byte[] color = Bits(info.Color, width, height);
			if (color.Length == 0)
			{
				return null;
			}
			bool hasAlpha = false;
			for (int i = 3; i < color.Length; i += 4)
			{
				if (color[i] != 0)
				{
					hasAlpha = true;
					break;
				}
			}
			if (!hasAlpha)
			{
				// An old icon without alpha: the mask says which pixels are see-through (white = transparent).
				byte[] mask = info.Mask != IntPtr.Zero ? Bits(info.Mask, width, height) : [];
				for (int i = 0; i < color.Length; i += 4)
				{
					color[i + 3] = mask.Length == color.Length && mask[i] != 0 ? (byte)0 : (byte)255;
				}
			}
			return new IconPixels(width, height, color);
		}
		finally
		{
			if (info.Color != IntPtr.Zero)
			{
				DeleteObject(info.Color);
			}
			if (info.Mask != IntPtr.Zero)
			{
				DeleteObject(info.Mask);
			}
		}
	}

	// The bitmap as 32-bit top-down BGRA; empty on failure.
	private static byte[] Bits(IntPtr bitmap, int width, int height)
	{
		var bmi = new BitmapInfo
		{
			// BITMAPINFOHEADER only (the trailing colour entry is not part of it).
			Size = 40,
			Width = width,
			Height = -height,
			Planes = 1,
			BitCount = 32
		};
		byte[] pixels = new byte[width * height * 4];
		IntPtr dc = GetDC(IntPtr.Zero);
		try
		{
			return GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref bmi, 0) == height ? pixels : [];
		}
		finally
		{
			ReleaseDC(IntPtr.Zero, dc);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IconInfo
	{
		public bool IsIcon;
		public int HotspotX;
		public int HotspotY;
		public IntPtr Mask;
		public IntPtr Color;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BitmapHeader
	{
		public int Type;
		public int Width;
		public int Height;
		public int WidthBytes;
		public ushort Planes;
		public ushort BitsPixel;
		public IntPtr Bits;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BitmapInfo
	{
		public int Size;
		public int Width;
		public int Height;
		public short Planes;
		public short BitCount;
		public int Compression;
		public int SizeImage;
		public int XPelsPerMeter;
		public int YPelsPerMeter;
		public int ClrUsed;
		public int ClrImportant;
		public int Colors;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, IntPtr ids, uint count, uint flags);

	[DllImport("user32.dll")]
	private static extern bool DestroyIcon(IntPtr icon);

	[DllImport("user32.dll")]
	private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

	[DllImport("gdi32.dll")]
	private static extern int GetObject(IntPtr handle, int size, out BitmapHeader header);

	[DllImport("gdi32.dll")]
	private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteObject(IntPtr handle);

	[DllImport("user32.dll")]
	private static extern IntPtr GetDC(IntPtr window);

	[DllImport("user32.dll")]
	private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
