// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Diagnostics;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;

// `Kopru.Probe micload [seconds-per-phase] [nospeaker]`: what the Bluetooth mic costs. Phases back to back on one
// session: (0) no stream at all, then with a gentle haptics pattern keeping the stream running: (1) haptics only,
// (2) + mic on, (3) + quiet speaker tone + mic on (left out with `nospeaker`). One comparable line per phase, then the mic
// is switched off (the 0xFE burst) and phase 2 is compared with phase 1. No device ids are printed.
//
// Triggers: in every phase the L2/R2 effect alternates between two mild effects every 250 ms (ending with triggers off).
// For each request: the delay until the report 0x31 carrying it has been written (UpdateOutput writes synchronously, so
// a request is only coalesced when a later one is written first, dropped when the write throws). Round trip: a 1 s
// baseline without trigger changes marks the input-body bytes that change by themselves; of the rest (buttons/reserved
// 7..14 and 40..62), a byte that changes within 250 ms after at least half the requests counts as reacting, and the
// request -> first change of a reacting byte is the round trip. When no byte reacts the probe says so.
internal static class MicLoad
{
	private sealed record Trig(int Requests, int Written, int Coalesced, int Dropped, Spread? WriteMs, long FromTicks, long ToTicks);

	private sealed record Phase(string Name, LinkResult? Link, double StreamHz, Spread? SendMs, double MicHz, int MicGaps, double StrayHz, double SpeakerHz,
		Trig Trigger);

	private sealed record Request(long Ticks, byte[] Effect);

	private sealed record Body(long Ticks, byte[] Bytes);

	private const int TriggerPeriodMs = 250;

	private static readonly int[] Candidates = [.. Enumerable.Range(7, 8), .. Enumerable.Range(40, 23)];

