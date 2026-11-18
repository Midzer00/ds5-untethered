// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Kopru.Core.Protocol;

namespace Kopru.Core.Virtual;

// The Gaming Input Protocol (GIP) of wired Xbox One / Series controllers, as published by Microsoft in the open
// specification [MS-GIPUSB] 1.0 (2024-09-16, learn.microsoft.com/openspecs/windows_protocols/ms-gipusb). Written from
// that specification only. Section numbers below are the specification's.
//
// Every message starts with a header (2.2.10): type, flags, sequence id, payload length (7 bits per byte, bit 7 = more
// bytes follow), and for a fragment one more such number: the total length (first fragment) or the offset. Several
// messages may be packed into one USB transfer (3.1.5.3).
public static class Gip
{
	// Message types (3.1.5.4, table 26).
	public const byte ProtocolControl = 0x01;
	public const byte Hello = 0x02;
	public const byte Status = 0x03;
	public const byte Metadata = 0x04;
	public const byte SetDeviceState = 0x05;
	public const byte Security = 0x06;
	public const byte GuideButton = 0x07;
	public const byte AudioControl = 0x08;
	public const byte DirectMotor = 0x09;
	public const byte Led = 0x0A;
	public const byte Extended = 0x1E;
	public const byte Debug = 0x1F;
	public const byte Input = 0x20;

	// Flags (2.2.10.2, table 14).
	public const byte FlagFragment = 0x80;
	public const byte FlagInitFragment = 0x40;
	public const byte FlagSystem = 0x20;
	public const byte FlagAckMe = 0x10;

	// Set Device State values (3.1.5.5.5, table 40).
	public const byte StateStart = 0x00;
	public const byte StateStop = 0x01;
	public const byte StateFullPower = 0x03;
	public const byte StateOff = 0x04;
	public const byte StateQuiesce = 0x05;
	public const byte StateReset = 0x07;

	// MTU of the command and low latency classes (2.2.10.4) = the 64-byte interrupt packet.
	public const int MaxPacket = 64;

	// Data bytes in a metadata fragment with a 6-byte header (3.1.5.5.4.1-2: "58 bytes of data").
	public const int FragmentData = MaxPacket - 6;

	// Status field (3.1.5.5.2.2.1): power level "full power" (bits 7:6 = 10), not charging, battery absent, battery level
	// "critically low" — what table 31 asks of a wired device without batteries. Powering off: all zero (allowed only for
	// exactly that kind of device).
	public const byte StatusWiredFullPower = 0x80;
	public const byte StatusPoweringOff = 0x00;

	// The Xbox button as a virtual key (2.2.2.4.1: "Key Input ... the Xbox button is in the form of a VKEY (0x5B or
	// VK_LWIN)").
	public const byte GuideVirtualKey = 0x5B;

	public static string TypeName(byte type) => type switch
	{
		ProtocolControl => "Protocol Control",
		Hello => "Hello",
		Status => "Status",
		Metadata => "Metadata",
		SetDeviceState => "Set Device State",
		Security => "Security",
		GuideButton => "Guide Button",
		AudioControl => "Audio Control",
		DirectMotor => "Direct Motor",
		Led => "LED",
		Extended => "Extended Command",
		Debug => "Debug",
		Input => "Input Report",
		_ => string.Create(CultureInfo.InvariantCulture, $"type 0x{type:X2}")
	};

	public static string StateName(byte state) => state switch
	{
		StateStart => "START",
		StateStop => "STOP",
		StateFullPower => "FULL POWER",
		StateOff => "OFF",
		StateQuiesce => "QUIESCE",
		StateReset => "RESET",
		_ => string.Create(CultureInfo.InvariantCulture, $"0x{state:X2}")
	};

	// A length/offset field: 7 bits per byte, low bits first, bit 7 set when another byte follows (at most 4 bytes).
	public static bool TryReadNumber(ReadOnlySpan<byte> data, ref int at, out int value)
	{
		value = 0;
		for (int i = 0; i < 4; i++)
		{
			if (at >= data.Length)
			{
				return false;
			}
			byte b = data[at++];
			value |= (b & 0x7F) << (7 * i);
			if ((b & 0x80) == 0)
			{
				return true;
			}
		}
		return false;
	}

	// The two-byte form the specification uses to keep 6-byte headers even: low 7 bits | 0x80, then the next 7 bits.
	public static (byte Low, byte High) TwoByteNumber(int value) => ((byte)(0x80 | (value & 0x7F)), (byte)((value >> 7) & 0x7F));

