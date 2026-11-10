// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Kopru.Core.UsbIp;

namespace Kopru.Core.Virtual;

// A wired Xbox 360 controller made of software, served over USB/IP, for games that don't know the DualSense. Windows binds
// its in-box XUSB driver (xusb22) to VID 045E PID 028E, so no extra driver is needed. The descriptors are the public
// ones of the wired controller (as listed by lsusb dumps and the Linux xpad driver's notes); only interface 0 is used:
// interrupt IN 0x81 carries the 20-byte input report, interrupt OUT 0x01 rumble and LED reports. The other interfaces
// (headset, plug-in module, security) are described but never answer, like a real pad with nothing plugged in.
public sealed class VirtualXbox360 : IVirtualPad
{
	public const string BusId = VirtualDualSense.BusId;
	public const ushort VendorId = 0x045E;
	public const ushort ProductId = 0x028E;
	public const uint InEndpoint = 1;
	public const uint OutEndpoint = 1;

	public static readonly byte[] DeviceDescriptor =
	[
		0x12, 0x01, 0x00, 0x02, 0xFF, 0xFF, 0xFF, 0x08, 0x5E, 0x04, 0x8E, 0x02, 0x14, 0x01, 0x01, 0x02, 0x03, 0x01
	];

	public static readonly byte[] ConfigurationDescriptor =
	[
		0x09, 0x02, 0x99, 0x00, 0x04, 0x01, 0x00, 0xA0, 0xFA,
		// Interface 0: the controller (class FF, subclass 5D, protocol 01).
		0x09, 0x04, 0x00, 0x00, 0x02, 0xFF, 0x5D, 0x01, 0x00,
		0x11, 0x21, 0x00, 0x01, 0x01, 0x25, 0x81, 0x14, 0x00, 0x00, 0x00, 0x00, 0x13, 0x01, 0x08, 0x00, 0x00,
		0x07, 0x05, 0x81, 0x03, 0x20, 0x00, 0x04,
		0x07, 0x05, 0x01, 0x03, 0x20, 0x00, 0x08,
		// Interface 1: headset / voice (protocol 03).
		0x09, 0x04, 0x01, 0x00, 0x04, 0xFF, 0x5D, 0x03, 0x00,
		0x1B, 0x21, 0x00, 0x01, 0x01, 0x01, 0x82, 0x40, 0x01, 0x02, 0x20, 0x16, 0x83, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x16, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x07, 0x05, 0x82, 0x03, 0x20, 0x00, 0x02,
		0x07, 0x05, 0x02, 0x03, 0x20, 0x00, 0x04,
		0x07, 0x05, 0x83, 0x03, 0x20, 0x00, 0x40,
		0x07, 0x05, 0x03, 0x03, 0x20, 0x00, 0x10,
		// Interface 2: plug-in module (protocol 02).
		0x09, 0x04, 0x02, 0x00, 0x01, 0xFF, 0x5D, 0x02, 0x00,
		0x09, 0x21, 0x00, 0x01, 0x01, 0x22, 0x84, 0x07, 0x00,
		0x07, 0x05, 0x84, 0x03, 0x20, 0x00, 0x10,
		// Interface 3: security (class FF, subclass FD, protocol 13), no endpoints.
		0x09, 0x04, 0x03, 0x00, 0x00, 0xFF, 0xFD, 0x13, 0x04,
		0x06, 0x41, 0x00, 0x01, 0x01, 0x03
	];

	private static readonly Dictionary<byte, string> Strings = new()
	{
		[1] = "©Microsoft Corporation",
		[2] = "Controller",
		[3] = "KOPRU360",
		[4] = "Xbox Security Method 3, Version 1.00, © 2005 Microsoft Corporation. All rights reserved."
	};

	private readonly object gate = new();

	private readonly Queue<(uint Sequence, Action<SubmitResult> Complete)> inputWaiting = new();

	// IN transfers on the unused interfaces: held (a real pad NAKs them) until unlinked or released.
	private readonly HashSet<uint> idleWaiting = [];

	private byte[] latestInput = XboxReports.FromDualSense([]);

	private byte configuration;

	public VirtualXbox360(string busId = BusId)
	{
		Info = new UsbIpDeviceInfo("/kopru/xbox360", busId, 1, 1, UsbIpCodes.SpeedFull, VendorId, ProductId, 0x0114, 0xFF, 0xFF, 0xFF, 1, 1,
			[(0xFF, 0x5D, 0x01), (0xFF, 0x5D, 0x03), (0xFF, 0x5D, 0x02), (0xFF, 0xFD, 0x13)]);
	}

	public VirtualPadType Type => VirtualPadType.Xbox360;

	public UsbIpDeviceInfo Info { get; }

	// DualSense-format output (0x02) with the game's rumble in the classic motor bytes.
	public event Action<byte[]>? OutputReceived;

	public event Action<ReadOnlyMemory<byte>>? AudioReceived
	{
		add { }
		remove { }
	}

	// The last Xbox input report games got (tests, diagnostics).
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
		byte[] report = XboxReports.FromDualSense(usbInputReport);
		(uint, Action<SubmitResult>)? waiting = null;
		lock (gate)
		{
			// Like a real pad, a report only goes out when something changed.
			if (report.AsSpan().SequenceEqual(latestInput))
			{
				return;
			}
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
			if (XboxReports.RumbleToDualSense(command.Data) is { } rumble)
			{
				OutputReceived?.Invoke(rumble);
			}
			return;
		}
		if (command.IsIn)
		{
			lock (gate)
			{
				idleWaiting.Add(command.Sequence);
			}
			return;
		}
		complete(SubmitResult.Ok(command.Length));
	}

	public bool Unlink(uint sequence)
	{
		lock (gate)
		{
			if (idleWaiting.Remove(sequence))
			{
				return true;
			}
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
			idleWaiting.Clear();
			configuration = 0;
		}
	}

	public void Dispose() => Released();

	internal SubmitResult Control(byte[] setup, byte[] data)
	{
		byte requestType = setup[0];
		byte request = setup[1];
		ushort value = (ushort)(setup[2] | (setup[3] << 8));
		int length = setup[6] | (setup[7] << 8);
		int type = (requestType >> 5) & 3;
		bool isIn = (requestType & 0x80) != 0;
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
		// Vendor and class requests (the driver's capability and serial queries): zeros in, accepted out.
		return isIn ? Answer(new byte[length], length) : SubmitResult.Ok(data.Length);
	}

	private static SubmitResult Descriptor(byte type, byte index, int length) => type switch
	{
		1 => Answer(DeviceDescriptor, length),
		2 => Answer(ConfigurationDescriptor, length),
		3 when index == 0 => Answer([4, 3, 0x09, 0x04], length),
		3 when Strings.TryGetValue(index, out string? text) => Answer(StringDescriptor(text), length),
		_ => SubmitResult.Stall()
	};

	private static SubmitResult Answer(byte[] data, int length) => SubmitResult.In(data[..Math.Min(length, data.Length)]);

	private static byte[] StringDescriptor(string text)
	{
		byte[] body = Encoding.Unicode.GetBytes(text);
		return [(byte)(body.Length + 2), 3, .. body];
	}
}
