// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;
using Kopru.Core.Audio;
using Kopru.Core.Bluetooth;
using Kopru.Core.Bridge;
using Kopru.Core.Games;
using Kopru.Core.Profiles;
using Kopru.Core.Protocol;
using Kopru.Core.Virtual;

namespace Kopru.Core.Tests;

// The game's sound turned into haptics: the DSP on synthetic signals, the runner's capture lifecycle with a fake capture,
// and the settings.
public class AudioHapticsDspTests
{
	public const int Rate = 48000;

	// Interleaved stereo float at 48 kHz: left and right as functions of the frame index.
	internal static float[] Stereo(int frames, Func<int, double>? left = null, Func<int, double>? right = null)
	{
		float[] s = new float[frames * 2];
		for (int f = 0; f < frames; f++)
		{
			s[2 * f] = (float)(left?.Invoke(f) ?? 0);
			s[2 * f + 1] = (float)(right?.Invoke(f) ?? 0);
		}
		return s;
	}

	internal static Func<int, double> Tone(double hz, double amplitude, int rate = Rate) => f => amplitude * Math.Sin(2 * Math.PI * hz * f / rate);

	// Runs the DSP in 10 ms blocks, as a capture delivers them.
	internal static List<(sbyte L, sbyte R)> Run(AudioHapticsDsp dsp, float[] samples, int channels = 2, int rate = Rate)
	{
		List<(sbyte, sbyte)> frames = [];
		int block = rate / 100 * channels;
		sbyte[] l = new sbyte[AudioHapticsDsp.MaxFrames(rate / 100, rate)];
		sbyte[] r = new sbyte[l.Length];
		for (int i = 0; i < samples.Length; i += block)
		{
			int n = dsp.Process(samples.AsSpan(i, Math.Min(block, samples.Length - i)), channels, rate, l, r);
			for (int k = 0; k < n; k++)
			{
				frames.Add((l[k], r[k]));
			}
		}
		return frames;
	}

	private static int Peak(IEnumerable<(sbyte L, sbyte R)> frames) => frames.Select(f => Math.Max(Math.Abs((int)f.L), Math.Abs((int)f.R))).DefaultIfEmpty(0).Max();

	[Fact]
	public void A60HzBurst_ProducesHaptics_At3kHz()
	{
		AudioHapticsDsp dsp = new();
		List<(sbyte L, sbyte R)> frames = Run(dsp, Stereo(Rate / 2, Tone(60, 0.3), Tone(60, 0.3)));

		Assert.InRange(frames.Count, 1499, 1501);
		Assert.True(Peak(frames.Skip(300)) > 40, $"peak {Peak(frames)}");
	}

	[Fact]
	public void A2kHzTone_ThroughBass_GivesAlmostNothing()
	{
		AudioHapticsDsp dsp = new() { Band = AudioHapticsBand.Bass };
		// Faded in over 20 ms, as sound in a game mix is (an abrupt start is a click, and clicks are meant to be felt).
		Func<int, double> tone = f => Math.Min(1, f / 960.0) * Tone(2000, 0.8)(f);
		List<(sbyte L, sbyte R)> frames = Run(dsp, Stereo(Rate, tone, tone));

		Assert.True(Peak(frames) <= 1, $"peak {Peak(frames)}");
		Assert.True(frames.Skip(300).All(f => f == (0, 0)));
	}

	[Fact]
	public void FullBand_KeepsWhatBassDrops()
	{
		float[] tone = Stereo(Rate / 2, Tone(300, 0.3), Tone(300, 0.3));
		int bass = Peak(Run(new AudioHapticsDsp { Band = AudioHapticsBand.Bass }, tone).Skip(300));
		int full = Peak(Run(new AudioHapticsDsp { Band = AudioHapticsBand.Full }, tone).Skip(300));

		Assert.True(full > 40, $"full {full}");
		Assert.True(full > 2 * bass, $"bass {bass}, full {full}");
	}