	// Every message in one transfer, in order. Stops at the first header that does not fit (truncated = true).
	public static List<GipMessage> ParseAll(ReadOnlySpan<byte> packet, out bool truncated)
	{
		List<GipMessage> messages = [];
		int at = 0;
		truncated = false;
		while (at < packet.Length)
		{
			int start = at;
			if (packet.Length - at < 4)
			{
				truncated = true;
				break;
			}
			byte type = packet[at], flags = packet[at + 1], sequence = packet[at + 2];
			at += 3;
			if (!TryReadNumber(packet, ref at, out int length))
			{
				truncated = true;
				break;
			}
			int? extra = null;
			if ((flags & FlagFragment) != 0)
			{
				if (!TryReadNumber(packet, ref at, out int value))
				{
					truncated = true;
					break;
				}
				extra = value;
			}
			if (packet.Length - at < length)
			{
				truncated = true;
				break;
			}
			messages.Add(new GipMessage(type, flags, sequence, extra, at - start, packet.Slice(at, length).ToArray()));
			at += length;
		}
		return messages;
	}

	public static string Hex(ReadOnlySpan<byte> data, int max = 64)
	{
		StringBuilder s = new();
		for (int i = 0; i < data.Length && i < max; i++)
		{
			if (i > 0)
			{
				s.Append(' ');
			}
			s.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
		}
		if (data.Length > max)
		{
			s.Append(" ...");
		}
		return s.ToString();
	}
}

// One GIP message. TotalOrOffset: for a fragment, the total length (first fragment) or the fragment's offset.
public sealed record GipMessage(byte Type, byte Flags, byte Sequence, int? TotalOrOffset, int HeaderSize, byte[] Payload)
{
	public bool IsFragment => (Flags & Gip.FlagFragment) != 0;

	public bool WantsAck => (Flags & Gip.FlagAckMe) != 0;

	public bool IsSystem => (Flags & Gip.FlagSystem) != 0;
}

// Who the virtual pad says it is: USB VID/PID/bcdDevice and strings, and the GIP firmware version in Hello and metadata.
// Share: the pad has the Series Share button (the "Console Function Map" extension: 32-byte input report, its interface
// in the metadata).
public sealed record GipIdentity(string Name, ushort VendorId, ushort ProductId, ushort BcdDevice, string Manufacturer, string Product,
	bool Share = false)
{
	// The GIP firmware version (Hello, 3.1.5.5.1): major.minor must be listed in the metadata; 0.0.0.0 is invalid.
	public const ushort FirmwareMajor = 1;
	public const ushort FirmwareMinor = 0;
	public const ushort FirmwareBuild = 1;
	public const ushort FirmwareRevision = 0;

	// The host keeps metadata per VID/PID/firmware version until a reboot (3.1.5.5.4): the pad with the Share button says
	// build 2, so metadata cached from a pad without it (the 14-byte report) is never reused for it.
	public ushort Build => Share ? (ushort)2 : FirmwareBuild;

	// The input report payload size (14, or 32 with Share).
	public int InputSize => Share ? GipReports.InputPayloadSizeWithShare : GipReports.InputPayloadSize;

	// A generic GIP device that Windows can only recognise by the compatible ID XGIP10 from the Microsoft OS descriptor
	// (2.2.6). 1209:0001 is the pid.codes test PID, meant for private testing; nothing real ships with it.
	public static readonly GipIdentity Generic = new("generic", 0x1209, 0x0001, 0x0100, "DS5 Untethered", "Virtual GIP Controller (experimental)");

	// A Microsoft Xbox One S controller (model 1708), one of the IDs in Windows' dc1-controller.inf.
	public static readonly GipIdentity XboxOneS = new("Xbox One S 045E:02EA", 0x045E, 0x02EA, 0x0100, "Microsoft", "Controller");

	// A Microsoft Xbox Series controller (model 1914), also in dc1-controller.inf, with its Share button.
	public static readonly GipIdentity XboxSeries = new("Xbox Series 045E:0B12", 0x045E, 0x0B12, 0x0100, "Microsoft", "Controller", Share: true);

	public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Name} ({VendorId:X4}:{ProductId:X4})");
}

// One entry of the metadata's message list (2.2.2.5).
public sealed record GipMetadataMessage(byte Type, ushort Length, bool Upstream);

