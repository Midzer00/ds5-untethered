// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Diagnostics;
using Kopru.Core.Platform;
using Kopru.Core.UsbIp;
using Kopru.Core.Virtual;
using Kopru.Platform.Linux;
using Kopru.Platform.Linux.Diagnostics;
using Kopru.Platform.Linux.Hidraw;
using Kopru.Platform.Linux.UsbIp;

namespace Kopru.LinuxSpike;

// Runs the shared bridge (BridgeHost: Bluetooth session, virtual USB DualSense, USB/IP server) with the Linux platform
// and logs what the three test questions need: does the virtual pad's USB audio arrive steadily through vhci_hcd, does a
// game send haptics and trigger effects to it, and what the real pad looks like to the system meanwhile.
[SupportedOSPlatform("linux")]
internal sealed class Spike(SpikeOptions options, SpikeLog log)
{
	private static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(5);

	// The virtual DualSense's USB audio: 4 channels of 16-bit samples at 48 kHz.
	private static readonly int AudioBytesPerMs = VirtualDualSense.AudioChannels * (VirtualDualSense.AudioRate / 1000) * 2;

	private readonly IsoAudioMeter audio = new(AudioBytesPerMs);

	private readonly object outputGate = new();

	private OutputReportInfo? lastTriggers;

	private long outputReports, triggerReports, windowOutputs, windowTriggers, featureWrites, imports, releases;

	private int outputLinesThisSecond, dropoutLinesThisSecond;

	private long suppressedOutputLines, suppressedDropoutLines;

	private HidrawLink? link;

	private GameSignals windowSignals;

	private static double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

	public async Task<int> RunAsync()
	{
		LinuxPlatform platform = new(new LinuxPlatformOptions
		{
			AttachMode = options.AttachMode,
			TrimOutputReports = !options.NoTrim,
			Notice = line => log.Write("ACTION NEEDED: " + line)
		});
		PlatformServices.Current = platform;
		// The shared bridge also keeps its own rolling log (app.log, not masked): next to the --log file, else in the
		// current folder, never in the app's data folder (under sudo that would be root's home).
		AppLog.Directory = options.LogFile != null ? Path.GetDirectoryName(Path.GetFullPath(options.LogFile))! : Environment.CurrentDirectory;
		AppLog.Written += line => log.Write("bridge: " + line);
		log.Write("logs: this log " + (log.FilePath ?? "on the console only (use --log FILE)") + "; the bridge's own log (not masked) " +
			Path.Combine(AppLog.Directory, AppLog.FileName));

		WriteSystemReport(platform);
		IReadOnlyList<HidrawNode> before = platform.CreateFinder().FindNodes();
		IReadOnlyList<string> cardsBefore = SoundCards();
		if (options.ListOnly)
		{
			return 0;
		}
		if (!before.Any(n => n.IsBluetooth))
		{
			log.Write("no Bluetooth DualSense yet: pair and connect it (bluetoothctl), the spike waits for it");
		}

		platform.LinkOpened += opened =>
		{
			link = opened;
			log.Write($"controller: opened {opened.Node.DevicePath} (output reports {(opened.TrimOutput ? "cut to the declared length" : "at full length")})");
		};
		await using BridgeHost host = BridgeHost.Create(platform);
		host.IdleMinutes = 0;
		host.StateChanged += state =>
		{
			log.Write("state: " + state + (state == BridgeState.Error ? $" ({host.Status.LastError}: {host.Status.LastErrorDetail})" : ""));
			if (state == BridgeState.Running)
			{
				_ = Task.Run(async () =>
				{
					await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
					ReportVirtualPad(platform, before, cardsBefore);
				});
			}
		};
		host.PadServing += (pad, server) => Watch(platform, host, pad, server);
		host.GameUseStarted += use => log.Write("game: started using the controller: " + use);

		using CancellationTokenSource stop = new();
		ConsoleCancelEventHandler onCancel = (_, e) =>
		{
			e.Cancel = true;
			stop.Cancel();
		};
		Console.CancelKeyPress += onCancel;
		using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
		{
			context.Cancel = true;
			stop.Cancel();
		});
		if (options.Seconds is int seconds)
		{
			stop.CancelAfter(TimeSpan.FromSeconds(seconds));
		}