	[Fact]
	public void Silence_GivesExactZeros_AndSoDoesTheTailAfterASound()
	{
		AudioHapticsDsp dsp = new();
		Assert.All(Run(dsp, Stereo(Rate / 2)), f => Assert.Equal((0, 0), f));

		List<(sbyte L, sbyte R)> after = Run(dsp, [.. Stereo(Rate / 4, Tone(80, 0.5), Tone(80, 0.5)), .. Stereo(Rate)]);
		Assert.True(Peak(after.Take(750)) > 20);
		// Gate hold and fade (about 0.1 s) and the release: well before a second of silence the output is exactly 0.
		Assert.All(after.Skip(750 + 1500), f => Assert.Equal((0, 0), f));
		Assert.False(dsp.GateOpen);
	}

	[Fact]
	public void VeryQuietSound_StaysBelowTheGate()
	{
		// -60 dBFS of bass (dither, a distant hum): nothing.
		AudioHapticsDsp dsp = new();
		Assert.All(Run(dsp, Stereo(Rate / 2, Tone(60, 0.001), Tone(60, 0.001))), f => Assert.Equal((0, 0), f));
	}

	[Fact]
	public void Strength_ScalesTheOutput()
	{
		float[] tone = Stereo(Rate / 2, Tone(80, 0.02), Tone(80, 0.02));
		int Strength(double k) => Peak(Run(new AudioHapticsDsp { Strength = k }, tone).Skip(600));

		int full = Strength(1), half = Strength(0.5), twice = Strength(2);
		Assert.Equal(0, Strength(0));
		Assert.True(full > 10, $"full {full}");
		Assert.InRange(half / (double)full, 0.4, 0.6);
		Assert.True(twice > full * 1.5, $"twice {twice}, full {full}");
		Assert.Equal(2, new AudioHapticsDsp { Strength = 5 }.Strength);
	}

	[Fact]
	public void Compressor_LiftsQuietScenes_AndKeepsLoudOnesFromSaturating()
	{
		float[] quiet = Stereo(Rate / 2, Tone(80, 0.01), Tone(80, 0.01)); // -40 dBFS
		float[] loud = Stereo(Rate / 2, Tone(80, 1.0), Tone(80, 1.0)); // 0 dBFS
		int q = Peak(Run(new AudioHapticsDsp(), quiet).Skip(600));
		int l = Peak(Run(new AudioHapticsDsp(), loud).Skip(600));

		// 40 dB apart going in, much closer coming out.
		Assert.True(l < q * 10, $"quiet {q}, loud {l}");
		Assert.True(q >= 6, $"quiet {q}");
		Assert.InRange(l, 50, 100);
		// Even at 200 % a full-scale input stays inside the soft limit.
		Assert.True(Peak(Run(new AudioHapticsDsp { Strength = 2 }, loud)) <= 127);
	}

	[Fact]
	public void Stereo_IsKept_LeftOnlySoundOnlyOnTheLeft()
	{
		List<(sbyte L, sbyte R)> frames = Run(new AudioHapticsDsp(), Stereo(Rate / 2, Tone(70, 0.3)));

		Assert.True(frames.Skip(300).Max(f => Math.Abs((int)f.L)) > 40);
		Assert.All(frames, f => Assert.Equal(0, f.R));
	}

	[Fact]
	public void Mono_And51_AreDownmixed()
	{
		float[] mono = new float[Rate / 2];
		for (int i = 0; i < mono.Length; i++)
		{
			mono[i] = (float)Tone(60, 0.3)(i);
		}
		List<(sbyte L, sbyte R)> m = Run(new AudioHapticsDsp(), mono, channels: 1);
		Assert.True(Peak(m.Skip(300)) > 40);
		Assert.All(m, f => Assert.Equal(f.L, f.R));

		// 5.1 with only the LFE channel: both sides.
		float[] surround = new float[Rate / 2 * 6];
		for (int i = 0; i < Rate / 2; i++)
		{
			surround[i * 6 + 3] = (float)Tone(50, 0.4)(i);
		}
		List<(sbyte L, sbyte R)> s = Run(new AudioHapticsDsp(), surround, channels: 6);
		Assert.True(s.Skip(300).Max(f => Math.Abs((int)f.L)) > 30);
		Assert.True(s.Skip(300).Max(f => Math.Abs((int)f.R)) > 30);
	}