// The device metadata (2.2.2): the capability blob the host asks for once per VID/PID/firmware version. The binary form
// is what Microsoft's metadata compiler produces; its layout here is read from the specification's compiled example (the
// 182-byte blob in 2.2.2 for the example gamepad JSON), and the tests check that building that JSON gives exactly those
// bytes:
//   header (16): u16 header size 16, u16 major 1, u16 minor 0, 8 reserved, u16 total length
//   device block: u16 block size, u16 offsets (from the block start) of firmware versions, audio formats, in system
//     commands, out system commands, preferred types, interfaces, HID descriptor (0 = none), 6 reserved; then the lists:
//     u8 count + (u16 major, u16 minor) each; u8 count of audio formats (0); u8 count + command bytes (in, then out);
//     u8 count + (u16 length + ASCII) each type; u8 count + 16-byte GUIDs (Windows byte order)
//   messages: u8 count, then per message 23 bytes: u16 record size 23, u8 type, u16 length, u16 data type (1 = custom),
//     u16 direction (0x10 upstream, 0x08 downstream), zeros (period, persistence timeout, reserved)
public static class GipMetadata
{
	public static readonly Guid IController = new("9776FF56-9BFD-4581-AD45-B645BBA526D6");
	public static readonly Guid IGamepad = new("082E402C-07DF-45E1-A5AB-A3127AF197B5");
	public static readonly Guid INavigationController = new("B8F31FE7-7386-40E9-A9F8-2F21263ACFB7");

	// 5.1: "Controllers that talk to a Windows PC SHOULD use the following GUID to opt-out of the security exchange over
	// USB. You SHOULD add it to the SupportedInterfaces section of the metadata."
	public static readonly Guid SecurityOptOut = new("7A34CE77-7DE2-45C6-8CA4-0042C08BD94A");

	// The Share button ("Console Function Map", 3.1.5.6.1.1): the input report grows to 32 bytes, byte 14 is the button.
	public static readonly Guid ConsoleFunctionMap = new("ECDDD2FE-D387-4294-BD96-1A712E3DC77D");

	public const string GamepadType = "Windows.Xbox.Input.Gamepad";

	// The specification's example gamepad (2.2.2), optionally with the security opt-out interface added, and with share the
	// Console Function Map interface and the 32-byte input report.
	public static byte[] Gamepad(bool securityOptOut, bool share = false)
	{
		List<Guid> interfaces = [IController, IGamepad, INavigationController];
		if (share)
		{
			interfaces.Add(ConsoleFunctionMap);
		}
		if (securityOptOut)
		{
			interfaces.Add(SecurityOptOut);
		}
		return Build(
			[(GipIdentity.FirmwareMajor, GipIdentity.FirmwareMinor)],
			[1, 2, 3, 4, 6, 7],
			[1, 4, 5, 6, 10],
			[GamepadType],
			interfaces,
			[new GipMetadataMessage(Gip.Input, (ushort)(share ? GipReports.InputPayloadSizeWithShare : GipReports.InputPayloadSize), true),
				new GipMetadataMessage(Gip.DirectMotor, GipReports.MotorPayloadSize, false)]);
	}

