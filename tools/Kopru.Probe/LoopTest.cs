// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Kopru.Probe;

// `Kopru.Probe looptest [seconds] [render-name] [capture-name]` (app RUNNING, mic switched on): the whole Windows path.
// Plays ToneTest's pattern through WASAPI on the virtual pad's render endpoint (first two channels only) while recording
// the virtual pad's capture endpoint, then measures the tone like speakermic.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class LoopTest
{
	private static readonly string[] DefaultPatterns = ["DualSense", "Wireless Controller"];

	public static int Run(string[] args)
	{
		int seconds = 8;
		List<string> names = [];
		foreach (string a in args)
		{
			if (int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > 1)
			{
				seconds = s;
			}
			else
			{
				names.Add(a);
			}
		}
		Kopru.Probe.CaptureEndpoint? render = Pick("Render", Kopru.Probe.WasapiCapture.ListEndpoints(render: true), names.ElementAtOrDefault(0));
		Kopru.Probe.CaptureEndpoint? capture = Pick("Capture", Kopru.Probe.WasapiCapture.ListEndpoints(render: false), names.ElementAtOrDefault(1));
		if (render == null)
		{
			Console.WriteLine("Verdict: no render endpoint found (the virtual pad's speaker is not a Windows playback device: nothing a game plays can reach the controller)");
			return 2;
		}
		if (capture == null)
		{
			Console.WriteLine("Verdict: no capture endpoint found");
			return 2;
		}
		if (render.Muted == true || render.Volume is <= 0f)
		{
			Console.WriteLine($"NOTE: the render endpoint is {(render.Muted == true ? "MUTED" : "at ZERO volume")} in Windows: that alone makes the speaker silent");
		}
		if (capture.Muted == true || capture.Volume is <= 0f)
		{
			Console.WriteLine($"NOTE: the capture endpoint is {(capture.Muted == true ? "MUTED" : "at ZERO volume")} in Windows: the recording will be silent");
		}

		Console.WriteLine(F($"Playing {ToneTest.ToneHz:F0} Hz at {20 * Math.Log10(ToneTest.Amplitude):F0} dBFS (channels 1-2 only), 1 s on / 1 s off for {seconds} s, recording at the same time..."));
		Kopru.Probe.CaptureResult? recording = null;
		Exception? captureError = null;
		Thread recorder = new(() =>
		{
			try
			{
				recording = Kopru.Probe.WasapiCapture.Record(capture.Index, TimeSpan.FromSeconds(seconds + 2));
			}
			catch (Exception e)
			{
				captureError = e;
			}
		})
		{ IsBackground = true };
		recorder.Start();
		Thread.Sleep(300);
		Kopru.Probe.RenderResult played;
		try
		{
			played = Kopru.Probe.WasapiCapture.Play(render.Index, TimeSpan.FromSeconds(seconds), t => ToneTest.Sample(t, seconds));
		}
		catch (Exception e)
		{
			Console.WriteLine($"Playback failed: {e.GetType().Name}: {e.Message}");
			return 3;
		}
		recorder.Join();
		if (recording == null)
		{
			Console.WriteLine($"Capture failed: {captureError?.GetType().Name}: {captureError?.Message}");
			return 3;
		}
		Kopru.Probe.MixFormat pf = played.Format;
		Console.WriteLine(F($"Render format {pf.Rate} Hz, {pf.Channels} ch, {pf.Bits}-bit {(pf.Float ? "float" : "PCM")}; {played.FramesWritten} frames written, buffer ran empty {played.Underruns} times"));
		Console.WriteLine(F($"Capture format {recording.Rate} Hz, {recording.Channels} ch, {recording.Bits}-bit {(recording.Float ? "float" : "PCM")}; {recording.Frames} frames, packets {recording.Packets}, flagged silent {recording.SilentPackets}"));

		// First channel of the recording; its sample 0 is at the capture start, measured against the playback start.
		float[] mono = new float[recording.Frames];
		for (int i = 0; i < mono.Length; i++)
		{
			mono[i] = recording.Samples[i * recording.Channels];
		}
		double offset = (recording.StartTimestamp - played.StartTimestamp) / (double)Stopwatch.Frequency;
		short[] pcm = mono.Select(v => (short)Math.Clamp(Math.Round(v * 32767.0), short.MinValue, short.MaxValue)).ToArray();
		string wav = Path.Combine(Path.GetTempPath(), $"kopru-looptest-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
		File.WriteAllBytes(wav, Kopru.Core.Audio.WavWriter.Build(pcm, Math.Max(1, recording.Rate), 1));
		Console.WriteLine($"Wrote {wav} (first channel)");
		ToneTest.Result result = ToneTest.Analyze(mono, recording.Rate, offset, seconds);
		return ToneTest.Report(result, seconds, recording.Frames == 0 ? "no frames captured" : "");
	}

	private static Kopru.Probe.CaptureEndpoint? Pick(string kind, IReadOnlyList<Kopru.Probe.CaptureEndpoint> endpoints, string? wanted)
	{
		string[] patterns = wanted != null ? [wanted] : DefaultPatterns;
		Kopru.Probe.CaptureEndpoint? pick = null;
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
		Console.WriteLine($"{kind} endpoints: {endpoints.Count}");
		foreach (Kopru.Probe.CaptureEndpoint e in endpoints)
		{
			string volume = e.Volume is float v ? F($"volume {v * 100:F0} %") : "volume ?";
			string muted = e.Muted is bool m ? (m ? "MUTED" : "not muted") : "mute ?";
			Console.WriteLine($"  {(e == pick ? "*" : " ")} {e.Name} ({volume}, {muted}{(e.IsDefault ? ", default device" : "")})");
		}
		if (pick == null)
		{
			Console.WriteLine($"  No {kind.ToLowerInvariant()} endpoint name contains {string.Join(" or ", patterns.Select(p => $"\"{p}\""))}");
			return null;
		}
		int matches = endpoints.Count(e => e.Name.Contains(matchedBy!, StringComparison.OrdinalIgnoreCase));
		Console.WriteLine($"  Using \"{pick.Name}\": name contains \"{matchedBy}\"{(wanted != null ? " (given)" : "")}" +
			(matches > 1 ? $"; {matches} endpoints match, took the first" : "") + (pick.IsDefault ? "" : $"; NOT the default {kind.ToLowerInvariant()} device"));
		return pick;
	}

	private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
