// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Protocol;

namespace Kopru.Core.Lighting;

// The light last written to a controller (BluetoothSession.SentLight): the light bar colour as sent (brightness is part
// of it), the player LED mask (bits 0..4) and their brightness byte, and how many times any of it changed (a cheap test
// for "anything new" for whoever shows it). Whoever set it: the app's light, the game, Low Power, the mute LED path.
public sealed record LightOutput(byte R, byte G, byte B, byte PlayerLeds, byte LedBrightness, long Changes)
{
	public static readonly LightOutput None = new(0, 0, 0, 0, OutputBlock.LedBrightnessHigh, 0);

	public bool Lit => R != 0 || G != 0 || B != 0;

	// The player LEDs' brightness as a level 0..1 (the controller's three steps).
	public double PlayerLedLevel => LedBrightness switch
	{
		OutputBlock.LedBrightnessLow => 0.4,
		OutputBlock.LedBrightnessMedium => 0.7,
		_ => 1.0
	};

	// The light fields an output block about to be sent writes (only those with their control bit set), applied to this
	// one; the same instance when nothing changes.
	public LightOutput After(ReadOnlySpan<byte> block)
	{
		byte r = R, g = G, b = B, leds = PlayerLeds, brightness = LedBrightness;
		if ((block[OutputBlock.Flag1] & OutputBlock.LightbarControl) != 0)
		{
			(r, g, b) = (block[OutputBlock.Red], block[OutputBlock.Red + 1], block[OutputBlock.Red + 2]);
		}
		if ((block[OutputBlock.Flag1] & OutputBlock.PlayerLedControl) != 0)
		{
			leds = (byte)(block[OutputBlock.PlayerLeds] & 0x1F);
		}
		if ((block[OutputBlock.Flag2] & OutputBlock.LedBrightnessControl) != 0)
		{
			brightness = block[OutputBlock.LedBrightness];
		}
		return (r, g, b, leds, brightness) == (R, G, B, PlayerLeds, LedBrightness) ? this : new(r, g, b, leds, brightness, Changes + 1);
	}
}