	public static byte[] Build(IReadOnlyList<(ushort Major, ushort Minor)> firmware, IReadOnlyList<byte> inSystem, IReadOnlyList<byte> outSystem,
		IReadOnlyList<string> preferredTypes, IReadOnlyList<Guid> interfaces, IReadOnlyList<GipMetadataMessage> messages)
	{
		const int deviceHeader = 22;
		List<byte> device = [];
		int[] offsets = new int[6];
		offsets[0] = deviceHeader + device.Count;
		device.Add((byte)firmware.Count);
		foreach ((ushort major, ushort minor) in firmware)
		{
			AddU16(device, major);
			AddU16(device, minor);
		}
		offsets[1] = deviceHeader + device.Count;
		device.Add(0);
		offsets[2] = deviceHeader + device.Count;
		device.Add((byte)inSystem.Count);
		device.AddRange(inSystem);
		offsets[3] = deviceHeader + device.Count;
		device.Add((byte)outSystem.Count);
		device.AddRange(outSystem);
		offsets[4] = deviceHeader + device.Count;
		device.Add((byte)preferredTypes.Count);
		foreach (string type in preferredTypes)
		{
			byte[] text = Encoding.ASCII.GetBytes(type);
			AddU16(device, (ushort)text.Length);
			device.AddRange(text);
		}
		offsets[5] = deviceHeader + device.Count;
		device.Add((byte)interfaces.Count);
		foreach (Guid id in interfaces)
		{
			device.AddRange(id.ToByteArray());
		}

		List<byte> blob = [];
		AddU16(blob, 16);
		AddU16(blob, 1);
		AddU16(blob, 0);
		blob.AddRange(new byte[8]);
		AddU16(blob, 0); // total length, set below
		AddU16(blob, (ushort)(deviceHeader + device.Count));
		foreach (int offset in offsets)
		{
			AddU16(blob, (ushort)offset);
		}
		AddU16(blob, 0); // no HID descriptor
		blob.AddRange(new byte[6]);
		blob.AddRange(device);
		blob.Add((byte)messages.Count);
		foreach (GipMetadataMessage m in messages)
		{
			byte[] record = new byte[23];
			BinaryPrimitives.WriteUInt16LittleEndian(record, 23);
			record[2] = m.Type;
			BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(3), m.Length);
			BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(5), 1);
			BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(7), (ushort)(m.Upstream ? 0x10 : 0x08));
			blob.AddRange(record);
		}
		byte[] result = [.. blob];
		BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14), (ushort)result.Length);
		return result;
	}

	private static void AddU16(List<byte> list, ushort value)
	{
		list.Add((byte)value);
		list.Add((byte)(value >> 8));
	}
}

// A Direct Motor Command (3.1.5.6.1, table 56): which motors it sets (bitmap), their levels in percent (0-100), and how
// long it plays: duration in 10 ms steps (0 = stop every motor), a delay in 10 ms steps and a repeat count.
public readonly record struct GipMotorCommand(byte Bitmap, byte LeftTrigger, byte RightTrigger, byte LeftMain, byte RightMain, byte Duration,
	byte Delay = 0, byte Repeat = 0)
{
	public const byte LeftTriggerBit = 0x08, RightTriggerBit = 0x04, LeftMainBit = 0x02, RightMainBit = 0x01;

	public bool SetsLeftTrigger => (Bitmap & LeftTriggerBit) != 0;

	public bool SetsRightTrigger => (Bitmap & RightTriggerBit) != 0;

	public bool SetsLeftMain => (Bitmap & LeftMainBit) != 0;

	public bool SetsRightMain => (Bitmap & RightMainBit) != 0;

	public bool SetsTriggers => (Bitmap & (LeftTriggerBit | RightTriggerBit)) != 0;

	public bool StopsAll => Duration == 0;

	// How long the command keeps the motors running, in ms. The specification gives the three fields but not how delay
	// and repeats combine; this is the upper bound (every repeat waits the delay, then plays the duration).
	public int PlayMs => StopsAll ? 0 : (Delay + Duration) * (Repeat + 1) * 10;

	public override string ToString()
	{
		string motors = string.Join(' ', new[] { (SetsLeftTrigger, "LT"), (SetsRightTrigger, "RT"), (SetsLeftMain, "LM"), (SetsRightMain, "RM") }
			.Where(m => m.Item1).Select(m => m.Item2));
		return string.Create(CultureInfo.InvariantCulture,
			$"bitmap 0x{Bitmap:X2} ({(motors.Length > 0 ? motors : "none")}), levels LT {LeftTrigger} RT {RightTrigger} LM {LeftMain} RM {RightMain}, " +
			$"duration {Duration} ({Duration * 10} ms), delay {Delay}, repeat {Repeat}");
	}
}

// The levels the motors play now, in percent (0-100).
public readonly record struct GipMotorLevels(byte LeftMain, byte RightMain, byte LeftTrigger, byte RightTrigger)
{
	public bool AnyOn => (LeftMain | RightMain | LeftTrigger | RightTrigger) != 0;
}

// The four motors as Direct Motor Commands leave them. A command sets the motors in its bitmap; each motor keeps its
// own timing, so a command for the main motors neither cuts a trigger rumble short nor stretches it. A motor stops on its
// own when its command's time is over (the device is expected to do that; the host refreshes a held rumble), at once
// with a level-0 command for it, and every motor stops with duration 0 (Apply) or Stop.
//
// Timing: the specification gives duration, delay and repeat (10 ms steps) but not how they combine. We read a command
// as repeat + 1 cycles of "wait the delay, then play the duration" (the same reading as GipMotorCommand.PlayMs). With
// delay 0, which is what Windows sent in the hardware probe (duration 255, delay 0, repeat 235), that is one steady
// stretch of (repeat + 1) x duration; with a delay the motor pulses. The pad's timer runs every 20 ms, so shorter
// pulses are played no finer than that.
public sealed class GipMotors
{
	// LeftMain, RightMain, LeftTrigger, RightTrigger (the GipMotorLevels order).
	private readonly Motor[] motors = new Motor[4];