		log.Write("bridge: starting; Ctrl+C stops (the virtual pad is unplugged first)");
		await host.StartAsync("linux spike").ConfigureAwait(false);
		DateTimeOffset nextSummary = DateTimeOffset.Now + SummaryInterval;
		try
		{
			while (!stop.IsCancellationRequested)
			{
				await Task.Delay(TimeSpan.FromSeconds(1), stop.Token).ConfigureAwait(false);
				EverySecond(host);
				if (DateTimeOffset.Now >= nextSummary)
				{
					nextSummary = DateTimeOffset.Now + SummaryInterval;
					Summary(host);
				}
			}
		}
		catch (OperationCanceledException)
		{
			// Stopping.
		}
		finally
		{
			Console.CancelKeyPress -= onCancel;
		}
		log.Write("bridge: stopping");
		await host.StopAsync("linux spike exit").ConfigureAwait(false);
		Final(host);
		return 0;
	}

	private void WriteSystemReport(LinuxPlatform platform)
	{
		string version = typeof(BridgeHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
		log.Write($"DS5 Untethered Linux spike {version} (EXPERIMENTAL), .NET {Environment.Version}, {RuntimeInformation.OSArchitecture}");
		log.Write("system: " + (OsRelease() ?? "unknown distribution") + ", kernel " + (ReadLine("/proc/sys/kernel/osrelease") ?? "?"));
		log.Write("system: running as " + (RealLinuxHost.Instance.IsRoot ? "root" : "a normal user") + ", attach mode " + options.AttachMode);
		string? usbip = RealLinuxHost.Instance.FindExecutable("usbip");
		log.Write("usbip: client " + (usbip ?? "NOT FOUND (install your distribution's usbip tools)"));
		bool vhci = UsbIpLinuxCommands.VhciDirectories.Any(Directory.Exists);
		log.Write("usbip: vhci_hcd " + (vhci ? "loaded" : "NOT loaded (sudo modprobe vhci-hcd)"));
		log.Write("timing: " + PreciseWait.Name(platform.CreatePreciseWait()) + " wait");
		IReadOnlyList<HidrawNode> nodes = platform.CreateFinder().FindNodes();
		if (nodes.Count == 0)
		{
			log.Write("hidraw: no DualSense found in " + HidrawFinder.SysClassHidraw);
		}
		foreach (HidrawNode node in nodes)
		{
			log.Write($"hidraw: {Describe(node)}, access {Access(node.DevicePath)}");
		}
		foreach (string card in SoundCards())
		{
			log.Write("sound: " + card);
		}
	}

	private static string Describe(HidrawNode node)
	{
		string lengths = node.Descriptor is { } d
			? $"in {d.MaxLength(HidReportKind.Input)} / out {d.MaxLength(HidReportKind.Output)} / feature {d.MaxLength(HidReportKind.Feature)} bytes"
			: "no report descriptor";
		return string.Create(CultureInfo.InvariantCulture,
			$"{node.DevicePath} {node.Transport?.ToString() ?? "bus 0x" + node.Id.Bus.ToString("X2", CultureInfo.InvariantCulture)} " +
			$"{node.Id.VendorId:X4}:{node.Id.ProductId:X4} \"{node.Uevent.Name}\" driver {node.Uevent.Driver ?? "none"}, {lengths}");
	}

	private static string Access(string path)
	{
		try
		{
			using FileStream probe = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 0);
			return "read/write ok";
		}
		catch (UnauthorizedAccessException)
		{
			return "DENIED (needs a udev rule, or run the spike as root)";
		}
		catch (IOException e)
		{
			return "error: " + e.Message;
		}
	}

	private void Watch(LinuxPlatform platform, BridgeHost host, IVirtualPad pad, UsbIpServer server)
	{
		log.Write($"virtual pad: {pad.Type} served on 127.0.0.1:{server.Port}, bus id {host.BusId}");
		server.Imported += () =>
		{
			Interlocked.Increment(ref imports);
			log.Write("virtual pad: the usbip client imported it (vhci_hcd)");
			platform.NotifyImported();
		};
		server.Released += () =>
		{
			Interlocked.Increment(ref releases);
			log.Write("virtual pad: the usbip client released it");
		};
		if (pad is VirtualDualSense dualSense)
		{
			dualSense.OutputReceived += OnOutput;
			dualSense.AudioReceived += data => OnAudio(data.Length);
			dualSense.FeatureWritten += (id, _) =>
			{
				Interlocked.Increment(ref featureWrites);
				log.Write($"game: feature report 0x{id:X2} written");
			};
		}
		else
		{
			log.Write($"virtual pad: {pad.Type} is not counted by the spike (only the DualSense is)");
		}
	}

	private void OnOutput(byte[] report)
	{
		Interlocked.Increment(ref outputReports);
		Interlocked.Increment(ref windowOutputs);
		if (OutputReportInfo.Parse(report) is not { } info)
		{
			return;
		}
		if (info.AnyTrigger)
		{
			Interlocked.Increment(ref triggerReports);
			Interlocked.Increment(ref windowTriggers);
		}
		lock (outputGate)
		{
			bool changed = lastTriggers is not { } last || last.RightTrigger != info.RightTrigger || last.RightMode != info.RightMode ||
				last.LeftTrigger != info.LeftTrigger || last.LeftMode != info.LeftMode || last.Rumble != info.Rumble;
			lastTriggers = info;
			if (!changed)
			{
				return;
			}
			if (outputLinesThisSecond++ < 5)
			{
				log.Write("game: output report: " + info.Describe());
			}
			else
			{
				suppressedOutputLines++;
			}
		}
	}

	private void OnAudio(int length)
	{
		if (audio.Record(NowMs, length) is { } gap)
		{
			bool show;
			lock (outputGate)
			{
				show = dropoutLinesThisSecond++ < 5;
				if (!show)
				{
					suppressedDropoutLines++;
				}
			}
			if (show)
			{
				log.Write(string.Create(CultureInfo.InvariantCulture, $"audio: DROPOUT, no audio transfer for {gap:F1} ms"));
			}
		}
	}

	private void EverySecond(BridgeHost host)
	{
		GameSignals signals = host.TakeGameSignals();
		windowSignals = new GameSignals(windowSignals.Haptics || signals.Haptics, windowSignals.Triggers || signals.Triggers,
			windowSignals.Rumble || signals.Rumble, windowSignals.TriggerRumble || signals.TriggerRumble);
		IsoAudioWindow window = audio.TakeWindow(NowMs);
		long outputs = Interlocked.Exchange(ref windowOutputs, 0);
		long triggers = Interlocked.Exchange(ref windowTriggers, 0);
		long hiddenOutput, hiddenDropouts;
		lock (outputGate)
		{
			outputLinesThisSecond = dropoutLinesThisSecond = 0;
			hiddenOutput = suppressedOutputLines;
			hiddenDropouts = suppressedDropoutLines;
			suppressedOutputLines = suppressedDropoutLines = 0;
		}
		if (!window.Active && outputs == 0)
		{
			return;
		}
		BridgeStatus status = host.Status;
		log.Write(string.Create(CultureInfo.InvariantCulture,
			$"1s: audio {window.TransfersPerSecond:F0} transfers/s, {window.AudioMsPerSecond:F0} ms of audio/s, max gap {window.MaxGapMs:F1} ms, " +
			$"dropouts {window.Dropouts}, stream starts {window.Starts}; game output reports {outputs} ({triggers} with trigger effects); " +
			$"Bluetooth haptics reports {status.HapticsReportsPerSecond:F1}/s; input {status.InputRateHz:F0} Hz") +
			(hiddenOutput + hiddenDropouts > 0 ? $" ({hiddenOutput} output and {hiddenDropouts} dropout lines not shown)" : ""));
	}

	private void Summary(BridgeHost host)
	{
		BridgeStatus status = host.Status;
		GameSignals signals = windowSignals;
		windowSignals = default;
		HidrawLink? l = link;
		string writes = l != null ? $"; controller writes {l.Writes} ({l.WriteFailures} failed, {l.TrimmedWrites} cut)" : "";
		string quality = status.Link is { } q
			? string.Create(CultureInfo.InvariantCulture, $"; link {q.State}, worst delay {q.WorstDelayMs:F1} ms, lost {q.LostPercent:F1} %")
			: "";
		log.Write(string.Create(CultureInfo.InvariantCulture,
			$"status: {status.State}, input {status.InputRateHz:F0} Hz, battery {status.Battery?.Percent.ToString(CultureInfo.InvariantCulture) ?? "?"} %" +
			$"; game sent haptics {Yes(signals.Haptics)}, triggers {Yes(signals.Triggers)}, rumble {Yes(signals.Rumble)} (last {SummaryInterval.TotalSeconds:F0} s)") +
			writes + quality);
	}

	private void Final(BridgeHost host)
	{
		log.Write(string.Create(CultureInfo.InvariantCulture,
			$"total: imports {imports}, releases {releases}; game output reports {outputReports} ({triggerReports} with trigger effects), " +
			$"feature writes {featureWrites}; audio transfers {audio.TotalTransfers}, {audio.TotalBytes / (double)AudioBytesPerMs / 1000:F1} s of audio, " +
			$"dropouts {audio.TotalDropouts} (gaps over {audio.DropoutGapMs:F0} ms), stream starts {audio.TotalStarts}; last error {host.Status.LastError}"));
		if (link is { } l)
		{
			log.Write($"total: controller writes {l.Writes}, failed {l.WriteFailures}, cut to the declared length {l.TrimmedWrites}");
		}
		if (log.FilePath != null)
		{
			log.Write("log written to " + log.FilePath);
		}
	}

	private void ReportVirtualPad(LinuxPlatform platform, IReadOnlyList<HidrawNode> before, IReadOnlyList<string> cardsBefore)
	{
		HashSet<string> known = [.. before.Select(n => n.Name)];
		List<HidrawNode> added = [.. platform.CreateFinder().FindNodes().Where(n => !known.Contains(n.Name))];
		if (added.Count == 0)
		{
			log.Write("virtual pad: no new DualSense hidraw node yet (is it attached? see \"usbip port\" and dmesg)");
		}
		foreach (HidrawNode node in added)
		{
			log.Write($"virtual pad: appeared as {Describe(node)}");
		}
		foreach (string card in SoundCards().Except(cardsBefore))
		{
			log.Write("virtual pad: new sound card: " + card);
		}
	}

	// The DualSense lines of /proc/asound/cards ("N [Controller ]: USB-Audio - DualSense Wireless Controller").
	private static IReadOnlyList<string> SoundCards()
	{
		try
		{
			return [.. File.ReadAllLines("/proc/asound/cards").Select(l => l.Trim()).Where(l => l.Contains("DualSense", StringComparison.OrdinalIgnoreCase))];
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	private static string? OsRelease()
	{
		try
		{
			string? line = File.ReadLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
			return line?["PRETTY_NAME=".Length..].Trim('"');
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static string? ReadLine(string path)
	{
		try
		{
			return File.ReadLines(path).FirstOrDefault()?.Trim();
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static string Yes(bool value) => value ? "yes" : "no";
}