	public static int Run(IReadOnlyList<HidDevice> devices, string[] args)
	{
		HidDevice? bt = devices.FirstOrDefault(d => d.Transport == Transport.Bluetooth);
		if (bt == null)
		{
			Console.WriteLine("No Bluetooth DualSense visible (connected? hidden by HidHide?)");
			return 2;
		}
		int seconds = args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > 1 ? s : 8;
		bool speakerPhase = !args.Any(a => string.Equals(a, "nospeaker", StringComparison.OrdinalIgnoreCase));

		using TimedLink link = new(WindowsHid.Open(bt));
		using BluetoothSession session = new(link);
		List<LinkSample> samples = [];
		session.InputReceived += input =>
		{
			lock (samples)
			{
				samples.Add(new LinkSample(Stopwatch.GetTimestamp(), input.Sequence, input.SensorTicks));
			}
		};
		List<Body> bodies = [];
		session.InputBodyReceived += body =>
		{
			long now = session.LastReadTicks;
			lock (bodies)
			{
				bodies.Add(new Body(now, body.ToArray()));
			}
		};
		int micReports = 0, micGaps = 0, lastCounter = -1;
		session.MicReportReceived += report =>
		{
			int counter = report[MicPackets.CounterOffset];
			if (lastCounter >= 0 && (byte)(lastCounter + 1) != counter)
			{
				micGaps++;
			}
			lastCounter = counter;
			micReports++;
		};
		session.Start();
		Thread.Sleep(500);
		// Baseline for the trigger round trip: 1 s with no trigger change.
		long baselineFrom = Stopwatch.GetTimestamp();
		Thread.Sleep(1000);
		long baselineTo = Stopwatch.GetTimestamp();

		byte[][] effects = [TriggerEffects.Feedback(3, 2), TriggerEffects.Weapon(2, 5, 2)];
		List<Request> requests = [];
		int nextEffect = 0;
		long lastRequest = 0;
		void DriveTriggers()
		{
			long now = Stopwatch.GetTimestamp();
			if (lastRequest != 0 && Stopwatch.GetElapsedTime(lastRequest, now).TotalMilliseconds < TriggerPeriodMs)
			{
				return;
			}
			lastRequest = now;
			byte[] effect = effects[nextEffect++ % effects.Length];
			RequestTrigger(session, link, requests, effect);
		}

		using CancellationTokenSource stop = new();
		Thread haptics = new(() => FeedHaptics(session, stop.Token)) { IsBackground = true, Name = "micload haptics" };

		List<Phase> phases = [];
		Phase Measure(string name, Action? during = null)
		{
			lock (samples)
			{
				samples.Clear();
			}
			link.TakeSendTimes();
			link.TakeTriggerResults();
			Interlocked.Exchange(ref micReports, 0);
			Interlocked.Exchange(ref micGaps, 0);
			lastCounter = -1;
			long stream = session.HapticsReports, stray = session.StrayMicReports, speaker = session.SpeakerReports;
			long from = Stopwatch.GetTimestamp();
			Stopwatch clock = Stopwatch.StartNew();
			Console.WriteLine($"{name}: {seconds} s...");
			lastRequest = 0;
			while (clock.Elapsed.TotalSeconds < seconds)
			{
				during?.Invoke();
				DriveTriggers();
				Thread.Sleep(5);
			}
			double t = clock.Elapsed.TotalSeconds;
			long to = Stopwatch.GetTimestamp();
			List<LinkSample> taken;
			lock (samples)
			{
				taken = [.. samples];
			}
			double[] sends = link.TakeSendTimes();
			(int written, int coalesced, int dropped, double[] delays) = link.TakeTriggerResults();
			int asked = requests.Count(r => r.Ticks >= from && r.Ticks < to);
			Trig trig = new(asked, written, coalesced, dropped, delays.Length > 0 ? SpreadOf(delays) : null, from, to);
			Phase p = new(name, LinkAnalyzer.Analyze(taken), (session.HapticsReports - stream) / t, sends.Length > 0 ? SpreadOf(sends) : null,
				Volatile.Read(ref micReports) / t, Volatile.Read(ref micGaps), (session.StrayMicReports - stray) / t, (session.SpeakerReports - speaker) / t,
				trig);
			phases.Add(p);
			Print(p);
			return p;
		}

		Measure("0 no stream");
		haptics.Start();
		Thread.Sleep(300);
		Phase one = Measure("1 haptics only");
		session.MicStreamEnabled = true;
		Thread.Sleep(500);
		Phase two = Measure("2 haptics + mic");
		if (speakerPhase)
		{
			session.Speaker = new SpeakerAudioSender(OpusEncoders.Create()) { Volume = 0.2, HeadphoneVolume = 0.2 };
			byte[] chunk = new byte[480 * 8];
			long sample = 0;
			Stopwatch tone = Stopwatch.StartNew();
			void Feed()
			{
				long due = (long)(tone.Elapsed.TotalSeconds * 48000) + 480 * 2;
				while (sample < due)
				{
					for (int i = 0; i < 480; i++, sample++)
					{
						short v = (short)(3000 * Math.Sin(2 * Math.PI * 440 * sample / 48000.0));
						BitConverter.TryWriteBytes(chunk.AsSpan(i * 8), v);
						BitConverter.TryWriteBytes(chunk.AsSpan(i * 8 + 2), v);
					}
					session.PushSpeakerAudio(chunk);
				}
			}
			Measure("3 haptics + speaker + mic", Feed);
			session.Speaker = null;
		}

		session.UpdateOutput(b => OutputBlock.SetTriggers(b, TriggerEffects.Off(), TriggerEffects.Off()));
		stop.Cancel();
		haptics.Join(1000);
		session.MicStreamEnabled = false;
		Thread.Sleep(600);
		Console.WriteLine($"Mic off: sent {session.MicOffSent} stream reports with the off value; triggers off");
		List<Body> recorded;
		lock (bodies)
		{
			recorded = [.. bodies];
		}
		string roundTrip = RoundTrip(recorded, requests, phases, baselineFrom, baselineTo);
		Console.WriteLine(Verdict(one, two) + "; " + TriggerVerdict(phases) + "; " + roundTrip);
		return 0;
	}

