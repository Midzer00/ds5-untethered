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
// Public DualSense input layout (as documented by the Linux hid-playstation driver's input report struct): the status
// byte is body offset 52 (USB report byte 53, Bluetooth 0x31 report byte 54). Its low nibble is the battery level in
// steps of 10 % (0..10, larger values clamp to 100 %), its high nibble the charging state: 0 discharging, 1 charging,
// 2 fully charged, anything else an error (charging halted).
public readonly record struct BatteryStatus(int Percent, ChargeState State)
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

	public static BatteryStatus From(InputState input) =>
		FromStatusByte((byte)((input.ChargingState << 4) | Math.Min(input.BatteryLevel / 10, 10)));
}