	[Fact]
	public void OtherRates_AreResampledTo3kHz()
	{
		const int rate = 44100;
		float[] tone = new float[rate * 2];
		for (int f = 0; f < rate; f++)
		{
			tone[2 * f] = tone[2 * f + 1] = (float)Tone(60, 0.3, rate)(f);
		}
		List<(sbyte L, sbyte R)> frames = Run(new AudioHapticsDsp(), tone, rate: rate);

		Assert.InRange(frames.Count, 2999, 3001);
		Assert.True(Peak(frames.Skip(300)) > 40);
	}

	[Fact]
	public void Process_AllocatesNothing()
	{
		AudioHapticsDsp dsp = new();
		float[] tone = Stereo(480, Tone(60, 0.5), Tone(60, 0.5));
		sbyte[] l = new sbyte[64], r = new sbyte[64];
		dsp.Process(tone, 2, Rate, l, r);
		dsp.Band = AudioHapticsBand.Full;
		Assert.Equal(0, SteadyStateAllocation(() => dsp.Process(tone, 2, Rate, l, r)));
	}

	// Bytes the calling thread allocates in a round of `perRound` calls once the code is warm (tier-1 JIT reached): the
	// smallest of several rounds, so a one-off allocation of the runtime (a tier-up, a stub) in some round is not counted
	// against the code, while anything the code itself allocates shows in every round.
	internal static long SteadyStateAllocation(Action action, int warmup = 1000, int rounds = 20, int perRound = 50)
	{
		for (int i = 0; i < warmup; i++)
		{
			action();
		}
		long least = long.MaxValue;
		for (int round = 0; round < rounds; round++)
		{
			long before = GC.GetAllocatedBytesForCurrentThread();
			for (int i = 0; i < perRound; i++)
			{
				action();
			}
			least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
		}
		return least;
	}
}

public class AudioHapticsRunnerTests
{
	internal sealed class FakeCapture(AudioCaptureMode mode) : IAudioCapture
	{
		public AudioCaptureMode Mode => mode;

		public bool Disposed;

		public void Dispose() => Disposed = true;
	}

	internal sealed class FakeCaptureFactory : IAudioCaptureFactory
	{
		public List<int> Started = [];

		public FakeCapture? Last;

		public IAudioCaptureSink? Sink;

		public bool Fail;

		public AudioCaptureMode Mode = AudioCaptureMode.ProcessLoopback;

		public IAudioCapture? Start(int processId, IAudioCaptureSink sink, Action<string> log)
		{
			Started.Add(processId);
			if (Fail)
			{
				return null;
			}
			Sink = sink;
			return Last = new FakeCapture(Mode);
		}
	}

	internal sealed class FakeOutput : IAudioHapticsOutput
	{
		public AudioHapticsPush Answer = AudioHapticsPush.Played;

		public long Frames;

		public int Peak;

		public int Stops;

		public void StopAudioHaptics() => Stops++;

		public AudioHapticsPush PushAudioHaptics(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right)
		{
			Frames += left.Length;
			for (int i = 0; i < left.Length; i++)
			{
				Peak = Math.Max(Peak, Math.Max(Math.Abs((int)left[i]), Math.Abs((int)right[i])));
			}
			return Answer;
		}
	}

	private static ForegroundGame Game(string exe = "game.exe", int pid = 1234) => new(exe, null, pid);

	private static readonly AudioHapticsSettings On = new(true, 1, AudioHapticsBand.Bass);

	private static (AudioHapticsRunner Runner, FakeCaptureFactory Factory, FakeOutput Output, List<string> Log) Create()
	{
		FakeCaptureFactory factory = new();
		FakeOutput output = new();
		List<string> log = [];
		return (new AudioHapticsRunner(factory, output, log.Add), factory, output, log);
	}

