// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;
using Kopru.Probe;

// `Kopru.Probe speakermic [seconds]` (app CLOSED): is the controller's speaker audible? A direct Bluetooth session switches
// the mic on, plays ToneTest's 1 kHz on/off pattern on the speaker (SpeakerAudioSender.Volume 1.0 = speaker volume byte
// 0x64, the top of the range the speaker reacts to; tone at -6 dBFS) and listens with the controller's own mic.
internal static class SpeakerMic
{
	public const double SpeakerVolume = 1.0;

	public static int Run(IReadOnlyList<HidDevice> devices, string[] args)
	{
		HidDevice? bt = devices.FirstOrDefault(d => d.Transport == Transport.Bluetooth);
		if (bt == null)
		{
			Console.WriteLine("No Bluetooth DualSense visible (connected? app closed? hidden by HidHide?)");
			return 2;
		}
		int seconds = args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > 1 ? s : 8;

		RouteLink link = new(WindowsHid.Open(bt));
		using BluetoothSession session = new(link);
		using ConcentusMicDecoder decoder = new();
		short[] frame = new short[5760];
		List<short> pcm = [];
		List<(double Time, int End)> arrivals = [];
		long t0 = 0;
		int failed = 0;
		session.MicReportReceived += report =>
		{
			long now = Stopwatch.GetTimestamp();
			if (!MicPackets.TryGetOpus(report, out ReadOnlySpan<byte> opus))
			{
				failed++;
				return;
			}
			try
			{
				int n = decoder.Decode(opus, frame);
				lock (pcm)
				{
					pcm.AddRange(frame.AsSpan(0, n).ToArray());
					arrivals.Add((Stopwatch.GetElapsedTime(Volatile.Read(ref t0), now).TotalSeconds, pcm.Count));
				}
			}
			catch (Exception)
			{
				failed++;
			}
		};
		session.Start();
		Thread.Sleep(300);
		Console.WriteLine($"Headphones plugged: {session.HeadphonesPlugged} (true = the controller routes audio to its jack, not the speaker)");
		session.MicStreamEnabled = true;
		Thread.Sleep(1000);
		long micBefore = session.MicReports;
		if (micBefore == 0)
		{
			Console.WriteLine("No mic reports in the first second after asking for the mic");
		}

		SpeakerAudioSender sender = new(OpusEncoders.Create()) { Volume = SpeakerVolume, HeadphoneVolume = 0.5 };
		Console.WriteLine(F($"Speaker: Volume {SpeakerVolume:F1} (headphones {sender.HeadphoneVolume:F1}), tone {ToneTest.ToneHz:F0} Hz at {20 * Math.Log10(ToneTest.Amplitude):F0} dBFS, 1 s on / 1 s off for {seconds} s, encoder {sender.GetType().Name}"));
		Volatile.Write(ref t0, Stopwatch.GetTimestamp());
		lock (pcm)
		{
			pcm.Clear();
			arrivals.Clear();
		}
		session.Speaker = sender;
		byte[] chunk = new byte[480 * 8];
		long sample = 0;
		Stopwatch clock = Stopwatch.StartNew();
		List<long> speakerAt = [], micAt = [];
		long speaker0 = session.SpeakerReports, mic0 = session.MicReports;
		byte[]? routeReport = null;
		while (clock.Elapsed.TotalSeconds < seconds)
		{
			long due = (long)(clock.Elapsed.TotalSeconds * 48000) + 480 * 2; // stay ~20 ms ahead
			while (sample < due)
			{
				Array.Clear(chunk);
				for (int i = 0; i < 480; i++, sample++)
				{
					// Zeros in the off seconds keep 0x36 reports (silent Opus) going, so the path stays the same.
					short v = (short)Math.Round(32767 * ToneTest.Sample(sample / 48000.0, seconds));
					BitConverter.TryWriteBytes(chunk.AsSpan(i * 8), v);
					BitConverter.TryWriteBytes(chunk.AsSpan(i * 8 + 2), v);
				}
				session.PushSpeakerAudio(chunk);
			}
			if (clock.Elapsed.TotalSeconds >= speakerAt.Count + 1)
			{
				speakerAt.Add(session.SpeakerReports);
				micAt.Add(session.MicReports);
			}
			Thread.Sleep(5);
		}
		Thread.Sleep(1200); // let the last second's mic audio arrive
		routeReport = link.LastOutput;
		session.Speaker = null;
		session.MicStreamEnabled = false;
		Thread.Sleep(400);

		Console.WriteLine("Speaker reports per second: " + string.Join(" ", Deltas(speaker0, speakerAt)));
		Console.WriteLine("Mic reports per second:     " + string.Join(" ", Deltas(mic0, micAt)));
		Console.WriteLine($"0x36 audio sub-packets: speaker (0x93) {link.SpeakerPackets}, headset jack (0x96) {link.HeadsetPackets}, other {link.OtherPackets}");
		PrintRoute(routeReport);

		float[] mono;
		double offset;
		lock (pcm)
		{
			mono = pcm.Select(v => v / 32768f).ToArray();
			// Schedule time of sample 0: the median of (arrival - position), so arrival jitter does not shift the timeline.
			double[] offsets = arrivals.Select(a => a.Time - a.End / (double)MicPackets.SampleRate).OrderBy(v => v).ToArray();
			offset = offsets.Length > 0 ? offsets[offsets.Length / 2] : 0;
		}
		string wav = Path.Combine(Path.GetTempPath(), $"kopru-speakermic-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
		File.WriteAllBytes(wav, WavWriter.Build(pcm.ToArray(), MicPackets.SampleRate, 1));
		Console.WriteLine(F($"Mic: {session.MicReports - micBefore} reports while playing, decode failures {failed}, {mono.Length / (double)MicPackets.SampleRate:F1} s decoded"));
		Console.WriteLine($"Wrote {wav}");
		ToneTest.Result result = ToneTest.Analyze(mono, MicPackets.SampleRate, offset, seconds);
		return ToneTest.Report(result, seconds, mono.Length == 0 ? "no mic audio decoded" : "");
	}

	// The audio fields of the last report 0x31 sent while the speaker was attached (block offsets per AudioOutput).
	private static void PrintRoute(byte[]? report)
	{
		if (report == null)
		{
			Console.WriteLine("Route: no report 0x31 seen");
			return;
		}
		ReadOnlySpan<byte> b = report.AsSpan(3, OutputBlock.Size);
		byte flag0 = b[OutputBlock.Flag0], flag1 = b[OutputBlock.Flag1], control = b[AudioOutput.AudioControl];
		string path = (control & AudioOutput.PathMask) switch
		{
			AudioOutput.PathSpeaker => "speaker",
			AudioOutput.PathHeadphones => "headphones",
			int other => $"0x{other:X2} (unknown)"
		};
		Console.WriteLine(F($"Route (last 0x31 while playing): flag0 0x{flag0:X2} [audio control {On(flag0, AudioOutput.AudioControlEnable)}, speaker volume {On(flag0, AudioOutput.SpeakerVolumeEnable)}, ") +
			F($"headphone volume {On(flag0, AudioOutput.HeadphoneVolumeEnable)}, mic volume {On(flag0, AudioOutput.MicVolumeEnable)}], flag1 0x{flag1:X2} [audio control 2 {On(flag1, AudioOutput.AudioControl2Enable)}]"));
		Console.WriteLine(F($"  speaker volume 0x{b[AudioOutput.SpeakerVolume]:X2} (reacts ~0x{AudioOutput.MinSpeakerVolume:X2}..0x{AudioOutput.MaxSpeakerVolume:X2}), headphone volume 0x{b[AudioOutput.HeadphoneVolume]:X2}, ") +
			F($"mic volume 0x{b[AudioOutput.MicVolume]:X2}, audio control 0x{control:X2} (path {path}), audio control 2 0x{b[AudioOutput.AudioControl2]:X2} (preamp {b[AudioOutput.AudioControl2] & AudioOutput.PreampMask})"));
	}

	private static string On(byte flags, byte bit) => (flags & bit) != 0 ? "on" : "off";

	private static IEnumerable<long> Deltas(long start, List<long> marks)
	{
		long previous = start;
		foreach (long m in marks)
		{
			yield return m - previous;
			previous = m;
		}
	}

	private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

	// Remembers the last report 0x31 written and counts the audio sub-packet kinds of reports 0x36.
	private sealed class RouteLink(IHidLink inner) : IHidLink
	{
		private int disposed;

		public byte[]? LastOutput { get; private set; }

		public int SpeakerPackets, HeadsetPackets, OtherPackets;

		public HidDevice Device => inner.Device;

		public int Read(Span<byte> buffer) => inner.Read(buffer);

		public void Write(ReadOnlySpan<byte> report)
		{
			inner.Write(report);
			if (report.Length >= BluetoothReports.OutputSize && report[0] == BluetoothReports.OutputId)
			{
				LastOutput = report[..BluetoothReports.OutputSize].ToArray();
			}
			else if (report.Length > SpeakerReports.OpusHeaderOffset && report[0] == SpeakerReports.ReportId)
			{
				switch (report[SpeakerReports.OpusHeaderOffset] & 0x7F)
				{
					case SpeakerReports.SpeakerPacket:
						Interlocked.Increment(ref SpeakerPackets);
						break;
					case SpeakerReports.HeadsetPacket:
						Interlocked.Increment(ref HeadsetPackets);
						break;
					default:
						Interlocked.Increment(ref OtherPackets);
						break;
				}
			}
		}

		public byte[] GetFeature(byte reportId) => inner.GetFeature(reportId);

		public void CancelRead() => inner.CancelRead();

		public void Dispose()
		{
			if (Interlocked.Exchange(ref disposed, 1) == 0)
			{
				inner.Dispose();
			}
		}
	}
}
