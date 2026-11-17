// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Kopru.Core.UsbIp;

namespace Kopru.Core.Virtual;

// A wired DualShock 4 (v2, VID 054C PID 09CC) made of software, served over USB/IP, for older games that only know the
// PS4 controller. Windows binds its in-box HID and USB audio drivers, so no extra driver is needed. Laid out like the
// real pad, from public descriptions of its USB descriptors: one configuration (225 bytes) with the headset jack's audio
// on interfaces 0-2 (0 audio control; 1 speaker, isochronous OUT 0x01, 2 channels 32 kHz 16-bit; 2 microphone,
// isochronous IN 0x82, 1 channel 16 kHz 16-bit) and HID on interface 3 (interrupt IN 0x84, 64-byte input report 0x01;
// interrupt OUT 0x03, output report 0x05), so Windows makes it a composite device and the HID path ends in "&mi_03" as
// on a real pad. The HID report descriptor declares every report the real one does, with the same ids, usages and sizes
// (507 bytes); it is built here from that public report list (DualShock4Reports.Features). Speaker audio is accepted
// and dropped; the microphone sends silence.
public sealed class VirtualDualShock4 : IVirtualPad
{
	public const string BusId = VirtualDualSense.BusId;
	public const ushort VendorId = 0x054C;
	public const ushort ProductId = 0x09CC;
	public const ushort BcdDevice = 0x0100;
	public const uint InEndpoint = 4;
	public const uint OutEndpoint = 3;
	public const uint AudioOutEndpoint = 1;
	public const uint MicInEndpoint = 2;
	public const int HidInterface = 3;

	public const int SpeakerRate = 32000;
	public const int MicRate = 16000;

	// 16 kHz mono 16-bit: 16 samples per 1 ms frame.
	public const int MicBytesPerFrame = 32;

	// USB 2.0, class defined per interface, 64-byte EP0, bcdDevice 1.00, strings 1 manufacturer / 2 product, no serial.
	public static readonly byte[] DeviceDescriptor =
	[
		0x12, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x40, 0x4C, 0x05, 0xCC, 0x09, 0x00, 0x01, 0x01, 0x02, 0x00, 0x01
	];

	// The HID report descriptor (USB-IF HID 1.11 items).
	public static readonly byte[] ReportDescriptor = BuildReportDescriptor();

	// HID 1.11, country 0, one report descriptor.
	public static readonly byte[] HidDescriptor =
	[
		0x09, 0x21, 0x11, 0x01, 0x00, 0x01, 0x22, (byte)ReportDescriptor.Length, (byte)(ReportDescriptor.Length >> 8)
	];