	[Fact]
	public void StartsOnlyWithTheGameActiveAndTheOptionOn_StopsWhenTheGameEnds()
	{
		(AudioHapticsRunner runner, FakeCaptureFactory factory, _, List<string> log) = Create();
		List<AudioHapticsStatus> statuses = [];
		runner.StatusChanged += statuses.Add;

		runner.Update(Game(), AudioHapticsSettings.Default);
		Assert.Empty(factory.Started);
		Assert.Equal(AudioHapticsStatusKind.Off, runner.Status.Kind);

		runner.Update(null, On);
		Assert.Empty(factory.Started);
		Assert.Equal(AudioHapticsStatusKind.WaitingForGame, runner.Status.Kind);

		runner.Update(Game(), On with { Strength = 1.5, Band = AudioHapticsBand.Full });
		Assert.Equal([1234], factory.Started);
		Assert.True(runner.IsCapturing);
		Assert.Equal(new AudioHapticsStatus(AudioHapticsStatusKind.Listening, "game", AudioCaptureMode.ProcessLoopback), runner.Status);
		Assert.Contains("audio haptics: listening to game.exe (process loopback), band full, strength 150 %", log);

		// The same game on the next tick: nothing new.
		runner.Update(Game(), On);
		Assert.Single(factory.Started);

		runner.Update(null, On);
		Assert.True(factory.Last!.Disposed);
		Assert.False(runner.IsCapturing);
		Assert.Contains("audio haptics: stopped (the game ended)", log);
		Assert.Single(log, l => l.StartsWith("audio haptics: session summary:", StringComparison.Ordinal));
		Assert.Equal(AudioHapticsStatusKind.WaitingForGame, runner.Status.Kind);
		Assert.Equal([AudioHapticsStatusKind.WaitingForGame, AudioHapticsStatusKind.Listening, AudioHapticsStatusKind.WaitingForGame],
			statuses.Select(s => s.Kind));
	}

	[Fact]
	public void TurningItOff_OrAnotherGame_StopsOrMovesTheCapture()
	{
		(AudioHapticsRunner runner, FakeCaptureFactory factory, _, List<string> log) = Create();
		runner.Update(Game(), On);
		FakeCapture first = factory.Last!;

		runner.Update(Game("other.exe", 99), On);
		Assert.True(first.Disposed);
		Assert.Equal([1234, 99], factory.Started);
		Assert.Contains("audio haptics: stopped (other.exe took over)", log);

		runner.Update(Game("other.exe", 99), On with { On = false });
		Assert.True(factory.Last!.Disposed);
		Assert.Contains("audio haptics: stopped (turned off)", log);
		Assert.Equal(AudioHapticsStatus.Off, runner.Status);

		runner.Dispose();
		runner.Update(Game(), On);
		Assert.Equal(2, factory.Started.Count);
	}

	// Strength and band changed while the game runs reach the running capture at once (no restart, no new game start needed).
	[Fact]
	public void StrengthAndBand_ChangedWhileTheGameRuns_ApplyToTheRunningCapture()
	{
		(AudioHapticsRunner runner, FakeCaptureFactory factory, FakeOutput output, _) = Create();
		float[] tone = AudioHapticsDspTests.Stereo(4800, AudioHapticsDspTests.Tone(300, 0.3), AudioHapticsDspTests.Tone(300, 0.3));
		int Play()
		{
			for (int i = 0; i < 4; i++)
			{
				factory.Sink!.OnAudio(tone, 2, 48000);
			}
			output.Peak = 0;
			for (int i = 0; i < 4; i++)
			{
				factory.Sink!.OnAudio(tone, 2, 48000);
			}
			return output.Peak;
		}

		runner.Update(Game(), On);
		FakeCapture capture = factory.Last!;
		int bass = Play();

		runner.Update(Game(), On with { Band = AudioHapticsBand.Full });
		int full = Play();
		Assert.True(full > 40 && full > 2 * bass, $"bass {bass}, full {full}");

		runner.Update(Game(), On with { Band = AudioHapticsBand.Full, Strength = 0 });
		Assert.Equal(0, Play());
		runner.Update(Game(), On with { Band = AudioHapticsBand.Full, Strength = 2 });
		Assert.True(Play() > full, "twice the strength");

		Assert.Single(factory.Started);
		Assert.Same(capture, factory.Last);
		Assert.False(capture.Disposed);
		Assert.Equal(AudioHapticsStatusKind.Listening, runner.Status.Kind);
	}

