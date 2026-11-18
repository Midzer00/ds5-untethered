// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Kopru.Core.UsbIp;

namespace Kopru.Core.Virtual;

// What the host (Windows, a game) asks of a virtual pad, for the app log: each distinct control request once per session
// (request type, report id, length, answered or stalled), the first IN poll and the first output report, and a summary of
// the session (IN polling rate, input reports delivered, output reports, control requests and stalls) 10 s after the
// first IN poll and when the host lets go. Only setup packets and counts are logged, never report contents (pairing
// reports carry Bluetooth addresses).
//
// The pads call this on their submit path (after completing the transfer): it only counts (Interlocked) and queues; the
// lines are formatted and written (app log file, under its lock) on one background writer shared by every pad.
//
//   virtual DS4 1-1: GET_REPORT feature 0x12 [A1 01 0312 0003 len 16] -> answered 16
//   virtual DS4 1-1: GET_REPORT feature 0x05 [A1 01 0305 0003 len 41] -> stalled
//   virtual DS4 1-1: first IN poll on endpoint 4
//   virtual DS4 1-1: first output report 0x05, 32 bytes (interrupt OUT)
//   virtual DS4 1-1: summary after 10 s: 10.0 s, IN polls 2500 (250/s), input reports 2499, output reports 120
//     (interrupt 120, SET_REPORT 0), control requests 85 (stalled 0, distinct 31)
public sealed class PadRequestLog
{
	// Distinct control requests logged per session; later new ones are counted only.
	public const int MaxDistinct = 64;

	public const long EarlySummaryMs = 10_000;

	private const long Unset = long.MinValue;

	// The background writer: lines in order, one thread for every pad, started with the first line.
	private static readonly Lazy<BlockingCollection<Action>> Writer = new(StartWriter);

	private readonly object gate = new();

	private readonly string prefix;

	private readonly Func<Action<string>?> sink;

	private readonly Func<long> nowMs;

	private readonly int hidInterface;

	// Guarded by gate (control requests only; polls never take the lock).
	private readonly HashSet<(byte RequestType, byte Request, ushort Value, ushort Index)> seen = [];

	private bool overflowLogged;

	private long startMs = Unset;

	private long inPolls, inputs, outInterrupt, outSetReport, controls, stalls;

	private int firstPollLogged, firstOutputLogged, earlyLogged;

	private readonly record struct Counts(long StartMs, long NowMs, long InPolls, long Inputs, long OutInterrupt, long OutSetReport, long Controls,
		long Stalls, int Distinct);

	// pad: "DS4", "DualSense"; hidInterface: the HID interface number (class requests to it are HID, others audio); sink:
	// where lines go (null: the app log; read on the writer); nowMs: a millisecond clock (tests).
	public PadRequestLog(string pad, string busId, int hidInterface, Func<Action<string>?>? sink = null, Func<long>? nowMs = null)
	{
		prefix = $"virtual {pad} {busId}";
		this.hidInterface = hidInterface;
		this.sink = sink ?? (() => null);
		long origin = Stopwatch.GetTimestamp();
		this.nowMs = nowMs ?? (() => (Stopwatch.GetTimestamp() - origin) * 1000 / Stopwatch.Frequency);
	}

	// Waits until every line queued so far is written (tests, shutdown). False on timeout.
	public static bool Flush(TimeSpan timeout)
	{
		using ManualResetEventSlim done = new();
		Writer.Value.Add(done.Set);
		return done.Wait(timeout);
	}

	// A control transfer on endpoint 0 and what the pad answered.
	public void Control(byte[] setup, SubmitResult result)
	{
		if (setup.Length < 8)
		{
			return;
		}
		Begin(nowMs());
		Interlocked.Increment(ref controls);
		if (result.Status != 0)
		{
			Interlocked.Increment(ref stalls);
		}
		ushort value = (ushort)(setup[2] | (setup[3] << 8));
		ushort index = (ushort)(setup[4] | (setup[5] << 8));
		bool first = false, overflow = false;
		lock (gate)
		{
			if (seen.Count < MaxDistinct)
			{
				first = seen.Add((setup[0], setup[1], value, index));
			}
			else if (!overflowLogged && !seen.Contains((setup[0], setup[1], value, index)))
			{
				overflow = overflowLogged = true;
			}
		}
		if (first)
		{
			int iface = hidInterface;
			Queue(() => Describe(setup, result, iface));
		}
		else if (overflow)
		{
			Queue(() => $"more than {MaxDistinct} distinct control requests, further ones are only counted");
		}
	}

