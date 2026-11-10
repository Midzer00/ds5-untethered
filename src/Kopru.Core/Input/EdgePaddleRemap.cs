// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Input;

// The DualSense Edge back paddles in the 63-byte input body (byte 9, buttons2; bit values from SDL's public PS5 HIDAPI
// driver documentation, not verified on hardware): 0x40 left paddle, 0x80 right paddle. The Fn buttons (0x10 left,
// 0x20 right) are reserved for future use: never consumed or changed here.
//
// Runs after the standard button remap. Each paddle's bit is cleared, then a pressed paddle presses its target: itself
// (passed through, for a pad presented as an Edge), a DualSense button, or nothing.
public static class EdgePaddleRemap
{
	public const byte LeftFunctionBit = 0x10, RightFunctionBit = 0x20, LeftBit = 0x40, RightBit = 0x80;

	private const int Buttons0 = ButtonRemap.Buttons0, Buttons1 = ButtonRemap.Buttons1, Buttons2 = ButtonRemap.Buttons2;
	private const int L2Analog = 4, R2Analog = 5;

	public static void Apply(Span<byte> body, DsButton left, DsButton right)
	{
		if (body.Length <= Buttons2)
			return;
		byte b2 = body[Buttons2];
		body[Buttons2] = (byte)(b2 & ~(LeftBit | RightBit));
		if ((b2 & LeftBit) != 0)
			Press(body, left);
		if ((b2 & RightBit) != 0)
			Press(body, right);
	}

	private static void Press(Span<byte> body, DsButton target)
	{
		switch (target)
		{
			case DsButton.None:
				return;
			case DsButton.DpadUp: AddDirection(body, 1); return;
			case DsButton.DpadRight: AddDirection(body, 2); return;
			case DsButton.DpadDown: AddDirection(body, 4); return;
			case DsButton.DpadLeft: AddDirection(body, 8); return;
			case DsButton.L2: body[L2Analog] = 255; break;
			case DsButton.R2: body[R2Analog] = 255; break;
		}
		(int offset, byte bit) = ButtonRemap.Location(target);
		if (offset >= 0)
			body[offset] |= bit;
	}

	private static void AddDirection(Span<byte> body, int direction)
	{
		int dirs = ButtonRemap.DirectionsOf(body[Buttons0] & ButtonRemap.HatMask) | direction;
		body[Buttons0] = (byte)((body[Buttons0] & 0xF0) | ButtonRemap.HatOf(dirs));
	}
}