	// Turned off while the game runs: the capture ends at once and the frames still waiting in the controller are dropped
	// (nothing is felt after it); turned on again, it listens again.
	[Fact]
	public void TurnedOffWhileTheGameRuns_StopsAtOnce_AndDropsWhatWasWaiting_OnAgainListens()
	{
		(AudioHapticsRunner runner, FakeCaptureFactory factory, FakeOutput output, _) = Create();
		runner.Update(Game(), On);
		FakeCapture first = factory.Last!;
		Assert.Equal(0, output.Stops);

		runner.Update(Game(), On with { On = false });
		Assert.True(first.Disposed);
		Assert.False(runner.IsCapturing);
		Assert.Equal(1, output.Stops);
		Assert.Equal(AudioHapticsStatus.Off, runner.Status);

		runner.Update(Game(), On);
		Assert.Equal([1234, 1234], factory.Started);
		Assert.True(runner.IsCapturing);
		Assert.Equal(AudioHapticsStatusKind.Listening, runner.Status.Kind);
	}

	[Fact]
	public void NoCapture_IsNotAvailable_AndNotRetriedEveryTick()
	{
		(AudioHapticsRunner runner, FakeCaptureFactory factory, _, _) = Create();
		factory.Fail = true;
		runner.Update(Game(), On);
		runner.Update(Game(), On);
		Assert.Single(factory.Started);
		Assert.Equal(AudioHapticsStatusKind.Unavailable, runner.Status.Kind);
		Assert.False(runner.Available);

		// Switched off and on again: tried once more.
		runner.Update(Game(), AudioHapticsSettings.Default);
		runner.Update(Game(), On);
		Assert.Equal(2, factory.Started.Count);
	}

	[Fact]
	public void CapturedSound_ReachesTheOutput_AndTheSummaryCountsIt()
	{
		(AudioHapticsRunner runner, FakeCaptureFactory factory, FakeOutput output, List<string> log) = Create();
		runner.Update(Game(), On);
		float[] tone = AudioHapticsDspTests.Stereo(4800, AudioHapticsDspTests.Tone(60, 0.4), AudioHapticsDspTests.Tone(60, 0.4));
		for (int i = 0; i < 10; i++)
		{
			factory.Sink!.OnAudio(tone, 2, 48000);
		}
		Assert.InRange(output.Frames, 2999, 3001);
		Assert.True(output.Peak > 40);

		runner.Update(null, On);
		string summary = Assert.Single(log, l => l.StartsWith("audio haptics: session summary:", StringComparison.Ordinal));
		Assert.Contains(output.Frames + " frames played", summary);
	}

	[Fact]
	public void NativeHaptics_PausesIt_LoggedOnce()
	{
		(AudioHapticsRunner runner, FakeCaptureFactory factory, FakeOutput output, List<string> log) = Create();
		runner.Update(Game(), On);
		output.Answer = AudioHapticsPush.NativeHaptics;
		float[] tone = AudioHapticsDspTests.Stereo(480, AudioHapticsDspTests.Tone(60, 0.4), AudioHapticsDspTests.Tone(60, 0.4));
		for (int i = 0; i < 20; i++)
		{
			factory.Sink!.OnAudio(tone, 2, 48000);
		}
		Assert.True(SpinWait.SpinUntil(() => runner.Status.Kind == AudioHapticsStatusKind.NativeHaptics, 5000));
		Assert.Single(log, l => l == "audio haptics: paused, the game sends its own haptics");

		// The game's haptics stop: listening again.
		output.Answer = AudioHapticsPush.Played;
		factory.Sink!.OnAudio(tone, 2, 48000);
		Assert.True(SpinWait.SpinUntil(() => runner.Status.Kind == AudioHapticsStatusKind.Listening, 5000));
	}