	// An interrupt IN transfer the host queued (a poll for the next input report). No lock.
	public void InPoll(uint endpoint)
	{
		long now = nowMs();
		long start = Begin(now);
		Interlocked.Increment(ref inPolls);
		if (Volatile.Read(ref firstPollLogged) == 0 && Interlocked.Exchange(ref firstPollLogged, 1) == 0)
		{
			Queue(() => string.Create(CultureInfo.InvariantCulture, $"first IN poll on endpoint {endpoint}"));
		}
		else if (Volatile.Read(ref earlyLogged) == 0 && now - start >= EarlySummaryMs && Interlocked.Exchange(ref earlyLogged, 1) == 0)
		{
			Counts counts = Snapshot(now);
			Queue(() => Summary("summary after 10 s", counts));
		}
	}

	// An input report handed to a waiting IN transfer. No lock.
	public void InputDelivered() => Interlocked.Increment(ref inputs);

	// An output report from the host, by interrupt OUT or SET_REPORT.
	public void Output(byte[] data, bool interrupt)
	{
		Begin(nowMs());
		Interlocked.Increment(ref interrupt ? ref outInterrupt : ref outSetReport);
		if (Volatile.Read(ref firstOutputLogged) == 0 && Interlocked.Exchange(ref firstOutputLogged, 1) == 0)
		{
			byte id = data.Length > 0 ? data[0] : (byte)0;
			int length = data.Length;
			Queue(() => string.Create(CultureInfo.InvariantCulture,
				$"first output report 0x{id:X2}, {length} bytes ({(interrupt ? "interrupt OUT" : "SET_REPORT")})"));
		}
	}

	// The host let go (or the pad is disposed): the session summary, then counting starts over. Nothing when the session
	// saw no traffic.
	public void End(string reason)
	{
		Counts counts;
		lock (gate)
		{
			if (Interlocked.Read(ref startMs) == Unset)
			{
				return;
			}
			counts = Snapshot(nowMs());
			Interlocked.Exchange(ref startMs, Unset);
			Interlocked.Exchange(ref inPolls, 0);
			Interlocked.Exchange(ref inputs, 0);
			Interlocked.Exchange(ref outInterrupt, 0);
			Interlocked.Exchange(ref outSetReport, 0);
			Interlocked.Exchange(ref controls, 0);
			Interlocked.Exchange(ref stalls, 0);
			Volatile.Write(ref firstPollLogged, 0);
			Volatile.Write(ref firstOutputLogged, 0);
			Volatile.Write(ref earlyLogged, 0);
			seen.Clear();
			overflowLogged = false;
		}
		Queue(() => Summary($"session end ({reason})", counts));
	}

	// A pad-specific line (the experimental Xbox One pad's GIP steps), written in order with the others. The line is made
	// on the writer thread.
	public void Note(Func<string> line) => Queue(line);

	// The session start (set by the first event).
	private long Begin(long now)
	{
		long start = Interlocked.Read(ref startMs);
		if (start != Unset)
		{
			return start;
		}
		long before = Interlocked.CompareExchange(ref startMs, now, Unset);
		return before == Unset ? now : before;
	}

	private Counts Snapshot(long now)
	{
		int distinct;
		lock (gate)
		{
			distinct = seen.Count;
		}
		return new Counts(Interlocked.Read(ref startMs), now, Interlocked.Read(ref inPolls), Interlocked.Read(ref inputs),
			Interlocked.Read(ref outInterrupt), Interlocked.Read(ref outSetReport), Interlocked.Read(ref controls), Interlocked.Read(ref stalls), distinct);
	}

	private static string Summary(string label, Counts c)
	{
		double seconds = c.StartMs == Unset ? 0 : Math.Max(0, c.NowMs - c.StartMs) / 1000.0;
		double rate = seconds > 0 ? c.InPolls / seconds : 0;
		return string.Create(CultureInfo.InvariantCulture,
			$"{label}: {seconds:0.0} s, IN polls {c.InPolls} ({rate:0}/s), input reports {c.Inputs}, output reports {c.OutInterrupt + c.OutSetReport} " +
			$"(interrupt {c.OutInterrupt}, SET_REPORT {c.OutSetReport}), control requests {c.Controls} (stalled {c.Stalls}, distinct {c.Distinct})");
	}

