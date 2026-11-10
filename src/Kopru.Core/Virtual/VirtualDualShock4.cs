// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Kopru.Core.UsbIp;

namespace Kopru.Core.Virtual;

// A wired DualShock 4 (v2, VID 054C PID 09CC) made of software, served over USB/IP, for older games that only know the
// PS4 controller. Windows binds its in-box HID driver, so no extra driver is needed. HID only: the real pad's audio
// interfaces (headset jack) are left out, so the configuration has a single HID interface (number 0) with interrupt IN
// 0x84 (64-byte input report 0x01) and interrupt OUT 0x03 (output report 0x05). The report descriptor is our own,
// written from the public report layout (see DualShock4Reports): the usages follow the public DS4 convention (sticks
// X/Y/Z/Rz, triggers Rx/Ry, hat, 14 buttons, vendor bytes for the rest).
public sealed class VirtualDualShock4 : IVirtualPad
{
	public const string BusId = VirtualDualSense.BusId;
	public const ushort VendorId = 0x054C;
	public const ushort ProductId = 0x09CC;
	public const ushort BcdDevice = 0x0100;
	public const uint InEndpoint = 4;
	public const uint OutEndpoint = 3;

	// USB 2.0, class defined per interface, 64-byte EP0, bcdDevice 1.00, strings 1 manufacturer / 2 product, no serial.
	public static readonly byte[] DeviceDescriptor =
	[
		0x12, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x40, 0x4C, 0x05, 0xCC, 0x09, 0x00, 0x01, 0x01, 0x02, 0x00, 0x01
	];

	// The HID report descriptor (USB-IF HID 1.11 items).
	public static readonly byte[] ReportDescriptor =
	[
		0x05, 0x01,             // Usage Page (Generic Desktop)
		0x09, 0x05,             // Usage (Game Pad)
		0xA1, 0x01,             // Collection (Application)
		0x85, 0x01,             //   Report ID 0x01: input, 63 bytes
		0x09, 0x30, 0x09, 0x31, 0x09, 0x32, 0x09, 0x35, // Usage X, Y, Z, Rz (left X/Y, right X/Y)
		0x15, 0x00, 0x26, 0xFF, 0x00, // Logical 0..255
		0x75, 0x08, 0x95, 0x04, 0x81, 0x02, // 4 x 8 bits, Input (Data, Var, Abs)
		0x09, 0x39,             //   Usage (Hat Switch)
		0x15, 0x00, 0x25, 0x07, // Logical 0..7
		0x35, 0x00, 0x46, 0x3B, 0x01, // Physical 0..315
		0x65, 0x14,             //   Unit (degrees)
		0x75, 0x04, 0x95, 0x01, 0x81, 0x42, // 1 x 4 bits, Input (Data, Var, Abs, Null state)
		0x65, 0x00, 0x45, 0x00, //   Unit (none), Physical Maximum 0 (back to logical)
		0x05, 0x09, 0x19, 0x01, 0x29, 0x0E, // Buttons 1..14
		0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x0E, 0x81, 0x02,
		0x06, 0x00, 0xFF, 0x09, 0x20, // Vendor usage: report counter
		0x75, 0x06, 0x95, 0x01, 0x25, 0x3F, 0x81, 0x02,
		0x05, 0x01, 0x09, 0x33, 0x09, 0x34, // Usage Rx, Ry (L2, R2)
		0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x95, 0x02, 0x81, 0x02,
		0x06, 0x00, 0xFF, 0x09, 0x21, // Vendor: timestamp, IMU, battery, touchpad (54 bytes)
		0x95, 0x36, 0x81, 0x02,
		0x85, 0x05, 0x09, 0x22, 0x95, 0x1F, 0x91, 0x02, // Report ID 0x05: output, 31 bytes
		0x85, 0x02, 0x09, 0x24, 0x95, 0x24, 0xB1, 0x02, // Report ID 0x02: feature, 36 bytes (calibration)
		0x85, 0x12, 0x09, 0x25, 0x95, 0x0F, 0xB1, 0x02, // Report ID 0x12: feature, 15 bytes (pairing info)
		0x85, 0x81, 0x09, 0x26, 0x95, 0x06, 0xB1, 0x02, // Report ID 0x81: feature, 6 bytes (address)
		0x85, 0xA3, 0x09, 0x27, 0x95, 0x30, 0xB1, 0x02, // Report ID 0xA3: feature, 48 bytes (firmware)
		0xC0                    // End Collection
	];