	// One configuration, four interfaces; self powered, 500 mA. USB Audio Class 1 for the headset jack, then HID.
	public static readonly byte[] ConfigurationDescriptor =
	[
		0x09, 0x02, 0xE1, 0x00, 0x04, 0x01, 0x00, 0xC0, 0xFA,
		// Interface 0: audio control.
		0x09, 0x04, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00,
		0x0A, 0x24, 0x01, 0x00, 0x01, 0x47, 0x00, 0x02, 0x01, 0x02, // header: UAC 1.00, 71 bytes, streaming interfaces 1, 2
		0x0C, 0x24, 0x02, 0x01, 0x01, 0x01, 0x00, 0x02, 0x03, 0x00, 0x00, 0x00, // terminal 1: USB streaming, 2 channels (L, R)
		0x0A, 0x24, 0x06, 0x02, 0x01, 0x01, 0x03, 0x00, 0x00, 0x00, // unit 2: feature unit (master mute, volume) on terminal 1
		0x09, 0x24, 0x03, 0x03, 0x02, 0x04, 0x00, 0x02, 0x00,       // terminal 3: headset out, from unit 2
		0x0C, 0x24, 0x02, 0x04, 0x02, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, // terminal 4: headset in, 1 channel
		0x09, 0x24, 0x06, 0x05, 0x04, 0x01, 0x03, 0x00, 0x00,       // unit 5: feature unit (master mute, volume) on terminal 4
		0x09, 0x24, 0x03, 0x06, 0x01, 0x01, 0x00, 0x05, 0x00,       // terminal 6: USB streaming, from unit 5
		// Interface 1: speaker streaming; alternate 0 without bandwidth, alternate 1 with the endpoint.
		0x09, 0x04, 0x01, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00,
		0x09, 0x04, 0x01, 0x01, 0x01, 0x01, 0x02, 0x00, 0x00,
		0x07, 0x24, 0x01, 0x01, 0x01, 0x01, 0x00,                   // from terminal 1, delay 1, PCM
		0x0B, 0x24, 0x02, 0x01, 0x02, 0x02, 0x10, 0x01, 0x00, 0x7D, 0x00, // type I, 2 channels, 16-bit, 32000 Hz
		0x09, 0x05, 0x01, 0x09, 0x84, 0x00, 0x01, 0x00, 0x00,       // endpoint 0x01 OUT, isochronous adaptive, 132 bytes, every frame
		0x07, 0x25, 0x01, 0x00, 0x00, 0x00, 0x00,
		// Interface 2: microphone streaming.
		0x09, 0x04, 0x02, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00,
		0x09, 0x04, 0x02, 0x01, 0x01, 0x01, 0x02, 0x00, 0x00,
		0x07, 0x24, 0x01, 0x06, 0x01, 0x01, 0x00,                   // to terminal 6, delay 1, PCM
		0x0B, 0x24, 0x02, 0x01, 0x01, 0x02, 0x10, 0x01, 0x80, 0x3E, 0x00, // type I, 1 channel, 16-bit, 16000 Hz
		0x09, 0x05, 0x82, 0x05, 0x22, 0x00, 0x01, 0x00, 0x00,       // endpoint 0x82 IN, isochronous asynchronous, 34 bytes, every frame
		0x07, 0x25, 0x01, 0x00, 0x00, 0x00, 0x00,
		// Interface 3: HID; interrupt endpoints of 64 bytes, bInterval 5 as on the real pad.
		0x09, 0x04, 0x03, 0x00, 0x02, 0x03, 0x00, 0x00, 0x00,
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

	private readonly Dictionary<int, byte> alternateSettings = [];

	private readonly IsoFrameClock iso;

	private readonly UsbAudioControls audioControls = new(index => (index & 0xFF) == (0x80 | MicInEndpoint) ? MicRate : SpeakerRate);

	private readonly PadRequestLog requests;

	private byte[] latestInput = DualShock4Reports.Neutral();

	private byte[] lastTouch = new byte[8];

	private int counter;

	private byte touchCounter;

	private byte configuration;

	// dualSenseCalibration: the USB form of the controller's DualSense feature 0x05 (null: neutral values).
	public VirtualDualShock4(string busId = BusId, byte[]? dualSenseCalibration = null)
	{
		Info = new UsbIpDeviceInfo("/kopru/dualshock4", busId, 1, 1, UsbIpCodes.SpeedFull, VendorId, ProductId, BcdDevice, 0, 0, 0, 1, 1,
			[(0x01, 0x01, 0x00), (0x01, 0x02, 0x00), (0x01, 0x02, 0x00), (0x03, 0x00, 0x00)]);
		Address = AddressFor(busId);
		features = DualShock4Reports.FeatureAnswers(Address, dualSenseCalibration);
		iso = new IsoFrameClock("Virtual DualShock 4 frames", () => PreciseWaitFactory);
		requests = new PadRequestLog("DS4", busId, HidInterface, () => Log);
	}

	public VirtualPadType Type => VirtualPadType.DualShock4;

	public UsbIpDeviceInfo Info { get; }

	// Where the request diagnostics go (null: the app log; tests).
	public Action<string>? Log { get; init; }

	// Makes the wait the audio frame clock uses (tests); null = Bluetooth.PreciseWait.Create.
	public Func<Bluetooth.IPreciseWait>? PreciseWaitFactory { get; init; }

	// The pad's own made-up Bluetooth address (display order): locally administered (02:...), never a real one, and
	// distinct from the virtual DualSense's; the last byte is the player slot.
	public byte[] Address { get; }

	public static byte[] AddressFor(string busId)
	{
		int slot = busId.Length > 0 && char.IsAsciiDigit(busId[^1]) ? busId[^1] - '0' : 1;
		return [0x02, 0x4B, 0x50, 0x52, 0x34, (byte)Math.Clamp(slot, 1, 4)];
	}

	// The feature reports the pad answers (id -> report with the id): every one the report descriptor declares.
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

	public int AlternateSetting(int iface)
	{
		lock (gate)
		{
			return alternateSettings.GetValueOrDefault(iface);
		}
	}

	private static byte[] BuildReportDescriptor()
	{
		List<byte> d =
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
			0x65, 0x00,             //   Unit (none)
			0x05, 0x09, 0x19, 0x01, 0x29, 0x0E, // Buttons 1..14
			0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x0E, 0x81, 0x02,
			0x06, 0x00, 0xFF, 0x09, 0x20, // Vendor usage: report counter (6 bits)
			0x75, 0x06, 0x95, 0x01, 0x15, 0x00, 0x25, 0x7F, 0x81, 0x02,
			0x05, 0x01, 0x09, 0x33, 0x09, 0x34, // Usage Rx, Ry (L2, R2)
			0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x95, 0x02, 0x81, 0x02,
			0x06, 0x00, 0xFF, 0x09, 0x21, // Vendor: timestamp, IMU, battery, touchpad (54 bytes)
			0x95, 0x36, 0x81, 0x02,
			0x85, 0x05, 0x09, 0x22, 0x95, 0x1F, 0x91, 0x02 // Report ID 0x05: output, 31 bytes
		];
		// The feature reports: 8-bit fields (Report Size 8, Logical 0..255 still hold), Feature (Data, Var, Abs).
		foreach ((byte id, ushort page, byte usage, int count) in DualShock4Reports.Features)
		{
			d.AddRange([0x85, id]);
			if (page != 0)
			{
				d.AddRange([0x06, (byte)page, (byte)(page >> 8)]);
			}
			d.AddRange([0x09, usage, 0x95, (byte)count, 0xB1, 0x02]);
		}
		d.Add(0xC0);                // End Collection
		return [.. d];
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
		if (waiting is { } w)
		{
			requests.InputDelivered();
			w.Item2(SubmitResult.In(report));
		}
	}