	// The line is made and written on the writer thread.
	private void Queue(Func<string> line)
	{
		try
		{
			Writer.Value.Add(() =>
			{
				string message = prefix + ": " + line();
				if (sink() is { } custom)
				{
					custom(message);
				}
				else
				{
					Diagnostics.AppLog.Write(message);
				}
			});
		}
		catch (Exception)
		{
			// Diagnostics never break the device.
		}
	}

	private static BlockingCollection<Action> StartWriter()
	{
		BlockingCollection<Action> queue = [];
		Thread thread = new(() =>
		{
			foreach (Action write in queue.GetConsumingEnumerable())
			{
				try
				{
					write();
				}
				catch (Exception)
				{
					// Diagnostics never break anything.
				}
			}
		})
		{ Name = "Virtual pad request log", IsBackground = true, Priority = ThreadPriority.BelowNormal };
		thread.Start();
		return queue;
	}

	// "GET_REPORT feature 0x02 [A1 01 0302 0003 len 37] -> answered 37"
	public static string Describe(byte[] setup, SubmitResult result, int hidInterface)
	{
		byte requestType = setup[0];
		byte request = setup[1];
		ushort value = (ushort)(setup[2] | (setup[3] << 8));
		ushort index = (ushort)(setup[4] | (setup[5] << 8));
		int length = setup[6] | (setup[7] << 8);
		string outcome = result.Status != 0 ? "stalled" : (requestType & 0x80) != 0 ? $"answered {result.Data.Length}" : "accepted";
		return string.Create(CultureInfo.InvariantCulture,
			$"{Name(requestType, request, value, index, hidInterface)} [{requestType:X2} {request:X2} {value:X4} {index:X4} len {length}] -> {outcome}");
	}

	private static string Name(byte requestType, byte request, ushort value, ushort index, int hidInterface)
	{
		int type = (requestType >> 5) & 3;
		int recipient = requestType & 0x1F;
		byte high = (byte)(value >> 8), low = (byte)value;
		if (type == 0)
		{
			return request switch
			{
				0 => "GET_STATUS",
				1 => "CLEAR_FEATURE",
				3 => "SET_FEATURE",
				5 => "SET_ADDRESS",
				6 => "GET_DESCRIPTOR " + DescriptorName(high, low),
				8 => "GET_CONFIGURATION",
				9 => $"SET_CONFIGURATION {low}",
				10 => $"GET_INTERFACE {index}",
				11 => $"SET_INTERFACE {index} alt {low}",
				_ => $"standard request 0x{request:X2}"
			};
		}
		if (type == 1 && recipient == 1 && index == hidInterface)
		{
			string report = high switch { 1 => "input", 2 => "output", 3 => "feature", _ => "type " + high };
			return request switch
			{
				1 => $"GET_REPORT {report} 0x{low:X2}",
				9 => $"SET_REPORT {report} 0x{low:X2}",
				2 => "GET_IDLE",
				10 => "SET_IDLE",
				3 => "GET_PROTOCOL",
				11 => "SET_PROTOCOL",
				_ => $"HID request 0x{request:X2}"
			};
		}
		if (type == 1)
		{
			string target = recipient == 2 ? $"endpoint 0x{index & 0xFF:X2}" : $"interface {index & 0xFF} unit {index >> 8}";
			string name = request switch
			{
				0x01 => "SET_CUR",
				0x81 => "GET_CUR",
				0x82 => "GET_MIN",
				0x83 => "GET_MAX",
				0x84 => "GET_RES",
				_ => $"class request 0x{request:X2}"
			};
			return $"audio {name} {target}";
		}
		return $"vendor request 0x{request:X2}";
	}

	private static string DescriptorName(byte type, byte index) => type switch
	{
		1 => "device",
		2 => "configuration",
		3 => $"string {index}",
		6 => "device qualifier",
		0x21 => "HID",
		0x22 => "HID report",
		_ => $"type 0x{type:X2}"
	};
}
