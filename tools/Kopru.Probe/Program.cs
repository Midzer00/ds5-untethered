// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Kopru.Core.Bluetooth;
using Kopru.Core.Diagnostics;
using Kopru.Core.Hid;
using Kopru.Core.Input;
using Kopru.Core.Protocol;
using Kopru.Probe;

// Developer probe.
//   Kopru.Probe            lists the DualSense interfaces this program can see and reads input for two seconds
//   Kopru.Probe adapter    Bluetooth adapter check (5 s idle, 5 s with haptics) and its verdict
//   Kopru.Probe speaker [s] EXPERIMENTAL: a 440 Hz tone (speaker, or headphones if plugged) for s seconds (default 5),
//                          with a 1 s pulse on the haptics; Opus by Concentus (managed), or libopus (opus.dll next
//                          to the program) with KOPRU_OPUS=native
//   Kopru.Probe mic [s] [off|stop] EXPERIMENTAL: asks for the controller mic for s seconds (default 10; off = control
//                          run, not asked for; stop = on for 2 s, then the mic-off value for the rest), prints mic
//                          reports per second, groups reports by id + byte-1 low nibble and writes every non-input report
//                          plus a per-offset byte table to %TEMP%\kopru-mic-*.bin/.txt and the decoded mic as .wav
//   Kopru.Probe winmic [s] [name] lists the active Windows capture endpoints, picks the virtual DualSense's microphone
//                          (name contains "DualSense" or "Wireless Controller", or the given name substring), records it
//                          for s seconds (default 5, WASAPI shared mode, mix format) and prints format, level per second,
//                          whether L == R, and a verdict; writes %TEMP%\kopru-winmic-*.wav.
//                          Exit 0 audio received, 4 silence only, 2 no capture endpoint found, 3 capture failed
//   Kopru.Probe pad [s]    reads the virtual (USB) DualSense that games see for s seconds (default 5) without sending
//                          anything: rate, per-axis min/max/distinct, buttons and D-pad values seen, stick jumps (> 8),
//                          and a verdict. Exit 0 steady, 5 activity seen, 2 no USB DualSense visible, 3 no input reports
//   Kopru.Probe micload [s] [nospeaker] (app closed) what the Bluetooth mic costs: no stream / haptics only / + mic /
//                          + speaker + mic, s seconds each (default 8); in every phase the trigger effect alternates every
//                          250 ms (request -> written delay, coalesced/dropped, input-byte reaction); one line per phase
//                          and a verdict (see MicLoad.cs)
//   Kopru.Probe speakermic [s] (app closed) loopback with the controller's own mic as the ear: 1 kHz on/off on the
//                          speaker for s seconds (default 8), tone level per second, route bytes, headphones flag, verdict
//                          (see SpeakerMic.cs, ToneTest.cs). Exit 0 heard, 4 not heard, 5 inconclusive (mic silent), 2 no pad
//   Kopru.Probe looptest [s] [render-name] [capture-name] (app running, mic on) the same tone through Windows: played on the
//                          virtual pad's render endpoint, recorded from its capture endpoint (see LoopTest.cs); exit codes
//                          as speakermic, 2 also when an endpoint is missing, 3 playback/capture failed
//   Kopru.Probe audiodefaults [roundtrip] lists the active speakers and microphones, the roles each is the default for and
//                          whether it is a virtual pad endpoint (same identification as the app, with how it was decided);
//                          roundtrip (app closed): switches console + multimedia to another endpoint and back, per flow,
//                          always restoring the originals (see AudioDefaultsProbe.cs). Exit 0 works, 2 nothing to switch,
//                          3 setting failed, 4 restoring failed
if (args.FirstOrDefault() == "audiodefaults" && OperatingSystem.IsWindows())
{
	return AudioDefaultsProbe.Run(args.Skip(1).ToArray());
}

IReadOnlyList<HidDevice> devices = WindowsHid.FindDualSenses();
Console.WriteLine($"Found {devices.Count} DualSense interface(s)");
foreach (HidDevice d in devices)
{
	Console.WriteLine($"  {d.Transport} {(d.IsEdge ? "Edge" : "DualSense")} in {d.InputLength} out {d.OutputLength} feature {d.FeatureLength}");
}

