// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using Kopru.Core.UsbIp;

namespace Kopru.Core.Virtual;

// A wired DualSense made of software, served over USB/IP: answers the host's control requests from the captured
// identity, hands games' output (triggers, lights, rumble) and audio (speaker + haptics, 4 channels) to the app, and
// gives games the input the app pushes. Isochronous audio is paced on a 1 ms frame clock, as a real bus would.
public sealed class VirtualDualSense : IUsbIpDevice, IDisposable
{
	public const string BusId = "1-1";

	// USB/IP endpoint numbers (without the direction bit).
	public const uint HidInEndpoint = 4;
	public const uint HidOutEndpoint = 3;
	public const uint AudioOutEndpoint = 1;
	public const uint MicInEndpoint = 2;

	public const int AudioChannels = 4;
	public const int AudioRate = 48000;
	public const int MicBytesPerFrame = 48 * 2 * 2;

	private readonly DeviceIdentity identity;

	private readonly object gate = new();

	private readonly Queue<(uint Sequence, Action<SubmitResult> Complete)> inputWaiting = new();

	private readonly List<IsoCompletion> isoScheduled = [];

	private readonly Dictionary<(byte Request, ushort Value, ushort Index), byte[]> audioControls = [];

	private readonly Dictionary<int, byte> alternateSettings = [];

	private readonly Stopwatch clock = Stopwatch.StartNew();

	private readonly Thread isoThread;

	private volatile bool disposed;

	private byte[] latestInput;

	private long? frameBase;

	private long nextFrame;

	private sealed record IsoCompletion(uint Sequence, double DueMs, Action<SubmitResult> Complete, SubmitResult Result);

	public VirtualDualSense(DeviceIdentity identity)
	{
		this.identity = identity;
		latestInput = new byte[64];
		latestInput[0] = 0x01;
		latestInput[1] = latestInput[2] = latestInput[3] = latestInput[4] = 0x80;
		latestInput[8] = 0x08;
		byte[] d = identity.DeviceDescriptor;
		Info = new UsbIpDeviceInfo("/kopru/dualsense", BusId, 1, 1, UsbIpCodes.SpeedFull, (ushort)(d[8] | (d[9] << 8)), (ushort)(d[10] | (d[11] << 8)),
			(ushort)(d[12] | (d[13] << 8)), d[4], d[5], d[6], identity.ConfigurationDescriptor[5], d[17], identity.Interfaces());
		isoThread = new Thread(IsoLoop) { Name = "Virtual DualSense frames", IsBackground = true, Priority = ThreadPriority.AboveNormal };
		isoThread.Start();
	}