	private static void RequestTrigger(BluetoothSession session, TimedLink link, List<Request> requests, byte[] effect)
	{
		long ticks = Stopwatch.GetTimestamp();
		requests.Add(new Request(ticks, effect));
		link.ExpectTrigger(effect, ticks);
		try
		{
			session.UpdateOutput(b => OutputBlock.SetTriggers(b, effect, effect));
		}
		catch (Exception)
		{
			link.FailTrigger(ticks);
		}
	}

	// A faint 80 Hz tone (like the adapter check), so the stream runs at its full rate without a strong buzz.
	private static void FeedHaptics(BluetoothSession session, CancellationToken stop)
	{
		double phase = 0;
		while (!stop.IsCancellationRequested)
		{
			while (session.Haptics.Buffered < Kopru.Core.Haptics.HapticsMixer.MaxFrames / 2)
			{
				sbyte v = (sbyte)Math.Round(12 * Math.Sin(phase));
				session.Haptics.Push(v, v);
				phase += 2 * Math.PI * 80 / BluetoothReports.HapticsSampleRate;
			}
			session.WakeHaptics();
			Thread.Sleep(10);
		}
	}

	private static void Print(Phase p)
	{
		LinkResult? l = p.Link;
		string input = l == null
			? "input: not enough reports"
			: F($"input {l.RateHz:F0}/s, interval p50 {l.IntervalMs.P50:F2} p99 {l.IntervalMs.P99:F2} max {l.IntervalMs.Max:F1} ms, extra p99 {l.ExtraDelayMs.P99:F1} ms, lost {l.Lost} ({l.LostShare * 100:F2} %)");
		string send = p.SendMs == null ? "send n/a" : F($"send p50 {p.SendMs.P50:F2} p99 {p.SendMs.P99:F2} max {p.SendMs.Max:F1} ms");
		Console.WriteLine(F($"  {p.Name}: {input}; stream {p.StreamHz:F1}/s of {AdapterCheck.HapticsTargetHz:F2}, {send}; mic {p.MicHz:F1}/s, gaps {p.MicGaps}, stray {p.StrayHz:F1}/s; speaker {p.SpeakerHz:F1}/s"));
		Console.WriteLine("    " + TriggerLine(p.Trigger));
	}

	private static string TriggerLine(Trig t) =>
		F($"triggers: {t.Requests} requests, written {t.Written}, coalesced {t.Coalesced}, dropped {t.Dropped}, ") +
		(t.WriteMs == null ? "write n/a" : F($"request -> written p50 {t.WriteMs.P50:F2} p99 {t.WriteMs.P99:F2} max {t.WriteMs.Max:F1} ms"));

	private static string Verdict(Phase one, Phase two)
	{
		if (one.Link == null || two.Link == null)
		{
			return "Verdict: not enough input reports to compare";
		}
		double inputChange = (two.Link.RateHz / one.Link.RateHz - 1) * 100;
		double streamChange = (two.StreamHz / Math.Max(1e-9, one.StreamHz) - 1) * 100;
		string stream = Math.Abs(streamChange) < 2 ? "haptics send rate unchanged" : F($"haptics send rate {streamChange:+0;-0} %");
		return F($"Verdict (mic on vs off): input rate {inputChange:+0;-0} % ({one.Link.RateHz:F0} -> {two.Link.RateHz:F0}/s), {stream} ({one.StreamHz:F1} -> {two.StreamHz:F1}/s), ") +
			F($"p99 input interval {one.Link.IntervalMs.P99:F1} -> {two.Link.IntervalMs.P99:F1} ms, lost {one.Link.Lost} -> {two.Link.Lost}, ") +
			(one.SendMs != null && two.SendMs != null ? F($"send p99 {one.SendMs.P99:F2} -> {two.SendMs.P99:F2} ms") : "send n/a");
	}

	private static string TriggerVerdict(List<Phase> phases) =>
		"triggers request -> written p99 " + string.Join(", ", phases.Select(p =>
			F($"phase {p.Name[0]} {(p.Trigger.WriteMs == null ? "n/a" : p.Trigger.WriteMs.P99.ToString("F2", CultureInfo.InvariantCulture))} ms"))) +
		F($", coalesced {phases.Sum(p => p.Trigger.Coalesced)}, dropped {phases.Sum(p => p.Trigger.Dropped)}");