	public void Submit(SubmitCommand command, Action<SubmitResult> complete)
	{
		switch (command.Endpoint)
		{
			case 0:
				SubmitResult result = Control(command.Setup, command.Data);
				complete(result);
				requests.Control(command.Setup, result);
				break;
			case InEndpoint when command.IsIn && !command.IsIsochronous:
				lock (gate)
				{
					inputWaiting.Enqueue((command.Sequence, complete));
				}
				requests.InPoll(InEndpoint);
				break;
			case OutEndpoint when !command.IsIn && !command.IsIsochronous:
				complete(SubmitResult.Ok(command.Length));
				requests.Output(command.Data, true);
				Output(command.Data);
				break;
			case AudioOutEndpoint when command.IsIsochronous && !command.IsIn:
				iso.ScheduleOut(command, complete);
				break;
			case MicInEndpoint when command.IsIsochronous && command.IsIn:
				iso.ScheduleIn(command, complete, MicBytesPerFrame, null);
				break;
			default:
				complete(SubmitResult.Stall());
				break;
		}
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
			if (inputWaiting.Count < before)
			{
				return true;
			}
		}
		return iso.Unlink(sequence);
	}

	public void Released()
	{
		lock (gate)
		{
			inputWaiting.Clear();
			alternateSettings.Clear();
			configuration = 0;
		}
		iso.Reset();
		requests.End("host released");
	}

	public void Dispose()
	{
		lock (gate)
		{
			inputWaiting.Clear();
			configuration = 0;
		}
		requests.End("disposed");
		iso.Dispose();
	}

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
					return Answer([(byte)AlternateSetting(index)], length);
				case 11:
					lock (gate)
					{
						alternateSettings[index] = (byte)value;
					}
					return SubmitResult.Ok();
				case 1:
				case 3:
					return SubmitResult.Ok();
				default:
					return SubmitResult.Stall();
			}
		}
		if (type == 1 && recipient == 1 && index == HidInterface)
		{
			return HidRequest(request, value, data, length);
		}
		if (type == 1)
		{
			return audioControls.Request(requestType, request, value, index, data, length);
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
				requests.Output(data, false);
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
