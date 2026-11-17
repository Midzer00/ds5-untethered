// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Kopru.Core.UsbIp;

namespace Kopru.Core.Virtual;

// A wired DualSense made of software, served over USB/IP: answers the host's control requests from the captured
// identity, hands games' output (triggers, lights, rumble) and audio (speaker + haptics, 4 channels) to the app, and
// gives games the input the app pushes. Isochronous audio is paced on a 1 ms frame clock, as a real bus would.
public sealed class VirtualDualSense : IVirtualPad
{
	public const string BusId = "1-1";

	// The bus id of the pad in a player slot (1-4): slot 1 keeps BusId, the others are "1-2".."1-4".
	public static string BusIdFor(int slot) => slot <= 1 ? BusId : "1-" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);

	// USB/IP endpoint numbers (without the direction bit).
	public const uint HidInEndpoint = 4;
	public const uint HidOutEndpoint = 3;
	public const uint AudioOutEndpoint = 1;
	public const uint MicInEndpoint = 2;

	public const int AudioChannels = 4;
	public const int AudioRate = 48000;
	public const int MicChannels = 2;
	public const int MicBytesPerFrame = 48 * MicChannels * 2;

	// Fills the mic endpoint's packets (null: silence, as before).
	public volatile Audio.IMicFeed? Mic;

	private readonly DeviceIdentity identity;

	private readonly object gate = new();

	private readonly Queue<(uint Sequence, Action<SubmitResult> Complete)> inputWaiting = new();

	private readonly IsoFrameClock iso;

	private readonly UsbAudioControls audioControls = new(_ => AudioRate);

	private readonly Dictionary<int, byte> alternateSettings = [];

	private readonly PadRequestLog requests;

	// Fills a microphone transfer from Mic (one delegate for every transfer).
	private readonly Action<byte[]> fillMic;

	private byte[] latestInput;

	public VirtualDualSense(DeviceIdentity identity, string busId = BusId)
	{
		this.identity = identity;
		latestInput = new byte[64];
		latestInput[0] = 0x01;
		latestInput[1] = latestInput[2] = latestInput[3] = latestInput[4] = 0x80;
		latestInput[8] = 0x08;
		byte[] d = identity.DeviceDescriptor;
		Info = new UsbIpDeviceInfo("/kopru/dualsense", busId, 1, 1, UsbIpCodes.SpeedFull, (ushort)(d[8] | (d[9] << 8)), (ushort)(d[10] | (d[11] << 8)),
			(ushort)(d[12] | (d[13] << 8)), d[4], d[5], d[6], identity.ConfigurationDescriptor[5], d[17], identity.Interfaces());
		iso = new IsoFrameClock("Virtual DualSense frames", () => PreciseWaitFactory);
		requests = new PadRequestLog("DualSense", busId, HidInterface, () => Log);
		fillMic = data => Mic?.Fill(data, MicChannels);
	}

	// Where the request diagnostics go (null: the app log; tests).
	public Action<string>? Log { get; init; }

	public UsbIpDeviceInfo Info { get; }

	public VirtualPadType Type => VirtualPadType.DualSense;

	// A game's output report (0x02: triggers, lights, rumble), by interrupt OUT or SET_REPORT.
	public event Action<byte[]>? OutputReceived;

	// 16-bit little-endian interleaved audio, AudioChannels channels at AudioRate: 1-2 speaker, 3-4 haptics.
	public event Action<ReadOnlyMemory<byte>>? AudioReceived;

	public event Action<byte, byte[]>? FeatureWritten;

	public event Action? HostReleased;

	public int AlternateSetting(int iface)
	{
		lock (gate)
		{
			return alternateSettings.GetValueOrDefault(iface);
		}
	}

	// The input report games get (USB report 0x01, 64 bytes); hands it to a waiting interrupt IN at once.
	public void PushInput(ReadOnlySpan<byte> usbInputReport)
	{
		byte[] report = usbInputReport.ToArray();
		(uint, Action<SubmitResult>)? waiting = null;
		lock (gate)
		{
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
			case HidInEndpoint when command.IsIn:
				lock (gate)
				{
					inputWaiting.Enqueue((command.Sequence, complete));
				}
				requests.InPoll(HidInEndpoint);
				break;
			case HidOutEndpoint when !command.IsIn:
				complete(SubmitResult.Ok(command.Length));
				requests.Output(command.Data, true);
				OutputReceived?.Invoke(command.Data);
				break;
			case AudioOutEndpoint when command.IsIsochronous:
				iso.ScheduleOut(command, complete);
				AudioReceived?.Invoke(command.Data);
				break;
			case MicInEndpoint when command.IsIsochronous:
				iso.ScheduleIn(command, complete, MicBytesPerFrame, fillMic);
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
			int waiting = inputWaiting.Count;
			Queue<(uint, Action<SubmitResult>)> kept = new(inputWaiting.Where(w => w.Sequence != sequence));
			inputWaiting.Clear();
			foreach ((uint, Action<SubmitResult>) w in kept)
			{
				inputWaiting.Enqueue(w);
			}
			if (inputWaiting.Count < waiting)
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
		}
		iso.Reset();
		requests.End("host released");
		HostReleased?.Invoke();
	}

	public void Dispose()
	{
		requests.End("disposed");
		iso.Dispose();
	}

	// Times the iso thread woke up (diagnostics, tests): once per due completion, none while nothing is scheduled.
	public long IsoWakeups => iso.Wakeups;

	// Makes the wait the iso thread completes transfers on time with (tests); null = Bluetooth.PreciseWait.Create. Read
	// when the first transfer is scheduled (the thread starts in the constructor, before initialisers run).
	public Func<Bluetooth.IPreciseWait>? PreciseWaitFactory { get; init; }

	// ---------------------------------------------------------------- control transfers
	// How many control requests the host sent (diagnostics).
	public int ControlRequests => controlRequests;

	private int controlRequests;

	internal SubmitResult Control(byte[] setup, byte[] data)
	{
		Interlocked.Increment(ref controlRequests);
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
					return Answer([identity.ConfigurationDescriptor[5]], length);
				case 9:
				case 1:
				case 3:
					return SubmitResult.Ok();
				case 11:
					lock (gate)
					{
						alternateSettings[index] = (byte)value;
					}
					return SubmitResult.Ok();
				case 10:
					return Answer([(byte)AlternateSetting(index)], length);
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

	private const int HidInterface = 3;

	private SubmitResult Descriptor(byte type, byte index, int length) => type switch
	{
		1 => Answer(identity.DeviceDescriptor, length),
		2 => Answer(identity.ConfigurationDescriptor, length),
		3 when index == 0 => Answer([4, 3, 0x09, 0x04], length),
		3 when identity.Strings.TryGetValue(index, out string? text) => Answer(StringDescriptor(text), length),
		0x21 => Answer(identity.HidDescriptor(), length),
		0x22 => Answer(identity.HidReportDescriptor, length),
		_ => SubmitResult.Stall()
	};

	private SubmitResult HidRequest(byte request, ushort value, byte[] data, int length)
	{
		byte reportType = (byte)(value >> 8);
		byte reportId = (byte)value;
		switch (request)
		{
			case 1 when reportType == 3:
				return identity.FeatureReports.TryGetValue(reportId, out byte[]? feature) ? Answer(feature, length) : SubmitResult.Stall();
			case 1 when reportType == 1:
				lock (gate)
				{
					return Answer(latestInput, length);
				}
			case 9 when reportType == 2:
				requests.Output(data, false);
				OutputReceived?.Invoke(data);
				return SubmitResult.Ok(data.Length);
			case 9 when reportType == 3:
				FeatureWritten?.Invoke(reportId, data);
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

	private static SubmitResult Answer(byte[] data, int length) => SubmitResult.In(data[..Math.Min(length, data.Length)]);

	private static byte[] StringDescriptor(string text)
	{
		byte[] body = Encoding.Unicode.GetBytes(text);
		return [(byte)(body.Length + 2), 3, .. body];
	}
}