	[Fact]
	public void TheAudioPath_AllocatesNothing()
	{
		(AudioHapticsRunner runner, FakeCaptureFactory factory, _, _) = Create();
		runner.Update(Game(), On);
		float[] tone = AudioHapticsDspTests.Stereo(48000, AudioHapticsDspTests.Tone(60, 0.4), AudioHapticsDspTests.Tone(60, 0.4));
		IAudioCaptureSink sink = factory.Sink!;
		int block = 0;
		// 10 ms blocks, as a capture delivers them.
		Assert.Equal(0, AudioHapticsDspTests.SteadyStateAllocation(() =>
		{
			sink.OnAudio(tone.AsSpan(block * 960, 960), 2, 48000);
			block = (block + 1) % 50;
		}));
		// A whole second in one block (longer than the runner's step).
		Assert.Equal(0, AudioHapticsDspTests.SteadyStateAllocation(() => sink.OnAudio(tone, 2, 48000), warmup: 300, rounds: 10, perRound: 5));
	}

	// The real WASAPI capture: process loopback of this test process (silent), then the system loopback. Nothing is played
	// and no setting is changed. Skipped (returns) on machines without an audio output.
	[Fact]
	public void Wasapi_SmokeTest_StartsAndStops()
	{
		if (!OperatingSystem.IsWindows() || !HasRenderDevice())
		{
			return;
		}
		List<string> log = [];
		FakeOutput output = new();
		using (AudioHapticsRunner runner = new(new WasapiCaptureFactory(), output, log.Add))
		{
			runner.Update(new ForegroundGame("testhost.exe", null, Environment.ProcessId), On);
			Assert.True(runner.IsCapturing || runner.Status.Kind == AudioHapticsStatusKind.Unavailable, string.Join(" | ", log));
			if (WasapiCaptureFactory.ProcessLoopbackSupported)
			{
				// Windows 10 2004 and later: the game's own process tree, not the fallback.
				Assert.True(runner.Status.Mode == AudioCaptureMode.ProcessLoopback, string.Join(" | ", log));
			}
			Thread.Sleep(200);
			runner.Update(null, On);
			Assert.False(runner.IsCapturing);
		}
		if (OperatingSystem.IsWindows())
		{
			IAudioCapture? system = new WasapiCaptureFactory { UseProcessLoopback = false }.Start(0, new NullSink(), log.Add);
			Thread.Sleep(100);
			system?.Dispose();
		}
	}

	private sealed class NullSink : IAudioCaptureSink
	{
		public void OnAudio(ReadOnlySpan<float> samples, int channels, int sampleRate)
		{
		}
	}

	private static bool HasRenderDevice()
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}
		try
		{
			return new WindowsAudioDefaults().GetDefault(AudioFlow.Render, AudioRole.Console) != null;
		}
		catch (Exception)
		{
			return false;
		}
	}
}

// Mixing rules in the bridge: native haptics win, classic rumble is mixed in, the game's own audio stream carries the
// frames, silence lets the stream idle.
public partial class BridgeTests
{
	private static sbyte[] Wave(int frames, int amplitude) =>
		Enumerable.Range(0, frames).Select(i => (sbyte)Math.Round(amplitude * Math.Sin(2 * Math.PI * 100 * i / 3000.0))).ToArray();

	[Fact]
	public void AudioHaptics_GoIntoTheMixer_AndWakeTheStream()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		sbyte[] wave = Wave(30, 80);

