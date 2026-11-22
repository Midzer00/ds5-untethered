// SPDX-License-Identifier: GPL-3.0-or-later
namespace Kopru.Core.Audio;

// How the game's sound is captured: only the game's own process tree (Windows 10 2004 and later), or everything the
// default output plays (the fallback; other programs' sound is felt too).
public enum AudioCaptureMode
{
	ProcessLoopback,
	SystemLoopback,

	// One capture endpoint opened by id (the controller's USB microphone while wired): its mix format, no loopback.
	Endpoint
}

// Receives captured sound on the capture thread: interleaved float samples (-1..1) of `channels` channels at
// `sampleRate`. The span is only valid during the call; must be quick and must not allocate.
public interface IAudioCaptureSink
{
	void OnAudio(ReadOnlySpan<float> samples, int channels, int sampleRate);
}

// A running capture; Dispose stops it (and waits for its thread), after which the sink is never called again.
public interface IAudioCapture : IDisposable
{
	AudioCaptureMode Mode { get; }
}

// Opens captures (WasapiCaptureFactory; tests use a fake).
public interface IAudioCaptureFactory
{
	// Starts capturing the sound of a process and its children (falling back to the default output when that is not
	// possible); null when nothing can be captured (the reason goes to log).
	IAudioCapture? Start(int processId, IAudioCaptureSink sink, Action<string> log);
}

// Where audio haptics frames go (BridgeHost: the controller's haptics mixer through its bridge; tests use a fake).
public interface IAudioHapticsOutput
{
	AudioHapticsPush PushAudioHaptics(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right);

	// The capture stopped (switched off, game gone): frames still waiting are dropped, so nothing is felt after it.
	void StopAudioHaptics()
	{
	}
}

// What became of a block of audio haptics frames.
public enum AudioHapticsPush
{
	// Mixed into the controller's haptics.
	Played,
	// Dropped: the game sends its own (native) haptics, which win.
	NativeHaptics,
	// Dropped: no controller is connected.
	NoController
}

// What audio haptics is doing (the user-facing status line).
public enum AudioHapticsStatusKind
{
	// Off for the active game's profile.
	Off,
	// On, but no known game runs.
	WaitingForGame,
	// Capturing the game's sound.
	Listening,
	// Capturing, but paused: the game sends its own haptics.
	NativeHaptics,
	// On, but this Windows cannot capture the game's sound.
	Unavailable
}

public sealed record AudioHapticsStatus(AudioHapticsStatusKind Kind, string? Game = null, AudioCaptureMode? Mode = null)
{
	public static AudioHapticsStatus Off { get; } = new(AudioHapticsStatusKind.Off);
}

// The audio haptics part of a game profile: on, strength 0..2 (0-200 %), band, sensitivity in percent (0-100, sets the gate).
public sealed record AudioHapticsSettings(bool On = false, double Strength = 1, AudioHapticsBand Band = AudioHapticsBand.Bass,
	double Sensitivity = AudioHapticsDsp.DefaultSensitivity)
{
	public static AudioHapticsSettings Default { get; } = new();
}