	// HID 1.11, country 0, one report descriptor.
	public static readonly byte[] HidDescriptor =
	[
		0x09, 0x21, 0x11, 0x01, 0x00, 0x01, 0x22, (byte)ReportDescriptor.Length, (byte)(ReportDescriptor.Length >> 8)
	];

	// One configuration, one HID interface; bus powered at 500 mA. Interrupt endpoints of 64 bytes polled every 5 ms.
	public static readonly byte[] ConfigurationDescriptor =
	[
		0x09, 0x02, 0x29, 0x00, 0x01, 0x01, 0x00, 0xC0, 0xFA,
		0x09, 0x04, 0x00, 0x00, 0x02, 0x03, 0x00, 0x00, 0x00,
		.. HidDescriptor,
		0x07, 0x05, 0x84, 0x03, 0x40, 0x00, 0x05,
		0x07, 0x05, 0x03, 0x03, 0x40, 0x00, 0x05
	];

	private static readonly Dictionary<byte, string> Strings = new()
	{
		[1] = "Sony Interactive Entertainment",
		[2] = "Wireless Controller"
	};

	private readonly object gate = new();

	private readonly Queue<(uint Sequence, Action<SubmitResult> Complete)> inputWaiting = new();

	private readonly Dictionary<byte, byte[]> features;

	private byte[] latestInput = DualShock4Reports.Neutral();

	private byte[] lastTouch = new byte[8];

	private int counter;

	private byte touchCounter;

	private byte configuration;

	// dualSenseCalibration: the USB form of the controller's DualSense feature 0x05 (null: neutral values).
	public VirtualDualShock4(string busId = BusId, byte[]? dualSenseCalibration = null)
	{
		Info = new UsbIpDeviceInfo("/kopru/dualshock4", busId, 1, 1, UsbIpCodes.SpeedFull, VendorId, ProductId, BcdDevice, 0, 0, 0, 1, 1,
			[(0x03, 0x00, 0x00)]);
		Address = AddressFor(busId);
		features = new Dictionary<byte, byte[]>
		{
			[DualShock4Reports.CalibrationFeature] = DualShock4Reports.Calibration(dualSenseCalibration),
			[DualShock4Reports.PairingFeature] = DualShock4Reports.Pairing(Address),
			[DualShock4Reports.AddressFeature] = DualShock4Reports.Address(Address),
			[DualShock4Reports.FirmwareFeature] = DualShock4Reports.Firmware()
		};
	}

	public VirtualPadType Type => VirtualPadType.DualShock4;

	public UsbIpDeviceInfo Info { get; }

	// The pad's own made-up Bluetooth address (display order): locally administered (02:...), never a real one, and
	// distinct from the virtual DualSense's; the last byte is the player slot.
	public byte[] Address { get; }

	public static byte[] AddressFor(string busId)
	{
		int slot = busId.Length > 0 && char.IsAsciiDigit(busId[^1]) ? busId[^1] - '0' : 1;
		return [0x02, 0x4B, 0x50, 0x52, 0x34, (byte)Math.Clamp(slot, 1, 4)];
	}

	// The feature reports the pad answers (id -> report with the id).
	public IReadOnlyDictionary<byte, byte[]> FeatureReports => features;

	// DualSense-format output (0x02) with the game's rumble in the classic motor bytes and its lightbar colour.
	public event Action<byte[]>? OutputReceived;

	public event Action<ReadOnlyMemory<byte>>? AudioReceived
	{
		add { }
		remove { }
	}

	// The last DS4 input report games got (tests, diagnostics).
	public byte[] LatestInput
	{
		get
		{
			lock (gate)
			{
				return latestInput.ToArray();
			}
		}
	}

	public void PushInput(ReadOnlySpan<byte> usbInputReport)
	{
		byte[] report;
		(uint, Action<SubmitResult>)? waiting = null;
		lock (gate)
		{
			if (usbInputReport.Length >= 41 && !usbInputReport.Slice(33, 8).SequenceEqual(lastTouch))
			{
				usbInputReport.Slice(33, 8).CopyTo(lastTouch);
				touchCounter++;
			}
			report = DualShock4Reports.FromDualSense(usbInputReport, counter, touchCounter);
			counter = (counter + 1) & 0x3F;
			latestInput = report;
			if (inputWaiting.Count > 0)
			{
				waiting = inputWaiting.Dequeue();
			}
		}
		waiting?.Item2(SubmitResult.In(report));
	}

