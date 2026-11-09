// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Kopru.Core.Bluetooth;
using Kopru.Core.Diagnostics;
using Kopru.Core.Hid;
using Kopru.Core.Protocol;

// Developer probe.
//   Kopru.Probe            lists the DualSense interfaces this program can see and reads input for two seconds
//   Kopru.Probe adapter    Bluetooth adapter check (5 s idle, 5 s with haptics) and its verdict
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
