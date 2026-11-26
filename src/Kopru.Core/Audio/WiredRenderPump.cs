// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;

namespace Kopru.Core.Audio;

// The calls the wired render stream makes on an initialized audio client (WASAPI IAudioClient + IAudioRenderClient +
// IAudioClock); a seam so the pump is tested without Windows audio. Methods return HRESULTs like the COM calls.
internal interface IRenderStream
{
	int GetCurrentPadding(out uint frames);

	int GetBuffer(uint frames, out IntPtr data);

	int ReleaseBuffer(uint frames);

	int Start();

	int Stop();

	int Reset();

	// Seconds the device has played since the last Start/Reset (IAudioClock position / frequency); null when the
	// endpoint has no clock.
	double? PlayedSeconds();
}

// The wired stream started and was fed for StallCheckFrames, yet the device clock never moved: nothing reaches the
// controller. The session drops the sink and haptics go back to Bluetooth.
internal sealed class WiredAudioStalledException(string message) : IOException(message);

// Feeds one wired render stream: a haptics block in, a block out (with the speaker), a silence cushion before each
// start, padding steering, a whole-block drop when the endpoint falls behind, the peaks log, and the stall check:
// once StallCheckFrames were written since Start the device clock must have moved, else WriteHaptics throws
// WiredAudioStalledException. Not thread-safe: the sink serializes calls.
// Fix 8 (crackle while vibrating): the queue read before a block jumps by a whole engine period (shared mode reads
// 480 frames every 10 ms while blocks come every 10.67 ms), so most blocks fall outside the steering deadband. The
// old nudge dropped or repeated a block's last frames: a small step or flat spot in the haptics on most blocks.
// Smooth steering (the default) interpolates the block onto the nudged length instead. A gap (the queue ran dry: a
// silence cushion is queued) fades the next block in instead of jumping back to the old level, and a dropped block is
// skipped before it is built, so the waveform continues from what was really queued. WiredAudioTuning has the
// switches; the gap, nudge, late and low-queue counts go into the log lines.
internal sealed class WiredRenderPump(IRenderStream stream, int channels, WiredSampleFormat sample, Func<string> describeMode,
	Action<string>? log = null, WiredAudioTuning? tuning = null, Func<double>? clock = null)
{
	public const int CushionFrames = 960;         // 20 ms of silence queued before the first block (by default)
	public const int MaxQueuedFrames = 2880;      // 60 ms: more queued means the endpoint is not keeping up (by default)
	public const int StallCheckFrames = WiredAudioFrames.Rate; // 1 s written since Start

	// A block written more than this after one block's length since the previous one counts as late.
	public const double LateSlackSeconds = 0.002;

	// A queue below one block's length before a write counts as low (close to a gap).
	public const int LowQueueFrames = 512;

	private readonly WiredAudioTuning tuning = tuning ?? WiredAudioTuning.Default;
	private readonly WiredAudioFrames frames = new(tuning ?? WiredAudioTuning.Default);
	private readonly WiredAudioPeaks peaks = new();
	private readonly Action<string> log = log ?? (line => _ = Task.Run(() => Diagnostics.AppLog.Write(line)));
	private readonly Func<double> clock = clock ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
	private float[] mix = [];
	private bool started;
	private bool stallChecked;
	private long writtenSinceStart;
	private uint lastPadding;
	private long droppedBlocks;
	private long gaps, nudged;
	private double lastWriteAt;
	private WriterEvents second, window;
	private double secondStarted = double.NaN;

	// What happened on the writer in one stretch (a second for the event log, the peaks window for its line).
	private struct WriterEvents
	{
		public int Blocks, Gaps, Low, NudgedUp, NudgedDown, FramesUp, FramesDown, Late, Dropped;
		public double WorstBetween;
		public uint MinQueue, MaxQueue;
		public bool AnyQueue;

		public void Queue(uint padding)
		{
			MinQueue = AnyQueue ? Math.Min(MinQueue, padding) : padding;
			MaxQueue = AnyQueue ? Math.Max(MaxQueue, padding) : padding;
			AnyQueue = true;
		}
	}

	public WiredAudioFrames Frames => frames;

	public WiredAudioTuning Tuning => tuning;

	public bool Started => started;

	public long DroppedBlocks => Interlocked.Read(ref droppedBlocks);

	// Silence cushions queued because the endpoint ran dry while playing (diagnostics, tests).
	public long Gaps => Interlocked.Read(ref gaps);

	// Blocks written a few frames longer or shorter to steer the queue (diagnostics, tests).
	public long Nudged => Interlocked.Read(ref nudged);

	// Throws (COMException, WiredAudioStalledException) when the endpoint is gone or not playing.
	public void WriteHaptics(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right)
	{
		int blockFrames = left.Length * WiredAudioFrames.Upsample;
		int need = (blockFrames + WiredAudioSteering.MaxFramesPerBlock) * channels;
		if (mix.Length < need)
		{
			mix = new float[need];
		}
		double now = clock();
		Marshal.ThrowExceptionForHR(stream.GetCurrentPadding(out uint padding));
		lastPadding = padding;
		second.Blocks++;
		window.Blocks++;
		if (started)
		{
			double between = now - lastWriteAt;
			bool late = between > blockFrames / (double)WiredAudioFrames.Rate + LateSlackSeconds;
			Observe(ref second, padding, late, between);
			Observe(ref window, padding, late, between);
		}
		lastWriteAt = now;
		if (!started || padding == 0)
		{
			// Not playing yet (first block, or a just restarted stream) or a gap (the queue ran dry while playing):
			// a short silence cushion first, and the next block fades in from it.
			Queue(tuning.CushionFrames, null);
			if (started)
			{
				gaps++;
				second.Gaps++;
				window.Gaps++;
				frames.FadeIn(tuning.GapFadeFrames);
			}
			else
			{
				frames.FadeIn(tuning.StartFadeFrames);
			}
		}
		if (padding > tuning.MaxQueuedFrames)
		{
			if (tuning.Steering == WiredSteering.Legacy)
			{
				// As before fix 8: the block was built (its ramp state moved on) and then not queued.
				frames.Build(left, right, mix, channels);
			}
			// A dropped block still counts as fed for the stall check: a dead stream fills up and drops every block.
			droppedBlocks++;
			second.Dropped++;
			window.Dropped++;
			writtenSinceStart += blockFrames;
			Tick(now);
			CheckStall();
			return;
		}
		// The block is nudged a few frames so the queue drifts back to the target; the silence cushion above and the
		// whole-block drop for MaxQueuedFrames stay as the last resorts.
		int adjust = WiredAudioSteering.Adjust((int)padding, tuning.TargetFrames);
		int count;
		if (tuning.Steering == WiredSteering.Legacy)
		{
			count = frames.Build(left, right, mix, channels);
			count = WiredAudioSteering.Apply(mix, count, channels, adjust);
		}
		else
		{
			count = frames.Build(left, right, mix, channels, blockFrames + adjust);
		}
		if (adjust != 0)
		{
			nudged++;
			Nudge(ref second, adjust);
			Nudge(ref window, adjust);
		}
		Queue(count, mix);
		writtenSinceStart += count;
		// Only what was really queued counts (the cushion above is silence; a dropped block never arrived).
		peaks.Add(mix, count, channels);
		if (peaks.TryTake(false, out float hapticsPeak, out float speakerPeak, out int windowFrames))
		{
			LogPeaks(windowFrames, hapticsPeak, speakerPeak, stream.PlayedSeconds());
		}
		if (!started)
		{
			Marshal.ThrowExceptionForHR(stream.Start());
			started = true;
			stallChecked = false;
			writtenSinceStart = count;
		}
		Tick(now);
		CheckStall();
	}

	private static void Observe(ref WriterEvents e, uint padding, bool late, double between)
	{
		e.Queue(padding);
		if (padding < LowQueueFrames)
		{
			e.Low++;
		}
		if (late)
		{
			e.Late++;
			e.WorstBetween = Math.Max(e.WorstBetween, between);
		}
	}

	private static void Nudge(ref WriterEvents e, int adjust)
	{
		if (adjust > 0)
		{
			e.NudgedUp++;
			e.FramesUp += adjust;
		}
		else
		{
			e.NudgedDown++;
			e.FramesDown -= adjust;
		}
	}

	// The event log's line once a second of stream has passed (KOPRU_WIRED_AUDIO_LOG).
	private void Tick(double now)
	{
		if (!tuning.EventLog)
		{
			return;
		}
		if (double.IsNaN(secondStarted))
		{
			secondStarted = now;
			return;
		}
		if (now - secondStarted >= 1)
		{
			LogSecond(now - secondStarted);
			secondStarted = now;
		}
	}

	private void LogSecond(double seconds)
	{
		WriterEvents e = second;
		second = default;
		string queue = e.AnyQueue ? string.Create(CultureInfo.InvariantCulture, $"{e.MinQueue}..{e.MaxQueue}") : "-";
		log(string.Create(CultureInfo.InvariantCulture,
			$"wired: usb audio events {seconds:0.0} s: {e.Blocks} blocks, {e.Gaps} gaps, {e.Low} low, {e.NudgedUp + e.NudgedDown} nudged" +
			$" (+{e.FramesUp}/-{e.FramesDown} frames), {e.Dropped} dropped, {e.Late} late (worst {e.WorstBetween * 1000:0.0} ms between blocks)," +
			$" queue {queue} frames"));
	}

	// The stream stopped: the device clock is read first (a Reset zeroes it), then the client is stopped and reset so
	// the endpoint may sleep until the next block. Never throws.
	public void Idle()
	{
		try
		{
			double? played = started ? stream.PlayedSeconds() : null;
			stream.Stop();
			stream.Reset();
			started = false;
			// Nothing left over plays when the stream starts again.
			frames.Reset();
			if (peaks.TryTake(true, out float hapticsPeak, out float speakerPeak, out int windowFrames))
			{
				LogPeaks(windowFrames, hapticsPeak, speakerPeak, played);
			}
			if (tuning.EventLog && second.Blocks > 0)
			{
				LogSecond(double.IsNaN(secondStarted) ? 0 : clock() - secondStarted);
			}
			secondStarted = double.NaN;
		}
		catch (Exception)
		{
			// Already gone (device removed); the next write makes it fail anyway.
		}
	}

	private void CheckStall()
	{
		if (!started || stallChecked || writtenSinceStart < StallCheckFrames)
		{
			return;
		}
		stallChecked = true;
		if (stream.PlayedSeconds() is { } played && played <= 0)
		{
			throw new WiredAudioStalledException(string.Create(CultureInfo.InvariantCulture,
				$"device clock still 0 after {writtenSinceStart / (double)WiredAudioFrames.Rate:0.0} s written, queue {lastPadding} frames"));
		}
	}

	// count frames of `data` (null = silence) into the endpoint buffer.
	private unsafe void Queue(int count, float[]? data)
	{
		Marshal.ThrowExceptionForHR(stream.GetBuffer((uint)count, out IntPtr buffer));
		try
		{
			int samples = count * channels;
			Span<byte> target = new((void*)buffer, samples * WiredAudioSamples.BytesPerSample(sample));
			if (data == null)
			{
				target.Clear();
			}
			else
			{
				WiredAudioSamples.Write(data.AsSpan(0, samples), target, sample);
			}
		}
		finally
		{
			Marshal.ThrowExceptionForHR(stream.ReleaseBuffer((uint)count));
		}
	}

	// What the last window of queued audio held, with the queue and the device clock (read while the stream ran).
	// The gap, nudge and late counts cover the same window (fix 8).
	private void LogPeaks(int framesWritten, float haptics, float speaker, double? played)
	{
		WriterEvents e = window;
		window = default;
		log(string.Create(CultureInfo.InvariantCulture,
			$"wired: usb audio {framesWritten / (double)WiredAudioFrames.Rate:0.0} s written:" +
			$" peak ch3-4 (haptics) {haptics:0.000}, peak ch1-2 (speaker) {speaker:0.000}," +
			$" {DroppedBlocks} blocks dropped, {e.Gaps} gaps, {e.NudgedUp + e.NudgedDown} nudged of {e.Blocks} blocks," +
			$" {e.Late} late (worst {e.WorstBetween * 1000:0.0} ms), queue {lastPadding} frames," +
			$" device played {(played is { } p ? p.ToString("0.00", CultureInfo.InvariantCulture) + " s" : "unknown")} ({describeMode()})"));
	}
}