if (args.FirstOrDefault() == "adapter")
{
	HidDevice? bt = devices.FirstOrDefault(d => d.Transport == Transport.Bluetooth);
	if (bt == null)
	{
		Console.WriteLine("No Bluetooth DualSense visible (connected? hidden by HidHide?)");
		return 2;
	}
	using BluetoothSession session = new(WindowsHid.Open(bt));
	session.Start();
	Console.WriteLine("Measuring 10 s...");
	AdapterReport? report = await AdapterCheck.RunAsync(session, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
	if (report == null)
	{
		Console.WriteLine("Not enough input reports");
		return 3;
	}
	Console.WriteLine($"Verdict: {report.VerdictText}");
	foreach (string reason in report.Reasons)
	{
		Console.WriteLine("  " + reason);
	}
	Console.WriteLine($"Idle {report.Idle.RateHz:F0} Hz, extra p99 {report.Idle.ExtraDelayMs.P99:F1} ms, lost {report.Idle.Lost}; " +
		$"load {report.Load.RateHz:F0} Hz, extra p99 {report.Load.ExtraDelayMs.P99:F1} ms, lost {report.Load.Lost}; haptics {report.HapticsRateHz:F1}/s");
	return 0;
}

if (args.FirstOrDefault() == "speaker")
{
	HidDevice? bt = devices.FirstOrDefault(d => d.Transport == Transport.Bluetooth);
	if (bt == null)
	{
		Console.WriteLine("No Bluetooth DualSense visible (connected? hidden by HidHide?)");
		return 2;
	}
	int seconds = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 5;
	Kopru.Core.Audio.IOpusEncoder encoder = Kopru.Core.Audio.OpusEncoders.Create();
	Console.WriteLine($"Opus encoder: {encoder.GetType().Name}");
	if (Kopru.Core.Audio.OpusEncoders.NativeRequested && encoder is not Kopru.Core.Audio.NativeOpusEncoder)
	{
		Console.WriteLine("KOPRU_OPUS=native but opus.dll could not be loaded: using Concentus");
	}
	using BluetoothSession session = new(WindowsHid.Open(bt));
	session.Start();
	session.Speaker = new Kopru.Core.Audio.SpeakerAudioSender(encoder) { Volume = 0.6 };
	Thread.Sleep(200);
	Console.WriteLine($"Headphones plugged: {session.Speaker.Headset}. Playing 440 Hz for {seconds} s...");
	byte[] chunk = new byte[480 * 8];
	long sample = 0;
	Stopwatch clock = Stopwatch.StartNew();
	while (clock.Elapsed.TotalSeconds < seconds)
	{
		long due = (long)(clock.Elapsed.TotalSeconds * 48000) + 480 * 2; // stay ~20 ms ahead
		while (sample < due)
		{
			for (int i = 0; i < 480; i++, sample++)
			{
				short tone = (short)(8000 * Math.Sin(2 * Math.PI * 440 * sample / 48000.0));
				BitConverter.TryWriteBytes(chunk.AsSpan(i * 8), tone);
				BitConverter.TryWriteBytes(chunk.AsSpan(i * 8 + 2), tone);
			}
			session.PushSpeakerAudio(chunk);
			if ((sample / 48000) % 2 == 0)
			{
				for (int i = 0; i < 30; i++)
				{
					session.Haptics.Push((sbyte)(i % 2 == 0 ? 40 : -40), (sbyte)(i % 2 == 0 ? 40 : -40));
				}
			}
		}
		Thread.Sleep(5);
	}
	Thread.Sleep(300);
	Console.WriteLine($"Sent {session.SpeakerReports} speaker reports (0x36), {session.HapticsReports} stream reports in all");
	session.Speaker = null;
	return 0;
}

if (args.FirstOrDefault() == "mic")
{
	// mic [s] [off]: off is the control run (the mic is never asked for, so no stream reports are sent either).
	// Report kinds are grouped by id + the LOW nibble of byte 1 (the high nibble is a sequence). Every report that is
	// not "id 31, type 1" (ordinary gamepad input) goes to the .bin; the first 80 and the first 5 ordinary ones go to the
	// .txt as hex with a millisecond timestamp; for the other types a per-offset table (min, max, distinct, kind) shows
	// where a header ends and a payload starts.
	HidDevice? bt = devices.FirstOrDefault(d => d.Transport == Transport.Bluetooth);
	if (bt == null)
	{
		Console.WriteLine("No Bluetooth DualSense visible (connected? hidden by HidHide?)");
		return 2;
	}
	int seconds = args.Length > 1 && int.TryParse(args[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int s) ? s : 10;
	bool askMic = !args.Skip(1).Any(a => string.Equals(a, "off", StringComparison.OrdinalIgnoreCase));
	// stop: mic on for 2 s, then the mic-off value in stream reports for the rest; shows whether the controller obeys.
	bool stopRun = args.Skip(1).Any(a => string.Equals(a, "stop", StringComparison.OrdinalIgnoreCase));
	string mode = stopRun ? "stop" : askMic ? "on" : "off";
	string stem = Path.Combine(Path.GetTempPath(), $"kopru-mic-{mode}-{DateTime.Now:yyyyMMdd-HHmmss}");
	int[] micPerSecond = new int[seconds + 2];
	List<short> pcm = [];
	List<string> notes = [];
	List<string> ordinary = [];
	Dictionary<string, int> groups = [];
	Dictionary<string, int> detail = [];
	Dictionary<string, List<byte[]>> others = [];
	using MemoryStream raw = new();
	using Kopru.Core.Audio.ConcentusMicDecoder decoder = new();
	short[] frame = new short[5760];
	int decoded = 0, failed = 0;
	Stopwatch clock = Stopwatch.StartNew();
	using BluetoothSession session = new(WindowsHid.Open(bt));
	session.RawReportReceived += report =>
	{
		double ms = clock.Elapsed.TotalMilliseconds;
		int b1 = report.Length > 1 ? report[1] : 0;
		string group = $"id {report[0]:X2} type {b1 & 0x0F:X1} len {report.Length}";
		bool input = report[0] == BluetoothReports.InputId && (b1 & 0x0F) == 1;
		string line = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{ms,9:F1} ms  {Convert.ToHexString(report)}");
		lock (groups)
		{
			if (Kopru.Core.Audio.MicPackets.IsMicReport(report))
			{
				micPerSecond[Math.Min((int)(ms / 1000), micPerSecond.Length - 1)]++;
			}
			groups[group] = groups.GetValueOrDefault(group) + 1;
			string kind = $"id {report[0]:X2} b1 {b1:X2} len {report.Length} crc {(BluetoothReports.InputCrcValid(report) ? "ok" : "bad")}";
			detail[kind] = detail.GetValueOrDefault(kind) + 1;
			if (input)
			{
				if (ordinary.Count < 5)
				{
					ordinary.Add(line);
				}
				return;
			}
			raw.Write(BitConverter.GetBytes((ushort)report.Length));
			raw.Write(report);
			if (notes.Count < 80)
			{
				notes.Add(line);
			}
			if (!others.TryGetValue(group, out List<byte[]>? list))
			{
				others[group] = list = [];
			}
			list.Add(report.ToArray());
		}
	};
	session.MicReportReceived += report =>
	{
		if (!Kopru.Core.Audio.MicPackets.TryGetOpus(report, out ReadOnlySpan<byte> opus))
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
			}
			decoded++;
		}
		catch (Exception)
		{
			failed++;
		}
	};
	// Only the stop run sends "mic off" by itself; the other runs watch the controller untouched.
	session.StopStrayMic = false;
	session.Start();
	if (stopRun)
	{
		session.MicStreamEnabled = true;
		Console.WriteLine($"Stop run: mic requested for 2 s, then the mic-off value (0xFE) in stream reports for {Math.Max(0, seconds - 2)} s...");
		Thread.Sleep(TimeSpan.FromSeconds(Math.Min(2, seconds)));
		session.MicStreamEnabled = false;
		// About 94 stream reports per second for the rest of the run.
		session.SendMicOff((int)(Math.Max(0, seconds - 2) * 94));
		Thread.Sleep(TimeSpan.FromSeconds(Math.Max(0, seconds - 2)));
		Console.WriteLine($"Sent {session.MicOffSent} stream reports with the mic-off value");
	}
	else if (askMic)
	{
		session.MicStreamEnabled = true;
		Console.WriteLine($"Mic requested (stream control 0xFF). Speak into the controller for {seconds} s...");
		Thread.Sleep(TimeSpan.FromSeconds(seconds));
	}
	else
	{
		Console.WriteLine($"Control run: mic NOT requested (no stream reports sent). Speak into the controller for {seconds} s...");
		Thread.Sleep(TimeSpan.FromSeconds(seconds));
	}
	// Ends like the app: switching off sends the short mic-off burst (none in the control run: the mic never went on).
	session.MicStreamEnabled = false;
	Thread.Sleep(400);
	Console.WriteLine("Mic reports per second: " + string.Join(" ", micPerSecond.Take(Math.Min(micPerSecond.Length, (int)clock.Elapsed.TotalSeconds + 1))));
	double elapsed = clock.Elapsed.TotalSeconds;
	List<string> output = [];
	List<string> tables = [];
	lock (groups)
	{
		output.Add($"Report kinds over {elapsed:F1} s (id + low nibble of byte 1):");
		foreach ((string group, int count) in groups.OrderByDescending(g => g.Value))
		{
			output.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"  {group}: {count} ({count / elapsed:F1}/s)"));
		}
		foreach ((string group, List<byte[]> list) in others)
		{
			tables.Add($"Byte table for {group} ({list.Count} reports): offset min max distinct kind");
			int length = list.Min(r => r.Length);
			for (int i = 0; i < length; i++)
			{
				byte[] column = list.Select(r => r[i]).ToArray();
				int distinct = column.Distinct().Count();
				int steps = 0;
				for (int k = 1; k < column.Length; k++)
				{
					if ((byte)(column[k] - column[k - 1]) == 1)
					{
						steps++;
					}
				}
				string kind = distinct == 1 ? "const" : column.Length > 2 && steps >= (column.Length - 1) * 0.8 ? "counter" : "varying";
				tables.Add($"  {i,3} {column.Min():X2} {column.Max():X2} {distinct,3} {kind}");
			}
		}
		File.WriteAllLines(stem + ".txt", output.Concat(["", "Per kind (byte 1 in full, CRC check):"])
			.Concat(detail.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"  {k.Key}: {k.Value}"))
			.Concat(["", "First 5 ordinary input reports (id 31 type 1), hex:"]).Concat(ordinary)
			.Concat(["", "First 80 other reports, hex:"]).Concat(notes)
			.Concat([""]).Concat(tables));
	}
	File.WriteAllBytes(stem + ".wav", Kopru.Core.Audio.WavWriter.Build(pcm.ToArray(), 48000, 1));
	File.WriteAllBytes(stem + ".bin", raw.ToArray());
	output.ForEach(Console.WriteLine);
	tables.ForEach(Console.WriteLine);
	Console.WriteLine($"Mic reports {session.MicReports}, decoded {decoded}, failed {failed}, {pcm.Count / 48000.0:F1} s of audio");
	Console.WriteLine($"Wrote {stem}.txt");
	Console.WriteLine($"      {stem}.bin (every report except id 31 type 1: u16 length + bytes)");
	Console.WriteLine($"      {stem}.wav");
	return 0;
}