	public void Submit(SubmitCommand command, Action<SubmitResult> complete)
	{
		if (command.Endpoint == 0)
		{
			complete(Control(command.Setup, command.Data));
			return;
		}
		if (command.Endpoint == InEndpoint && command.IsIn)
		{
			lock (gate)
			{
				inputWaiting.Enqueue((command.Sequence, complete));
			}
			return;
		}
		if (command.Endpoint == OutEndpoint && !command.IsIn)
		{
			complete(SubmitResult.Ok(command.Length));
			Output(command.Data);
			return;
		}
		complete(SubmitResult.Stall());
	}

	public bool Unlink(uint sequence)
	{
		lock (gate)
		{
			int before = inputWaiting.Count;
			Queue<(uint, Action<SubmitResult>)> kept = new(inputWaiting.Where(w => w.Sequence != sequence));
			inputWaiting.Clear();
			foreach ((uint, Action<SubmitResult>) w in kept)
			{
				inputWaiting.Enqueue(w);
			}
			return inputWaiting.Count < before;
		}
	}

	public void Released()
	{
		lock (gate)
		{
			inputWaiting.Clear();
			configuration = 0;
		}
	}

	public void Dispose() => Released();

	private void Output(byte[] data)
	{
		if (DualShock4Reports.ToDualSense(data) is { } report)
		{
			OutputReceived?.Invoke(report);
		}
	}

	// A control transfer on endpoint 0 (public for tests and diagnostics).
	public SubmitResult Control(byte[] setup, byte[] data)
	{
		byte requestType = setup[0];
		byte request = setup[1];
		ushort value = (ushort)(setup[2] | (setup[3] << 8));
		ushort index = (ushort)(setup[4] | (setup[5] << 8));
		int length = setup[6] | (setup[7] << 8);
		int type = (requestType >> 5) & 3;
		int recipient = requestType & 0x1F;
		if (type == 0)
		{
			switch (request)
			{
				case 6:
					return Descriptor((byte)(value >> 8), (byte)value, length);
				case 0:
					return Answer([0, 0], length);
				case 8:
					lock (gate)
					{
						return Answer([configuration], length);
					}
				case 9:
					lock (gate)
					{
						configuration = (byte)value;
					}
					return SubmitResult.Ok();
				case 10:
					return Answer([0], length);
				case 1:
				case 3:
				case 11:
					return SubmitResult.Ok();
				default:
					return SubmitResult.Stall();
			}
		}
		if (type == 1 && recipient == 1 && index == 0)
		{
			return HidRequest(request, value, data, length);
		}
		return SubmitResult.Stall();
	}

	private SubmitResult HidRequest(byte request, ushort value, byte[] data, int length)
	{
		byte reportType = (byte)(value >> 8);
		byte reportId = (byte)value;
		switch (request)
		{
			case 1 when reportType == 3:
				return features.TryGetValue(reportId, out byte[]? feature) ? Answer(feature, length) : SubmitResult.Stall();
			case 1 when reportType == 1:
				lock (gate)
				{
					return Answer(latestInput, length);
				}
			case 9 when reportType == 2:
				Output(data);
				return SubmitResult.Ok(data.Length);
			case 9 when reportType == 3:
				// Writes (e.g. a host pairing request) are accepted and ignored.
				return SubmitResult.Ok(data.Length);
			case 0x0A:
			case 0x0B:
				return SubmitResult.Ok();
			case 2:
			case 3:
				return Answer([0], length);
			default:
				return SubmitResult.Stall();
		}
	}

	private static SubmitResult Descriptor(byte type, byte index, int length) => type switch
	{
		1 => Answer(DeviceDescriptor, length),
		2 => Answer(ConfigurationDescriptor, length),
		3 when index == 0 => Answer([4, 3, 0x09, 0x04], length),
		3 when Strings.TryGetValue(index, out string? text) => Answer(StringDescriptor(text), length),
		0x21 => Answer(HidDescriptor, length),
		0x22 => Answer(ReportDescriptor, length),
		_ => SubmitResult.Stall()
	};

	private static SubmitResult Answer(byte[] data, int length) => SubmitResult.In(data[..Math.Min(length, data.Length)]);

	private static byte[] StringDescriptor(string text)
	{
		byte[] body = Encoding.Unicode.GetBytes(text);
		return [(byte)(body.Length + 2), 3, .. body];
	}
}
