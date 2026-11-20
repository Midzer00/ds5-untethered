// SPDX-License-Identifier: GPL-3.0-or-later
using Kopru.Core.Protocol;

namespace Kopru.Platform.Linux.Diagnostics;

// What one output report a game sent to the virtual USB DualSense asks for (report 0x02: the report ID, then the 47-byte
// block of OutputBlock). For the spike's log; null for anything else.
public readonly record struct OutputReportInfo(bool RightTrigger, byte RightMode, bool LeftTrigger, byte LeftMode, bool Rumble,
	byte RumbleRight, byte RumbleLeft, bool HapticsSelect, bool Lightbar)
{
	public bool AnyTrigger => RightTrigger || LeftTrigger;

	public static OutputReportInfo? Parse(ReadOnlySpan<byte> report)
	{
		if (report.Length < 1 + OutputBlock.Size || report[0] != UsbReports.OutputId)
		{
			return null;
		}
		ReadOnlySpan<byte> block = report[1..];
		byte flag0 = block[OutputBlock.Flag0];
		byte flag1 = block[OutputBlock.Flag1];
		return new OutputReportInfo(
			(flag0 & OutputBlock.RightTriggerEffect) != 0, block[OutputBlock.RightTrigger],
			(flag0 & OutputBlock.LeftTriggerEffect) != 0, block[OutputBlock.LeftTrigger],
			(flag0 & OutputBlock.CompatibleVibration) != 0, block[OutputBlock.MotorRight], block[OutputBlock.MotorLeft],
			(flag0 & OutputBlock.HapticsSelect) != 0,
			(flag1 & OutputBlock.LightbarControl) != 0);
	}

	public string Describe()
	{
		List<string> parts = [];
		if (RightTrigger)
		{
			parts.Add($"R2 effect 0x{RightMode:X2}");
		}
		if (LeftTrigger)
		{
			parts.Add($"L2 effect 0x{LeftMode:X2}");
		}
		if (Rumble)
		{
			parts.Add($"rumble {RumbleLeft}/{RumbleRight}");
		}
		if (HapticsSelect)
		{
			parts.Add("haptics select");
		}
		if (Lightbar)
		{
			parts.Add("lightbar");
		}
		return parts.Count > 0 ? string.Join(", ", parts) : "no flags";
	}
}