if (args.FirstOrDefault() == "winmic" && OperatingSystem.IsWindows())
{
	return WinMic(args.Skip(1).ToArray());
}

if (args.FirstOrDefault() == "speakermic")
{
	return SpeakerMic.Run(devices, args.Skip(1).ToArray());
}

if (args.FirstOrDefault() == "looptest" && OperatingSystem.IsWindows())
{
	return LoopTest.Run(args.Skip(1).ToArray());
}

if (args.FirstOrDefault() == "micload")
{
	return MicLoad.Run(devices, args.Skip(1).ToArray());
}

if (args.FirstOrDefault() == "pad")
{
	return Pad(devices, args.Skip(1).ToArray());
}

if (args.FirstOrDefault() == "capture")
{
	// The controller on a USB cable: its descriptors and feature reports, saved to captures/ (not in git: it holds the
	// controller's own Bluetooth address).
	HidDevice? usb = devices.FirstOrDefault(d => d.Transport == Transport.Usb);
	Kopru.Core.Usb.UsbCaptureResult? capture = Kopru.Core.Usb.UsbCapture.Capture(HidDevice.SonyVendorId, usb?.ProductId ?? HidDevice.DualSenseProductId);
	if (capture == null)
	{
		Console.WriteLine("No DualSense on a USB cable");
		return 2;
	}
	Dictionary<string, string> features = [];
	if (usb != null)
	{
		using HidConnection connection = WindowsHid.Open(usb);
		foreach (byte id in WindowsHid.FeatureReportIds(usb))
		{
			try
			{
				features[$"0x{id:X2}"] = Convert.ToHexString(connection.GetFeature(id));
			}
			catch (Exception e)
			{
				features[$"0x{id:X2}"] = "error: " + e.Message;
			}
		}
	}
	var saved = new
	{
		capture.Strings,
		Device = Convert.ToHexString(capture.DeviceDescriptor),
		Configuration = Convert.ToHexString(capture.ConfigurationDescriptor),
		HidReportDescriptors = capture.HidReportDescriptors.ToDictionary(p => p.Key.ToString(), p => Convert.ToHexString(p.Value)),
		FeatureReports = features,
		capture.Notes
	};
	Directory.CreateDirectory("captures");
	string file = Path.Combine("captures", $"dualsense-usb-{DateTime.Now:yyyyMMdd-HHmmss}.json");
	File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(saved, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
	Console.WriteLine($"Device {capture.DeviceDescriptor.Length} B, configuration {capture.ConfigurationDescriptor.Length} B, strings {string.Join(", ", capture.Strings.Values)}");
	Console.WriteLine($"HID report descriptors: {string.Join(", ", capture.HidReportDescriptors.Select(p => $"interface {p.Key}: {p.Value.Length} B"))}");
	Console.WriteLine($"Feature reports: {string.Join(" ", features.Keys)}");
	foreach (string note in capture.Notes)
	{
		Console.WriteLine("  " + note);
	}
	Console.WriteLine("Saved " + Path.GetFullPath(file));
	return 0;
}

if (args.FirstOrDefault() == "virtual")
{
	// Plugs our virtual DualSense in through usbip-win2 for N seconds (default 20), feeds neutral input and counts what
	// Windows and games do with it, then unplugs it.
	string? captureFile = Directory.Exists("captures") ? Directory.GetFiles("captures", "*.json").OrderBy(f => f).LastOrDefault() : null;
	if (captureFile == null)
	{
		Console.WriteLine("No capture: run 'capture' with the controller on a cable first");
		return 2;
	}
	int seconds = args.Length > 1 ? int.Parse(args[1]) : 20;
	using Kopru.Core.Virtual.VirtualDualSense pad = new(Kopru.Core.Virtual.DeviceIdentity.FromCaptureFile(captureFile));
	int outputs = 0;
	long audioBytes = 0;
	int speakerPeak = 0;
	int hapticsPeak = 0;
	pad.OutputReceived += _ => Interlocked.Increment(ref outputs);
	pad.AudioReceived += a =>
	{
		Interlocked.Add(ref audioBytes, a.Length);
		ReadOnlySpan<byte> s = a.Span;
		for (int f = 0; f + 7 < s.Length; f += 8)
		{
			int speaker = Math.Max(Math.Abs((int)BitConverter.ToInt16(s[f..])), Math.Abs((int)BitConverter.ToInt16(s[(f + 2)..])));
			int haptic = Math.Max(Math.Abs((int)BitConverter.ToInt16(s[(f + 4)..])), Math.Abs((int)BitConverter.ToInt16(s[(f + 6)..])));
			if (speaker > speakerPeak) speakerPeak = speaker;
			if (haptic > hapticsPeak) hapticsPeak = haptic;
		}
	};
	string hapticTool = Path.GetFullPath(Path.Combine("..", "tools", "bin", "HapticTest", "HapticTest.exe"));
	HashSet<string> speakersBefore = SpeakerNames(hapticTool);
	await using Kopru.Core.UsbIp.UsbIpServer server = new(pad);
	server.Start();
	string usbip = @"C:\Program Files\USBip\usbip.exe";
	Process attach = Process.Start(new ProcessStartInfo(usbip, $"-t {server.Port} attach -r 127.0.0.1 -b {Kopru.Core.Virtual.VirtualDualSense.BusId} --once --terse")
	{ RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
	string attachOut = (await attach.StandardOutput.ReadToEndAsync()).Trim();
	await attach.WaitForExitAsync();
	Console.WriteLine($"attach exit {attach.ExitCode}, port '{attachOut}' {(await attach.StandardError.ReadToEndAsync()).Trim()}");
	// Input with a mark games can't miss: L2 at 123.
	byte[] neutral = new byte[64];
	neutral[0] = 0x01;
	neutral[1] = neutral[2] = neutral[3] = neutral[4] = 0x80;
	neutral[5] = 123;
	neutral[8] = 0x08;
	using CancellationTokenSource feeding = new();
	Task feeder = Task.Run(async () =>
	{
		while (!feeding.IsCancellationRequested)
		{
			neutral[7]++;
			pad.PushInput(neutral);
			await Task.Delay(4);
		}
	});

	await Task.Delay(4000);
	string? ourSpeaker = SpeakerNames(hapticTool).Except(speakersBefore).FirstOrDefault();
	Console.WriteLine($"our speaker: {ourSpeaker ?? "(not found)"}; control {pad.ControlRequests}");

	// Input: which visible wired DualSense reads L2 = 123?
	int marked = 0;
	foreach (HidDevice usbPad in WindowsHid.FindDualSenses().Where(d => d.Transport == Transport.Usb))
	{
		if (ReadsMark(usbPad))
		{
			marked++;
		}
	}
	Console.WriteLine($"input: {marked} wired DualSense interface(s) read L2 = 123 (our input reaching apps)");

	// Haptics: play the left-grip tone (0.5 level) on our speaker, as a game would.
	if (ourSpeaker != null)
	{
		Process play = Process.Start(new ProcessStartInfo(hapticTool, ["left", "--amp", "0.5", "--device", ourSpeaker])
		{ RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })!;
		await play.WaitForExitAsync();
		Console.WriteLine($"haptic tool exit {play.ExitCode} {(await play.StandardError.ReadToEndAsync()).Trim()}");
	}
	Console.WriteLine($"audio {audioBytes} B ({audioBytes / 8 / 48.0:F0} ms), speaker alt {pad.AlternateSetting(1)}, peaks: speaker {speakerPeak}, haptics {hapticsPeak} (0.5 x 32767 = 16384)");
	await Task.Delay(Math.Max(0, seconds * 1000 - 8000));
	feeding.Cancel();
	await feeder;
	if (int.TryParse(attachOut, out int port))
	{
		Process.Start(new ProcessStartInfo(usbip, $"detach -p {port}") { UseShellExecute = false, CreateNoWindow = true })!.WaitForExit();
	}
	Console.WriteLine($"done: control {pad.ControlRequests}, outputs {outputs}, audio {audioBytes} B; detached port {attachOut}");
	return 0;
}

if (args.FirstOrDefault() == "bridge")
{
	// Runs the full bridge for N seconds (default 30): Bluetooth DualSense <-> virtual wired DualSense.
	int seconds = args.Length > 1 ? int.Parse(args[1]) : 30;
	await using Kopru.Core.Bridge.BridgeHost host = Kopru.Core.Bridge.BridgeHost.CreateDefault();
	host.StateChanged += s => Console.WriteLine($"state: {s} {host.Status.LastErrorText()} {host.Status.LastErrorDetail}");
	host.LowBattery += p => Console.WriteLine(Kopru.Core.Localization.Text.BatteryLow(p));
	host.IdleTimeout += () => Console.WriteLine(host.IdleMessage());
	await host.StartAsync();
	for (int t = 5; t <= seconds && host.State != Kopru.Core.Bridge.BridgeState.Error; t += 5)
	{
		await Task.Delay(5000);
		Kopru.Core.Bridge.BridgeStatus st = host.Status;
		Console.WriteLine($"{t,3} s: {st.State}, input {st.InputRateHz:F0} Hz, haptics {st.HapticsReportsPerSecond:F0}/s, battery {st.Battery}");
	}
	await host.StopAsync();
	Console.WriteLine("done");
	return host.Status.LastError is Kopru.Core.Bridge.BridgeError.None or Kopru.Core.Bridge.BridgeError.ControllerLost ? 0 : 2;
}

if (args.FirstOrDefault() == "feel")
{
	// 8 s: green light, resistance on both triggers, haptics left (low), right (high), both.
	HidDevice? bt = devices.FirstOrDefault(d => d.Transport == Transport.Bluetooth);
	if (bt == null)
	{
		Console.WriteLine("No Bluetooth DualSense visible");
		return 2;
	}
	using BluetoothSession session = new(WindowsHid.Open(bt));
	session.Start();
	session.UpdateOutput(b =>
	{
		OutputBlock.SetLightbar(b, 0, 200, 60);
		OutputBlock.SetTriggers(b, TriggerEffects.Feedback(2, 7), TriggerEffects.Feedback(2, 7));
	});
	double phaseL = 0;
	double phaseR = 0;
	Stopwatch feel = Stopwatch.StartNew();
	while (feel.Elapsed < TimeSpan.FromSeconds(8))
	{
		double t = feel.Elapsed.TotalSeconds;
		int part = (int)(t / 1.5) % 3;
		while (session.Haptics.Buffered < 96)
		{
			sbyte l = part != 1 ? (sbyte)(70 * Math.Sin(phaseL)) : (sbyte)0;
			sbyte r = part != 0 ? (sbyte)(70 * Math.Sin(phaseR)) : (sbyte)0;
			session.Haptics.Push(l, r);
			phaseL += 2 * Math.PI * 80 / BluetoothReports.HapticsSampleRate;
			phaseR += 2 * Math.PI * 160 / BluetoothReports.HapticsSampleRate;
		}
		session.WakeHaptics();
		Thread.Sleep(10);
	}
	session.UpdateOutput(b =>
	{
		OutputBlock.SetLightbar(b, 0, 0, 255);
		OutputBlock.SetTriggers(b, TriggerEffects.Off(), TriggerEffects.Off());
	});
	Thread.Sleep(400);
	Console.WriteLine($"Done: {session.HapticsReports} haptics reports, input {session.InputReports}, L2/R2 last {session.LastInput.L2}/{session.LastInput.R2}");
	return 0;
}

if (devices.Count == 0)
{
	return 1;
}
ReadForTwoSeconds(devices[0]);
return 0;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static int WinMic(string[] rest)
{
	System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
	int seconds = 5;
	string? wanted = null;
	foreach (string a in rest)
	{
		if (int.TryParse(a, System.Globalization.NumberStyles.Integer, inv, out int s) && s > 0)
		{
			seconds = s;
		}
		else
		{
			wanted = a;
		}
	}
	IReadOnlyList<CaptureEndpoint> endpoints = WasapiCapture.ListEndpoints();
	string[] patterns = wanted != null ? [wanted] : ["DualSense", "Wireless Controller"];
	CaptureEndpoint? pick = null;
	string? matchedBy = null;
	foreach (string pattern in patterns)
	{
		pick = endpoints.FirstOrDefault(e => e.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase));
		if (pick != null)
		{
			matchedBy = pattern;
			break;
		}
	}
	Console.WriteLine($"Active capture endpoints: {endpoints.Count}");
	foreach (CaptureEndpoint e in endpoints)
	{
		Console.WriteLine($"  {(e == pick ? "*" : " ")} {e.Name}");
	}
	if (pick == null)
	{
		Console.WriteLine($"No endpoint name contains {string.Join(" or ", patterns.Select(p => $"\"{p}\""))}");
		Console.WriteLine("Verdict: no capture endpoint found");
		return 2;
	}
	int matches = endpoints.Count(e => e.Name.Contains(matchedBy!, StringComparison.OrdinalIgnoreCase));
	Console.WriteLine($"Using \"{pick.Name}\": name contains \"{matchedBy}\"{(wanted != null ? " (given)" : "")}" +
		(matches > 1 ? $"; {matches} endpoints match, took the first" : ""));
	Console.WriteLine($"Recording {seconds} s...");
	CaptureResult r;
	try
	{
		r = WasapiCapture.Record(pick.Index, TimeSpan.FromSeconds(seconds));
	}
	catch (Exception e)
	{
		Console.WriteLine($"Capture failed: {e.GetType().Name}: {e.Message}");
		return 3;
	}
	Console.WriteLine($"Format: {r.Rate} Hz, {r.Channels} ch, {r.Bits}-bit {(r.Float ? "float" : "PCM")} (shared-mode mix format)");
	Console.WriteLine($"Frames captured: {r.Frames} ({(r.Rate > 0 ? r.Frames / (double)r.Rate : 0):F2} s), packets {r.Packets}, flagged silent {r.SilentPackets}");
	double sum = 0, peak = 0;
	long nonZero = 0;
	foreach (float v in r.Samples)
	{
		sum += (double)v * v;
		peak = Math.Max(peak, Math.Abs(v));
		if (v != 0)
		{
			nonZero++;
		}
	}
	double rms = r.Samples.Length > 0 ? Math.Sqrt(sum / r.Samples.Length) : 0;
	Console.WriteLine($"Overall RMS {Db(rms)} dBFS, peak {Db(peak)} dBFS, non-zero samples {nonZero} of {r.Samples.Length}");
	List<string> perSecond = [];
	int frameStep = Math.Max(1, r.Rate);
	for (int start = 0; start < r.Frames; start += frameStep)
	{
		int end = Math.Min(r.Frames, start + frameStep);
		double s2 = 0;
		for (int i = start * r.Channels; i < end * r.Channels; i++)
		{
			s2 += (double)r.Samples[i] * r.Samples[i];
		}
		perSecond.Add(Db(Math.Sqrt(s2 / ((end - start) * r.Channels))));
	}
	Console.WriteLine($"RMS per second (dBFS): {string.Join(" ", perSecond)}");
	if (r.Channels >= 2)
	{
		long differ = 0;
		for (int f = 0; f < r.Frames; f++)
		{
			if (r.Samples[f * r.Channels] != r.Samples[f * r.Channels + 1])
			{
				differ++;
			}
		}
		Console.WriteLine(differ == 0 ? "Left and right: identical" : $"Left and right: differ in {differ} of {r.Frames} frames");
	}
	else
	{
		Console.WriteLine("Left and right: mono endpoint");
	}
	short[] pcm = new short[r.Samples.Length];
	for (int i = 0; i < pcm.Length; i++)
	{
		pcm[i] = (short)Math.Clamp(Math.Round(r.Samples[i] * 32767.0), short.MinValue, short.MaxValue);
	}
	string wav = Path.Combine(Path.GetTempPath(), $"kopru-winmic-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
	File.WriteAllBytes(wav, Kopru.Core.Audio.WavWriter.Build(pcm, r.Rate, Math.Max(1, r.Channels)));
	Console.WriteLine($"Wrote {wav} (16-bit PCM)");
	// Digital silence: no frames, all zeros, or an RMS below -80 dBFS.
	bool audio = r.Frames > 0 && nonZero > 0 && rms > Math.Pow(10, -80 / 20.0);
	Console.WriteLine(audio ? "Verdict: audio received"
		: $"Verdict: silence only ({(r.Frames == 0 ? "no frames arrived" : nonZero == 0 ? "all samples zero" : "RMS below -80 dBFS")})");
	return audio ? 0 : 4;
}

static string Db(double level) => level > 0
	? (20 * Math.Log10(level)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
	: "-inf";

static int Pad(IReadOnlyList<HidDevice> devices, string[] rest)
{
	int seconds = rest.Length > 0 && int.TryParse(rest[0], System.Globalization.NumberStyles.Integer,
		System.Globalization.CultureInfo.InvariantCulture, out int s) && s > 0 ? s : 5;
	List<HidDevice> usb = devices.Where(d => d.Transport == Transport.Usb).ToList();
	int bluetooth = devices.Count(d => d.Transport == Transport.Bluetooth);
	if (usb.Count == 0)
	{
		Console.WriteLine("No USB DualSense visible: is the app running with a virtual DualSense (not DS4 / Xbox emulation)?");
		return 2;
	}
	HidDevice pad = usb[0];
	Console.WriteLine($"Using the USB {(pad.IsEdge ? "DualSense Edge" : "DualSense")} (the virtual pad games see)" +
		(bluetooth > 0 ? $"; {bluetooth} Bluetooth interface(s) also visible, ignored" : "") +
		(usb.Count > 1 ? $"; {usb.Count} USB interfaces visible, took the first (a real pad on a cable would also count)" : ""));
	Console.WriteLine($"Reading for {seconds} s, read-only, nothing sent...");

	string[] axes = ["LX", "LY", "RX", "RY", "L2", "R2"];
	byte[] first = new byte[6], previous = new byte[6], min = new byte[6], max = new byte[6];
	HashSet<byte>[] distinct = axes.Select(_ => new HashSet<byte>()).ToArray();
	SortedSet<int> dpad = [];
	PadButtons everSet = PadButtons.None;
	int reports = 0, jumps = 0, other = 0;
	using ReadOnlyHidConnection connection = WindowsHid.OpenReadOnly(pad);
	Stopwatch clock = Stopwatch.StartNew();
	Thread reader = new(() =>
	{
		byte[] buffer = new byte[Math.Max(64, pad.InputLength)];
		try
		{
			while (clock.Elapsed.TotalSeconds < seconds)
			{
				int length = connection.Read(buffer);
				ReadOnlySpan<byte> report = buffer.AsSpan(0, length);
				InputSnapshot? snap = InputSnapshot.FromReport(report, bluetooth: false);
				if (snap == null)
				{
					other++;
					continue;
				}
				byte[] values = [snap.LeftX, snap.LeftY, snap.RightX, snap.RightY, snap.L2, snap.R2];
				for (int i = 0; i < 6; i++)
				{
					if (reports == 0)
					{
						first[i] = min[i] = max[i] = values[i];
					}
					min[i] = Math.Min(min[i], values[i]);
					max[i] = Math.Max(max[i], values[i]);
					distinct[i].Add(values[i]);
				}
				if (reports > 0 && Enumerable.Range(0, 4).Any(i => Math.Abs(values[i] - previous[i]) > 8))
				{
					jumps++;
				}
				values.CopyTo(previous, 0);
				everSet |= snap.Buttons;
				dpad.Add(InputState.Body(report)[InputSnapshot.Buttons0] & 0x0F);
				reports++;
			}
		}
		catch (Exception)
		{
			// Cancelled at the deadline (no report arrived) or the pad went away.
		}
	})
	{ IsBackground = true };
	reader.Start();
	if (!reader.Join(TimeSpan.FromSeconds(seconds + 1)))
	{
		connection.CancelRead();
		reader.Join(TimeSpan.FromSeconds(2));
	}
	double elapsed = Math.Min(clock.Elapsed.TotalSeconds, seconds);
	if (reports == 0)
	{
		Console.WriteLine($"No input reports in {seconds} s ({other} other reports)");
		return 3;
	}
	Console.WriteLine($"{reports} input reports in {elapsed:F1} s: {reports / elapsed:F0} reports/s{(other > 0 ? $" ({other} other reports)" : "")}");
	for (int i = 0; i < 6; i++)
	{
		Console.WriteLine($"  {axes[i]}: first {first[i],3}  min {min[i],3}  max {max[i],3}  distinct {distinct[i].Count}");
	}
	Console.WriteLine($"Buttons ever set: {(everSet == PadButtons.None ? "none" : everSet.ToString())} (D-pad directions included)");
	Console.WriteLine($"D-pad values seen: {string.Join(" ", dpad)} (8 = released)");
	Console.WriteLine($"Stick jumps (> 8 from the previous report): {jumps}");
	List<string> moved = [];
	for (int i = 0; i < 4; i++)
	{
		int spread = Math.Max(max[i] - first[i], first[i] - min[i]);
		if (spread > 8)
		{
			moved.Add($"{axes[i]} moved {spread} from its first value");
		}
	}
	for (int i = 4; i < 6; i++)
	{
		if (max[i] > 0)
		{
			moved.Add($"{axes[i]} up to {max[i]}");
		}
	}
	if (everSet != PadButtons.None)
	{
		moved.Add($"buttons {everSet}");
	}
	Console.WriteLine(moved.Count == 0 ? "Verdict: steady" : $"Verdict: activity seen ({string.Join("; ", moved)})");
	return moved.Count == 0 ? 0 : 5;
}

static HashSet<string> SpeakerNames(string hapticTool)
{
	Process list = Process.Start(new ProcessStartInfo(hapticTool, "--list")
	{ RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 })!;
	string text = list.StandardOutput.ReadToEnd();
	list.WaitForExit();
	return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(" | ")[0].Trim()).Where(l => l.Length > 0).ToHashSet();
}

static bool ReadsMark(HidDevice device)
{
	try
	{
		using HidConnection connection = WindowsHid.Open(device);
		byte[] buffer = new byte[device.InputLength];
		for (int i = 0; i < 20; i++)
		{
			int length = connection.Read(buffer);
			ReadOnlySpan<byte> body = InputState.Body(buffer.AsSpan(0, length));
			if (!body.IsEmpty && InputState.Parse(body).L2 == 123)
			{
				return true;
			}
		}
	}
	catch (Exception)
	{
		// Not readable: not ours.
	}
	return false;
}

static void ReadForTwoSeconds(HidDevice device)
{
	using HidConnection connection = WindowsHid.Open(device);
	byte[] buffer = new byte[connection.Device.InputLength];
	int reports = 0;
	InputState last = default;
	Stopwatch clock = Stopwatch.StartNew();
	while (clock.Elapsed < TimeSpan.FromSeconds(2))
	{
		int length = connection.Read(buffer);
		ReadOnlySpan<byte> body = InputState.Body(buffer.AsSpan(0, length));
		if (!body.IsEmpty)
		{
			last = InputState.Parse(body);
			reports++;
		}
	}
	Console.WriteLine($"{reports / clock.Elapsed.TotalSeconds:F0} input reports/s, battery {last.BatteryLevel}%, sticks {last.LeftX},{last.LeftY} {last.RightX},{last.RightY}");
}