	// Request -> input reaction, per phase (see the class comment). Prints its own lines; returns the verdict part.
	private static string RoundTrip(List<Body> bodies, List<Request> requests, List<Phase> phases, long baselineFrom, long baselineTo)
	{
		List<Body> baseline = bodies.Where(b => b.Ticks >= baselineFrom && b.Ticks < baselineTo).ToList();
		if (baseline.Count < 10 || requests.Count == 0)
		{
			Console.WriteLine("Trigger round trip: not enough input reports in the baseline");
			return "trigger round trip: not measurable (no baseline)";
		}
		HashSet<int> noisy = Candidates.Where(i => baseline.Select(b => b.Bytes[i]).Distinct().Count() > 1).ToHashSet();
		int[] quiet = Candidates.Where(i => !noisy.Contains(i)).ToArray();
		long window = Stopwatch.Frequency * TriggerPeriodMs / 1000;
		// Per request: the first change (ticks after the request) of every quiet byte within the window.
		Dictionary<int, int> reactions = quiet.ToDictionary(i => i, _ => 0);
		List<(Request Request, Dictionary<int, long> Changes)> seen = [];
		int cursor = 0;
		for (int r = 0; r < requests.Count; r++)
		{
			Request request = requests[r];
			long end = Math.Min(request.Ticks + window, r + 1 < requests.Count ? requests[r + 1].Ticks : long.MaxValue);
			while (cursor < bodies.Count && bodies[cursor].Ticks < request.Ticks)
			{
				cursor++;
			}
			if (cursor == 0)
			{
				continue;
			}
			byte[] before = bodies[cursor - 1].Bytes;
			Dictionary<int, long> changes = [];
			for (int k = cursor; k < bodies.Count && bodies[k].Ticks < end; k++)
			{
				foreach (int i in quiet)
				{
					if (!changes.ContainsKey(i) && bodies[k].Bytes[i] != before[i])
					{
						changes[i] = bodies[k].Ticks - request.Ticks;
					}
				}
			}
			foreach (int i in changes.Keys)
			{
				reactions[i]++;
			}
			seen.Add((request, changes));
		}
		int[] reacting = reactions.Where(p => p.Value * 2 >= seen.Count).Select(p => p.Key).OrderBy(i => i).ToArray();
		Console.WriteLine($"Trigger round trip: baseline {baseline.Count} reports, bytes changing by themselves {(noisy.Count == 0 ? "none" : string.Join(" ", noisy.OrderBy(i => i)))} (of body 7..14, 40..62)");
		if (reacting.Length == 0)
		{
			string most = string.Join(", ", reactions.Where(p => p.Value > 0).OrderByDescending(p => p.Value).Take(3).Select(p => $"byte {p.Key} {p.Value}x"));
			Console.WriteLine($"  No input byte reacts to trigger effect changes with the trigger untouched ({seen.Count} requests{(most.Length > 0 ? "; most changes: " + most : "")}): round trip not measurable this way");
			return "trigger round trip: no input byte reacts without a press (not measurable)";
		}
		foreach (int i in reacting)
		{
			string values = string.Join(" ", bodies.Select(b => b.Bytes[i]).Distinct().OrderBy(v => v).Take(12).Select(v => v.ToString("X2", CultureInfo.InvariantCulture)));
			Console.WriteLine($"  body byte {i} reacts to {reactions[i]} of {seen.Count} requests; values seen {values}");
		}
		List<string> parts = [];
		foreach (Phase p in phases)
		{
			double[] rtt = seen.Where(x => x.Request.Ticks >= p.Trigger.FromTicks && x.Request.Ticks < p.Trigger.ToTicks)
				.Select(x => reacting.Where(x.Changes.ContainsKey).Select(i => x.Changes[i]).DefaultIfEmpty(-1).Min())
				.Where(t => t >= 0).Select(t => t * 1000.0 / Stopwatch.Frequency).ToArray();
			int asked = seen.Count(x => x.Request.Ticks >= p.Trigger.FromTicks && x.Request.Ticks < p.Trigger.ToTicks);
			string line = rtt.Length == 0 ? "no reaction" : F($"p50 {SpreadOf(rtt).P50:F1} p99 {SpreadOf(rtt).P99:F1} max {rtt.Max():F1} ms ({rtt.Length} of {asked})");
			Console.WriteLine($"  {p.Name}: request -> input reaction {line}");
			parts.Add(rtt.Length == 0 ? $"phase {p.Name[0]} n/a" : F($"phase {p.Name[0]} p99 {SpreadOf(rtt).P99:F1} ms"));
		}
		return $"trigger round trip (body byte {string.Join("/", reacting)}) " + string.Join(", ", parts);
	}

