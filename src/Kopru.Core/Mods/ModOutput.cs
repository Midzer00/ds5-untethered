// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Protocol;

namespace Kopru.Core.Mods;

// What a game mod asked of one controller (DSX mod support): per trigger an effect (11 bytes, TriggerEffects) and a
// threshold (0-255; the trigger reads as released below it, 0 = none), the light bar colour (brightness already applied),
// the player LED mask (bits 0-4) and the mute LED value (OutputBlock: 0 off, 1 on, 2 pulse). Null: the mod did not set it.
public sealed record ModOutput
{
	public static readonly ModOutput Empty = new();

	public byte[]? RightTrigger { get; init; }
	public byte[]? LeftTrigger { get; init; }
	public byte RightThreshold { get; init; }
	public byte LeftThreshold { get; init; }
	public (byte R, byte G, byte B)? Lightbar { get; init; }
	public byte? PlayerLeds { get; init; }
	public byte? MicLed { get; init; }

	public bool HasTriggers => RightTrigger != null || LeftTrigger != null;

	public bool HasLight => Lightbar != null || PlayerLeds != null || MicLed != null;

	public bool HasThresholds => RightThreshold != 0 || LeftThreshold != 0;

	public bool IsEmpty => !HasTriggers && !HasLight && !HasThresholds;

	public bool Equals(ModOutput? other) => other is not null && Same(RightTrigger, other.RightTrigger) && Same(LeftTrigger, other.LeftTrigger)
		&& RightThreshold == other.RightThreshold && LeftThreshold == other.LeftThreshold && Lightbar == other.Lightbar
		&& PlayerLeds == other.PlayerLeds && MicLed == other.MicLed;

	public override int GetHashCode() => HashCode.Combine(RightThreshold, LeftThreshold, Lightbar, PlayerLeds, MicLed,
		RightTrigger?.Length ?? -1, LeftTrigger?.Length ?? -1);

	private static bool Same(byte[]? a, byte[]? b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);

	// Writes the mod's light fields (with their valid flags) into an output block; the rest stays as it is. lights false
	// (Low Power, the user's light turned off): the light bar and player LEDs are left out, only the mute LED is written.
	public void WriteLight(Span<byte> block, bool lights = true)
	{
		if (lights && Lightbar is { } c)
		{
			OutputBlock.SetLightbar(block, c.R, c.G, c.B);
		}
		if (lights && PlayerLeds is { } leds)
		{
			block[OutputBlock.Flag1] |= OutputBlock.PlayerLedControl;
			block[OutputBlock.PlayerLeds] = (byte)(leds & 0x1F);
		}
		if (MicLed is { } mic)
		{
			OutputBlock.SetMuteLed(block, mic);
		}
	}
}