	private GipMotorLevels levels;

	public GipMotorLevels Levels => levels;

	// Motors (all four / the two trigger motors) that stopped because their command's time ran out, since ResetCounts.
	public int Expired { get; private set; }

	public int ExpiredTriggers { get; private set; }

	public void ResetCounts() => Expired = ExpiredTriggers = 0;

	// True when the levels changed.
	public bool Apply(GipMotorCommand command, long nowMs)
	{
		if (command.StopsAll)
		{
			return Stop();
		}
		if (command.SetsLeftMain)
		{
			motors[0] = Motor.For(command.LeftMain, command, nowMs);
		}
		if (command.SetsRightMain)
		{
			motors[1] = Motor.For(command.RightMain, command, nowMs);
		}
		if (command.SetsLeftTrigger)
		{
			motors[2] = Motor.For(command.LeftTrigger, command, nowMs);
		}
		if (command.SetsRightTrigger)
		{
			motors[3] = Motor.For(command.RightTrigger, command, nowMs);
		}
		return Update(nowMs);
	}

	// Follows the commands' timing (pulses, the end). True when the levels changed.
	public bool Tick(long nowMs) => Update(nowMs);

	public bool Stop()
	{
		Array.Clear(motors);
		return Set(default);
	}

	// Clears the main and/or trigger motors without counting it as a change (the bridge stopped what they played): the
	// next command for them is a change again, even if it repeats the last one.
	public void Forget(bool main, bool triggers)
	{
		if (main)
		{
			motors[0] = motors[1] = default;
		}
		if (triggers)
		{
			motors[2] = motors[3] = default;
		}
		levels = new GipMotorLevels(
			main ? (byte)0 : levels.LeftMain,
			main ? (byte)0 : levels.RightMain,
			triggers ? (byte)0 : levels.LeftTrigger,
			triggers ? (byte)0 : levels.RightTrigger);
	}

	private bool Update(long nowMs)
	{
		Span<byte> now = stackalloc byte[4];
		for (int i = 0; i < motors.Length; i++)
		{
			now[i] = motors[i].LevelAt(nowMs, out bool over);
			if (over)
			{
				motors[i] = default;
				Expired++;
				if (i >= 2)
				{
					ExpiredTriggers++;
				}
			}
		}
		return Set(new GipMotorLevels(now[0], now[1], now[2], now[3]));
	}

	private bool Set(GipMotorLevels next)
	{
		bool changed = next != levels;
		levels = next;
		return changed;
	}

	// One motor's command: level (1-100; 0 = idle), when it started and its cycles.
	private readonly record struct Motor(byte Level, long Start, int DelayMs, int OnMs, int Cycles)
	{
		public static Motor For(byte level, GipMotorCommand command, long nowMs) => level == 0 ? default
			: new Motor(Math.Min(level, (byte)100), nowMs, command.Delay * 10, command.Duration * 10, command.Repeat + 1);

		// The level at nowMs; over: the command's time has just run out (the caller drops it).
		public byte LevelAt(long nowMs, out bool over)
		{
			over = false;
			if (Level == 0)
			{
				return 0;
			}
			long elapsed = Math.Max(0, nowMs - Start);
			long cycle = DelayMs + OnMs;
			if (elapsed >= cycle * Cycles)
			{
				over = true;
				return 0;
			}
			return elapsed % cycle >= DelayMs ? Level : (byte)0;
		}
	}
}

// GIP input and output payloads and the DualSense <-> GIP conversions.
public static class GipReports
{
	// Gamepad Input Report payload (3.1.5.6.1.1, table 57): 14 bytes.
	public const int InputPayloadSize = 14;

	// With the Share button (Console Function Map): 32 bytes, byte ShareByte is 1 while Share is held, the rest 0.
	public const int InputPayloadSizeWithShare = 32;
	public const int ShareByte = 14;

	// Direct Motor Command payload (3.1.5.6.1, table 56): 9 bytes.
	public const int MotorPayloadSize = 9;

	// Payload byte 0.
	public const byte Menu = 0x04, View = 0x08, A = 0x10, B = 0x20, X = 0x40, Y = 0x80;