	public UsbIpDeviceInfo Info { get; }

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
		waiting?.Item2(SubmitResult.In(report));
	}

	public void Submit(SubmitCommand command, Action<SubmitResult> complete)
	{
		switch (command.Endpoint)
		{
			case 0:
				complete(Control(command.Setup, command.Data));
				break;
			case HidInEndpoint when command.IsIn:
				lock (gate)
				{
					inputWaiting.Enqueue((command.Sequence, complete));
				}
				break;
			case HidOutEndpoint when !command.IsIn:
				complete(SubmitResult.Ok(command.Length));
				OutputReceived?.Invoke(command.Data);
				break;
			case AudioOutEndpoint when command.IsIsochronous:
				ScheduleAudioOut(command, complete);
				break;
			case MicInEndpoint when command.IsIsochronous:
				ScheduleMicIn(command, complete);
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
			return inputWaiting.Count < waiting || isoScheduled.RemoveAll(c => c.Sequence == sequence) > 0;
		}
	}

	public void Released()
	{
		lock (gate)
		{
			inputWaiting.Clear();
			isoScheduled.Clear();
			frameBase = null;
			alternateSettings.Clear();
		}
		HostReleased?.Invoke();
	}

	public void Dispose()
	{
		disposed = true;
		isoThread.Join(500);
	}

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
			return AudioRequest(request, value, index, data, length);
		}
		return SubmitResult.Stall();
	}

	private int HidInterface => 3;

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

	// UAC1 requests to the feature units (volume, mute) and the streaming endpoints (sampling rate): SET_CUR is kept,
	// GET_CUR answers it; MIN/MAX/RES describe a 0 to -96 dB volume in 1 dB steps.
	private SubmitResult AudioRequest(byte request, ushort value, ushort index, byte[] data, int length)
	{
		lock (gate)
		{
			if (request == 0x01)
			{
				audioControls[(request, value, index)] = data;
				return SubmitResult.Ok(data.Length);
			}
			byte[] answer = request switch
			{
				0x81 => audioControls.GetValueOrDefault(((byte)0x01, value, index)) ?? Default(length),
				0x82 => length == 2 ? [0x00, 0xA0] : Default(length),
				0x83 => length == 2 ? [0x00, 0x00] : Default(length),
				0x84 => length == 2 ? [0x00, 0x01] : Default(length),
				_ => []
			};
			return answer.Length == 0 ? SubmitResult.Stall() : Answer(answer, length);
		}

		static byte[] Default(int length) => length == 3 ? [0x80, 0xBB, 0x00] : new byte[length];
	}

	private static SubmitResult Answer(byte[] data, int length) => SubmitResult.In(data[..Math.Min(length, data.Length)]);

	private static byte[] StringDescriptor(string text)
	{
		byte[] body = Encoding.Unicode.GetBytes(text);
		return [(byte)(body.Length + 2), 3, .. body];
	}

	// ---------------------------------------------------------------- isochronous audio
	private void ScheduleAudioOut(SubmitCommand command, Action<SubmitResult> complete)
	{
		IsoPacket[] done = command.Packets.Select(p => p with { ActualLength = p.Length, Status = 0 }).ToArray();
		int startFrame = NextFrames(command, out double dueMs);
		Schedule(command.Sequence, dueMs, complete, new SubmitResult(0, command.Length, [], startFrame, done));
		AudioReceived?.Invoke(command.Data);
	}

	private void ScheduleMicIn(SubmitCommand command, Action<SubmitResult> complete)
	{
		IsoPacket[] done = new IsoPacket[command.Packets.Length];
		int offset = 0;
		for (int i = 0; i < done.Length; i++)
		{
			int actual = Math.Min(MicBytesPerFrame, command.Packets[i].Length);
			done[i] = command.Packets[i] with { ActualLength = actual, Status = 0 };
			offset += actual;
		}
		int startFrame = NextFrames(command, out double dueMs);
		Schedule(command.Sequence, dueMs, complete, new SubmitResult(0, offset, new byte[offset], startFrame, done));
	}

	// Consecutive transfers get consecutive frames; a gap (the host was late) starts again from now.
	private int NextFrames(SubmitCommand command, out double dueMs)
	{
		lock (gate)
		{
			double now = clock.Elapsed.TotalMilliseconds;
			frameBase ??= (long)now - command.StartFrame;
			long current = (long)now - frameBase.Value;
			long start = Math.Max(nextFrame, current);
			nextFrame = start + command.Packets.Length;
			dueMs = frameBase.Value + nextFrame;
			return (int)start;
		}
	}

	private void Schedule(uint sequence, double dueMs, Action<SubmitResult> complete, SubmitResult result)
	{
		lock (gate)
		{
			isoScheduled.Add(new IsoCompletion(sequence, dueMs, complete, result));
		}
	}

	private void IsoLoop()
	{
		while (!disposed)
		{
			List<IsoCompletion> due = [];
			lock (gate)
			{
				double now = clock.Elapsed.TotalMilliseconds;
				for (int i = isoScheduled.Count - 1; i >= 0; i--)
				{
					if (isoScheduled[i].DueMs <= now)
					{
						due.Add(isoScheduled[i]);
						isoScheduled.RemoveAt(i);
					}
				}
			}
			foreach (IsoCompletion c in due.OrderBy(c => c.DueMs))
			{
				c.Complete(c.Result);
			}
			Thread.Sleep(1);
		}
	}
}
