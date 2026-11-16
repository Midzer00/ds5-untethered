// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Kopru.Core.Games;

namespace Kopru.Core.Audio;

// "Turn sound into haptics": while the active game's profile has it on, captures that game's sound (IAudioCaptureFactory),
// turns it into haptics frames (AudioHapticsDsp) and hands them to the controller (IAudioHapticsOutput), where they are
// mixed like the game's own haptics. Update is called with the active game and its profile's settings whenever either may
// have changed (each game tick and each settings change); it starts the capture when the game is active and the option on,
// and stops it when the game ends, another one takes over or the option goes off. Thread-safe; the audio path
// (OnAudio, on the capture thread) allocates nothing.
public sealed class AudioHapticsRunner : IAudioCaptureSink, IThreadPoolWorkItem, IDisposable
{
	// Output frames processed per step (the audio path never grows a buffer: longer blocks go in steps).
	public const int BlockFrames = 1024;

	private readonly object gate = new();
	private readonly IAudioCaptureFactory factory;
	private readonly IAudioHapticsOutput output;
	private readonly Action<string> log;
	private readonly AudioHapticsDsp dsp = new();
	private readonly sbyte[] left = new sbyte[BlockFrames];
	private readonly sbyte[] right = new sbyte[BlockFrames];

	private IAudioCapture? capture;
	private ForegroundGame? game;
	private AudioHapticsSettings settings = AudioHapticsSettings.Default;
	private AudioHapticsStatus status = AudioHapticsStatus.Off;
	private bool disposed;

	// The game process a start failed for (no retry until the game or the switch changes).
	private int failedPid;

	// The current capture run (written on the capture thread, read under gate when it ends).
	private long frames, pausedFrames;
	private int peak;
	private long startedAt;
	private volatile bool paused;
	private int pauseLogged;

	public AudioHapticsRunner(IAudioCaptureFactory factory, IAudioHapticsOutput output, Action<string>? log = null)
	{
		this.factory = factory;
		this.output = output;
		this.log = log ?? Diagnostics.AppLog.Write;
	}

	public AudioHapticsStatus Status
	{
		get
		{
			lock (gate)
			{
				return status;
			}
		}
	}

	// False after this Windows could not capture the game's sound (no process loopback and no usable system loopback).
	public bool Available => Status.Kind != AudioHapticsStatusKind.Unavailable;

	public bool IsCapturing
	{
		get
		{
			lock (gate)
			{
				return capture != null;
			}
		}
	}

	// Raised (on the calling thread or a pool thread) when Status changed.
	public event Action<AudioHapticsStatus>? StatusChanged;

	// The active game (null: none) and its profile's audio haptics settings.
	public void Update(ForegroundGame? activeGame, AudioHapticsSettings value)
	{
		AudioHapticsStatus? changed;
		lock (gate)
		{
			if (disposed)
			{
				return;
			}
			dsp.Band = value.Band;
			dsp.Strength = value.Strength;
			bool wasOn = settings.On;
			settings = value;
			int pid = activeGame?.ProcessId ?? 0;
			if (capture != null)
			{
				string? reason = !value.On ? "turned off"
					: activeGame == null ? "the game ended"
					: game == null || game.ProcessId != pid ? $"{activeGame.Exe} took over"
					: null;
				if (reason != null)
				{
					StopLocked(reason);
				}
			}
			if (!value.On || !wasOn || pid != failedPid)
			{
				failedPid = 0;
			}
			game = activeGame;
			if (capture == null && value.On && activeGame != null && pid > 0 && failedPid != pid)
			{
				StartLocked(activeGame);
			}
			changed = SetStatusLocked();
		}
		Raise(changed);
	}

	public void Dispose()
	{
		lock (gate)
		{
			if (disposed)
			{
				return;
			}
			StopLocked("app closing");
			disposed = true;
		}
	}