	// Payload byte 1.
	public const byte DpadUp = 0x01, DpadDown = 0x02, DpadLeft = 0x04, DpadRight = 0x08, LB = 0x10, RB = 0x20, LeftStick = 0x40, RightStick = 0x80;

	public const ushort TriggerMax = 1023;

	// D-pad hat value (0 up, clockwise, 8 released) -> GIP D-pad bits.
	private static readonly byte[] Hat =
	[
		DpadUp, DpadUp | DpadRight, DpadRight, DpadDown | DpadRight, DpadDown, DpadDown | DpadLeft, DpadLeft, DpadUp | DpadLeft
	];

	// A DualSense USB input report (0x01, after the app's InputTransform) as a GIP gamepad input payload, plus the Guide
	// button (sent as its own message). Same choices as the Xbox 360 pad: Cross A, Circle B, Square X, Triangle Y, L1/R1
	// LB/RB, L2/R2 analog triggers (0-255 -> 0-1023), Create and touchpad click View, Options Menu, L3/R3 stick clicks, PS
	// Guide; mute is not mapped. Sticks are centred and Y is inverted (up positive), as for XInput. share: the pad has the
	// Share button (32-byte payload). Create stays View there too: XInput games never see Share and the Game Bar may take
	// it, so a game that wants Back/View would lose it; Share is never pressed (no button maps to it).
	public static byte[] FromDualSense(ReadOnlySpan<byte> report, out bool guide, bool share = false)
	{
		byte[] p = new byte[share ? InputPayloadSizeWithShare : InputPayloadSize];
		guide = false;
		if (report.Length < 11 || report[0] != UsbReports.InputId)
		{
			return p;
		}
		byte b0 = report[8], b1 = report[9], b2 = report[10];
		byte low = 0;
		if ((b1 & 0x20) != 0) low |= Menu;
		if ((b1 & 0x10) != 0 || (b2 & 0x02) != 0) low |= View;
		if ((b0 & 0x20) != 0) low |= A;
		if ((b0 & 0x40) != 0) low |= B;
		if ((b0 & 0x10) != 0) low |= X;
		if ((b0 & 0x80) != 0) low |= Y;
		int hat = b0 & 0x0F;
		byte high = hat < 8 ? Hat[hat] : (byte)0;
		if ((b1 & 0x01) != 0) high |= LB;
		if ((b1 & 0x02) != 0) high |= RB;
		if ((b1 & 0x40) != 0) high |= LeftStick;
		if ((b1 & 0x80) != 0) high |= RightStick;
		guide = (b2 & 0x01) != 0;
		p[0] = low;
		p[1] = high;
		BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), Trigger(report[5]));
		BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), Trigger(report[6]));
		BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(6), XboxReports.Axis(report[1]));
		BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(8), XboxReports.InvertedAxis(report[2]));
		BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(10), XboxReports.Axis(report[3]));
		BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(12), XboxReports.InvertedAxis(report[4]));
		return p;
	}

	// 0-255 -> 0-1023 (0 and 255 exact).
	public static ushort Trigger(byte value) => (ushort)((value * TriggerMax + 127) / 255);

	// A Direct Motor Command payload (sub-command 0x00); null for anything else.
	public static GipMotorCommand? ParseMotor(ReadOnlySpan<byte> payload)
	{
		if (payload.Length < MotorPayloadSize || payload[0] != 0x00)
		{
			return null;
		}
		return new GipMotorCommand((byte)(payload[1] & 0x0F), payload[2], payload[3], payload[4], payload[5], payload[6], payload[7], payload[8]);
	}

	// The main motors as a DualSense output report (0x02) carrying only the classic motor bytes, as the Xbox 360 pad does
	// (left = the heavy motor). Levels are percent; the DualSense bytes 0-255.
	public static byte[] RumbleToDualSense(GipMotorLevels levels)
	{
		byte[] report = new byte[UsbReports.OutputSize];
		report[0] = UsbReports.OutputId;
		report[1 + OutputBlock.Flag0] = OutputBlock.CompatibleVibration | OutputBlock.HapticsSelect;
		report[1 + OutputBlock.MotorLeft] = Percent(levels.LeftMain);
		report[1 + OutputBlock.MotorRight] = Percent(levels.RightMain);
		return report;
	}

	private static byte Percent(byte level) => (byte)((Math.Min(level, (byte)100) * 255 + 50) / 100);
}
