// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Protocol;

namespace Kopru.Core.Bridge;

public enum ChargeState
{
	Discharging,
	Charging,
	Full,
	// Charging stopped by the controller (temperature, voltage...) or a value we don't know.
	Error
}

// Battery from the DualSense input body (63 bytes; byte 1 of USB report 0x01, byte 2 of Bluetooth report 0x31).
// Public DualSense input report documentation: the status
// byte is body offset 52 (USB report byte 53, Bluetooth 0x31 report byte 54). Its low nibble is the battery level in
// steps of 10 % (0..10, larger values clamp to 100 %), its high nibble the charging state: 0 discharging, 1 charging,
// 2 fully charged, anything else an error (charging halted).
//
// LevelKnown: false when the level nibble can't be trusted. Over USB the controller was seen reporting "charging, level
// 0" (status byte 0x10) for a whole wired session while Bluetooth showed a real level: the charging state is real, the
// level is not reported. Such a reading is "charging, level unknown" (Percent 0, LevelKnown false), never "0 %".
public readonly record struct BatteryStatus(int Percent, ChargeState State, bool LevelKnown = true)
{
	public const int StatusOffset = 52;

	public static BatteryStatus Parse(ReadOnlySpan<byte> body) => FromStatusByte(body[StatusOffset]);

	public static BatteryStatus FromStatusByte(byte status)
	{
		ChargeState state = (status >> 4) switch
		{
			0 => ChargeState.Discharging,
			1 => ChargeState.Charging,
			2 => ChargeState.Full,
			_ => ChargeState.Error
		};
		int percent = state == ChargeState.Full ? 100 : Math.Min(status & 0x0F, 10) * 10;
		return new BatteryStatus(percent, state);
	}

	public static BatteryStatus From(InputState input) => FromStatusByte(StatusByte(input));

	// The battery of an input that came over the given link: over USB "charging, level 0" is charging with the level
	// unknown (see LevelKnown). Bluetooth readings are taken as they are.
	public static BatteryStatus From(InputState input, bool overUsb)
	{
		BatteryStatus b = From(input);
		return overUsb && b.State == ChargeState.Charging && b.Percent == 0 ? b with { LevelKnown = false } : b;
	}

	// The status byte an input state was parsed from (a level nibble above 10 comes back as 10).
	public static byte StatusByte(InputState input) => (byte)((input.ChargingState << 4) | Math.Min(input.BatteryLevel / 10, 10));
}