	// The capture thread: sound into frames into the controller. Allocates nothing.
	public void OnAudio(ReadOnlySpan<float> samples, int channels, int sampleRate)
	{
		if (channels < 1 || sampleRate < AudioHapticsDsp.OutputRate)
		{
			return;
		}
		// Input frames per step so the output always fits BlockFrames.
		int step = Math.Max(1, (int)((long)(BlockFrames - 2) * sampleRate / AudioHapticsDsp.OutputRate)) * channels;
		while (!samples.IsEmpty)
		{
			ReadOnlySpan<float> part = samples[..Math.Min(step, samples.Length - samples.Length % channels)];
			if (part.IsEmpty)
			{
				return;
			}
			int n = dsp.Process(part, channels, sampleRate, left, right);
			samples = samples[part.Length..];
			if (n == 0)
			{
				continue;
			}
			AudioHapticsPush result = output.PushAudioHaptics(left.AsSpan(0, n), right.AsSpan(0, n));
			if (result == AudioHapticsPush.NativeHaptics)
			{
				pausedFrames += n;
				if (!paused)
				{
					paused = true;
					if (Interlocked.Exchange(ref pauseLogged, 1) == 0)
					{
						log("audio haptics: paused, the game sends its own haptics");
					}
					ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
				}
				continue;
			}
			if (paused)
			{
				paused = false;
				ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
			}
			if (result == AudioHapticsPush.Played)
			{
				frames += n;
				for (int i = 0; i < n; i++)
				{
					int a = Math.Max(Math.Abs((int)left[i]), Math.Abs((int)right[i]));
					if (a > peak)
					{
						peak = a;
					}
				}
			}
		}
	}

	// Caller holds gate.
	private void StartLocked(ForegroundGame target)
	{
		dsp.Reset();
		frames = pausedFrames = 0;
		peak = 0;
		paused = false;
		pauseLogged = 0;
		IAudioCapture? started;
		try
		{
			started = factory.Start(target.ProcessId, this, log);
		}
		catch (Exception e)
		{
			log($"audio haptics: capture failed ({e.GetType().Name}: {e.Message})");
			started = null;
		}
		if (started == null)
		{
			failedPid = target.ProcessId;
			log($"audio haptics: not available for {target.Exe} (this Windows cannot capture its sound)");
			return;
		}
		capture = started;
		startedAt = Stopwatch.GetTimestamp();
		log(string.Create(CultureInfo.InvariantCulture,
			$"audio haptics: listening to {target.Exe} ({ModeName(started.Mode)}), band {BandName(settings.Band)}, strength {Math.Round(settings.Strength * 100):0} %"));
	}

	// Caller holds gate.
	private void StopLocked(string reason)
	{
		IAudioCapture? running = capture;
		if (running == null)
		{
			return;
		}
		capture = null;
		try
		{
			running.Dispose();
		}
		catch (Exception)
		{
			// Stopping never fails the caller.
		}
		TimeSpan active = Stopwatch.GetElapsedTime(startedAt);
		log($"audio haptics: stopped ({reason})");
		log(string.Create(CultureInfo.InvariantCulture,
			$"audio haptics: session summary: {frames} frames played ({frames / (double)AudioHapticsDsp.OutputRate:F1} s), {pausedFrames} paused for native haptics, peak {Math.Min(peak, 127) * 100 / 127} %, active {active.TotalSeconds:F0} s"));
		paused = false;
	}

	// A pause or resume seen on the capture thread: the status is worked out on a pool thread (no allocation there).
	void IThreadPoolWorkItem.Execute() => RefreshStatus();

	private void RefreshStatus()
	{
		AudioHapticsStatus? changed;
		lock (gate)
		{
			changed = SetStatusLocked();
		}
		Raise(changed);
	}

	// Caller holds gate; the new status when it changed.
	private AudioHapticsStatus? SetStatusLocked()
	{
		AudioHapticsStatus next =
			!settings.On ? AudioHapticsStatus.Off
			: capture != null ? new AudioHapticsStatus(paused ? AudioHapticsStatusKind.NativeHaptics : AudioHapticsStatusKind.Listening, game?.Name, capture.Mode)
			: game != null && failedPid == game.ProcessId ? new AudioHapticsStatus(AudioHapticsStatusKind.Unavailable, game.Name)
			: new AudioHapticsStatus(AudioHapticsStatusKind.WaitingForGame);
		if (next == status)
		{
			return null;
		}
		status = next;
		return next;
	}

	private void Raise(AudioHapticsStatus? changed)
	{
		if (changed == null)
		{
			return;
		}
		try
		{
			StatusChanged?.Invoke(changed);
		}
		catch (Exception)
		{
			// A handler failing never breaks the capture.
		}
	}

	public static string ModeName(AudioCaptureMode mode) => mode == AudioCaptureMode.ProcessLoopback ? "process loopback" : "system loopback";

	public static string BandName(AudioHapticsBand band) => band == AudioHapticsBand.Full ? "full" : "bass";
}