	private static Spread SpreadOf(double[] values)
	{
		values = [.. values];
		Array.Sort(values);
		double At(double q) => values[Math.Min(values.Length - 1, (int)Math.Floor(q * (values.Length - 1)))];
		return new Spread(At(0.5), At(0.95), At(0.99), values[^1]);
	}

	private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

	// Times every stream report write (0x32 haptics, 0x36 haptics + speaker) and matches trigger requests to the report
	// 0x31 that carried them.
	private sealed class TimedLink(IHidLink inner) : IHidLink
	{
		private const int RightTriggerAt = 3 + OutputBlock.RightTrigger;

		private List<double> sends = [];

		private readonly List<(byte[] Effect, long Ticks)> pending = [];

		private int written, coalesced, dropped;

		private List<double> delays = [];

		public HidDevice Device => inner.Device;

		public int Read(Span<byte> buffer) => inner.Read(buffer);

		public void ExpectTrigger(byte[] effect, long ticks)
		{
			lock (pending)
			{
				pending.Add((effect, ticks));
			}
		}

		public void FailTrigger(long ticks)
		{
			lock (pending)
			{
				if (pending.RemoveAll(p => p.Ticks == ticks) > 0)
				{
					dropped++;
				}
			}
		}

		public (int Written, int Coalesced, int Dropped, double[] Delays) TakeTriggerResults()
		{
			lock (pending)
			{
				// Still pending at the end of a phase: never written.
				coalesced += pending.Count;
				pending.Clear();
				var result = (written, coalesced, dropped, delays.ToArray());
				written = coalesced = dropped = 0;
				delays = [];
				return result;
			}
		}

		public void Write(ReadOnlySpan<byte> report)
		{
			bool stream = report.Length > 0 && (report[0] == BluetoothReports.HapticsId || report[0] == SpeakerReports.ReportId);
			bool output = report.Length >= BluetoothReports.OutputSize && report[0] == BluetoothReports.OutputId;
			long start = Stopwatch.GetTimestamp();
			inner.Write(report);
			long done = Stopwatch.GetTimestamp();
			if (stream)
			{
				double ms = Stopwatch.GetElapsedTime(start, done).TotalMilliseconds;
				lock (this)
				{
					sends.Add(ms);
				}
			}
			if (output)
			{
				byte[] right = report.Slice(RightTriggerAt, TriggerEffects.Size).ToArray();
				lock (pending)
				{
					int match = pending.FindLastIndex(p => right.SequenceEqual(p.Effect));
					if (match >= 0)
					{
						// Requests before the one written were overtaken (coalesced into this write).
						coalesced += match;
						written++;
						delays.Add(Stopwatch.GetElapsedTime(pending[match].Ticks, done).TotalMilliseconds);
						pending.RemoveRange(0, match + 1);
					}
				}
			}
		}

		public double[] TakeSendTimes()
		{
			lock (this)
			{
				double[] taken = [.. sends];
				sends = [];
				return taken;
			}
		}

		public byte[] GetFeature(byte reportId) => inner.GetFeature(reportId);

		public void CancelRead() => inner.CancelRead();

		// The session disposes its link; the probe's `using` disposes it again: only the first call reaches the device.
		private int disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref disposed, 1) == 0)
			{
				inner.Dispose();
			}
		}
	}
}