		Assert.Equal(AudioHapticsPush.Played, bridge.PushAudioHaptics(wave, wave));
		Assert.True(session.IsHapticsStreaming || session.Haptics.Audible > 0 || session.HapticsReports > 0);
		Assert.Equal(30, bridge.AudioHapticsFrames);
	}

	[Fact]
	public void AudioHaptics_Silence_DoesNotStartTheStream()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		sbyte[] silence = new sbyte[30];

		for (int i = 0; i < 20; i++)
		{
			Assert.Equal(AudioHapticsPush.Played, bridge.PushAudioHaptics(silence, silence));
		}
		Assert.False(session.IsHapticsStreaming);
		Assert.Equal(0, session.HapticsStreams);
		Assert.True(session.Haptics.Buffered <= BluetoothSession.IdleCushionFrames);
	}

	[Fact]
	public void AudioHaptics_PauseWhileTheGameSendsItsOwnHaptics()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		sbyte[] wave = Wave(30, 80);

		SendAudio(pad, HapticsConverterTests.Audio(480, ch3: HapticsConverterTests.Tone(100, 0.3)));
		Assert.True(bridge.NativeHapticsActive);
		Assert.Equal(AudioHapticsPush.NativeHaptics, bridge.PushAudioHaptics(wave, wave));
		Assert.Equal(0, bridge.AudioHapticsFrames);
	}

	[Fact]
	public void AudioHaptics_ClassicRumbleIsMixedIn_AndTheRefillStaysOutOfTheWay()
	{
		// The bridge's audio timing runs on a clock the test moves: whether the frames come often enough for the refill to stay
		// out of the way no longer depends on how promptly a busy machine wakes the test thread.
		SimulatedClock clock = new();
		long ms = Stopwatch.Frequency / 1000;
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad) { Time = clock };
		SendOutput(pad, b => OutputBlock.SetRumble(b, 200, 0));
		Assert.True(bridge.RumbleRefills > 0);
		sbyte[] silence = new sbyte[32];
		bridge.PushAudioHaptics(silence, silence);
		// A refill the haptics thread began before that push may still be finishing: once the stream has sent two more
		// reports (or stopped), every later refill call saw the push.
		long reports = session.HapticsReports;
		Assert.True(SpinWait.SpinUntil(() => session.HapticsReports >= reports + 2 || !session.IsHapticsStreaming, 5000));
		session.Haptics.TakePeaks();
		long refills = bridge.RumbleRefills;

		// Silent audio haptics arriving every 10 ms (as a capture delivers them): the rumble rides on them, and the refill,
		// called before every report the stream sends meanwhile, adds nothing.
		for (int i = 0; i < 25; i++)
		{
			clock.Set(clock.GetTimestamp() + 10 * ms);
			bridge.PushAudioHaptics(silence, silence);
			Thread.Sleep(10);
		}
		Assert.True(session.Haptics.TakePeaks().Left > 0.2);
		Assert.Equal(0, bridge.RumbleRefills - refills);

		// They stop: after 50 ms without them the refill takes the rumble over again.
		clock.Set(clock.GetTimestamp() + 60 * ms);
		bridge.RumbleLevel = bridge.RumbleLevel;
		Assert.True(SpinWait.SpinUntil(() => bridge.RumbleRefills > refills, 5000), "the refill did not resume");
	}

	[Fact]
	public void AudioHaptics_RideOnTheGamesOwnAudioStream()
	{
		FakeLink link = new();
		// With the pause in silence on, a silent packet leaves the stream stopped, so the queue can be counted exactly.
		using BluetoothSession session = new(link) { PauseHapticsInSilence = true };
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);

		// The game streams silent audio to the pad: audio haptics frames are added into its frames, not queued again.
		SendAudio(pad, HapticsConverterTests.Audio(480));
		int before = session.Haptics.Buffered;
		sbyte[] wave = Wave(30, 80);
		Assert.Equal(AudioHapticsPush.Played, bridge.PushAudioHaptics(wave, wave));
		Assert.Equal(before, session.Haptics.Buffered);
		Assert.Equal(0, session.HapticsStreams);
		// The game's next packet carries them (only audible frames start the stream).
		SendAudio(pad, HapticsConverterTests.Audio(480), 8);
		Assert.True(SpinWait.SpinUntil(() => session.HapticsStreams > 0, 5000));
	}

	// Audio haptics switched off: the frames still waiting to ride on the game's stream are dropped, none come after it.
	[Fact]
	public void AudioHaptics_Stopped_DropsTheFramesStillWaiting()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link) { PauseHapticsInSilence = true };
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		SendAudio(pad, HapticsConverterTests.Audio(480));
		sbyte[] wave = Wave(30, 80);
		Assert.Equal(AudioHapticsPush.Played, bridge.PushAudioHaptics(wave, wave));
		Assert.Equal(30, bridge.AudioHapticsWaiting);

		bridge.StopAudioHaptics();
		Assert.Equal(0, bridge.AudioHapticsWaiting);
	}
}

public partial class BridgeTests
{
	// A DSX mod drives the triggers and the light (the output report) while audio haptics feed the haptics stream: the two
	// paths share nothing, so both stay in effect.
	[Fact]
	public void AudioHaptics_AndADsxMod_WorkAtTheSameTime()
	{
		FakeLink link = new();
		using BluetoothSession session = new(link);
		using VirtualDualSense pad = Pad();
		using ControllerBridge bridge = new(session, pad);
		bridge.SetModOutput(new Kopru.Core.Mods.ModOutput { RightTrigger = TriggerEffects.Weapon(2, 6, 8), Lightbar = (10, 20, 30) });
		sbyte[] wave = Wave(30, 80);

		Assert.Equal(AudioHapticsPush.Played, bridge.PushAudioHaptics(wave, wave));
		Assert.True(SpinWait.SpinUntil(() => session.HapticsStreams > 0, 5000));
		Assert.Equal(30, bridge.AudioHapticsFrames);
		Assert.Equal(TriggerEffects.Weapon(2, 6, 8), bridge.ControllerTriggers().Right);
		Assert.Equal(TriggerEffects.Weapon(2, 6, 8), RightOf(LastWrite(link)));
		Assert.NotNull(bridge.ModOutput);

		// The mod's next update while audio haptics keep coming: both still in effect.
		bridge.SetModOutput(new Kopru.Core.Mods.ModOutput { RightTrigger = TriggerEffects.Feedback(2, 5), Lightbar = (10, 20, 30) });
		Assert.Equal(AudioHapticsPush.Played, bridge.PushAudioHaptics(wave, wave));
		Assert.Equal(60, bridge.AudioHapticsFrames);
		Assert.Equal(TriggerEffects.Feedback(2, 5), RightOf(LastWrite(link)));
	}
}

public class AudioHapticsSettingsTests
{
	[Fact]
	public void ProfileValues_DefaultOff_AndRoundTripThroughExportImport()
	{
		ProfileValues defaults = new();
		Assert.False(defaults.AudioHaptics);
		Assert.Equal(100, defaults.AudioHapticsStrength);
		Assert.Equal(AudioHapticsBand.Bass, defaults.AudioHapticsBand);

		ProfileBook book = new();
		ControllerProfile p = book.Create("Racing", new ProfileValues { AudioHaptics = true, AudioHapticsStrength = 170, AudioHapticsBand = AudioHapticsBand.Full })!;
		string json = book.Export(p.Id);
		Assert.Contains("\"audioHapticsBand\": \"Full\"", json);
		ControllerProfile imported = new ProfileBook().Import(json, "file")!;
		Assert.True(imported.Values.AudioHaptics);
		Assert.Equal(170, imported.Values.AudioHapticsStrength);
		Assert.Equal(AudioHapticsBand.Full, imported.Values.AudioHapticsBand);
	}

	[Fact]
	public void OldProfileFiles_ReadAsOff()
	{
		const string old = """{ "format": "untethered-profile", "version": 1, "name": "Old", "values": { "rumbleLevel": 80 } }""";
		ControllerProfile imported = new ProfileBook().Import(old, "file")!;
		Assert.Equal(80, imported.Values.RumbleLevel);
		Assert.False(imported.Values.AudioHaptics);
		Assert.Equal(100, imported.Values.AudioHapticsStrength);
		Assert.Equal(AudioHapticsBand.Bass, imported.Values.AudioHapticsBand);

		// The settings file's own serializer (PascalCase, string enums) too.
		ProfileValues fromSettings = JsonSerializer.Deserialize<ProfileValues>("""{ "RumbleLevel": 80 }""")!;
		Assert.False(fromSettings.AudioHaptics);
		Assert.Equal(100, fromSettings.AudioHapticsStrength);
	}

	[Fact]
	public void ControllerSettings_CarryItIntoTheGameProfile()
	{
		Assert.Equal(AudioHapticsSettings.Default, new ControllerSettings().Game.AudioHaptics);
		ControllerSettings s = new() { AudioHapticsOn = true, AudioHapticsStrength = 250, AudioHapticsBand = AudioHapticsBand.Full };
		Assert.Equal(new AudioHapticsSettings(true, 2, AudioHapticsBand.Full), s.Game.AudioHaptics);
		Assert.Equal(new AudioHapticsSettings(true, 0.5, AudioHapticsBand.Bass), (s with { AudioHapticsStrength = 50, AudioHapticsBand = AudioHapticsBand.Bass }).Game.AudioHaptics);
	}
}
