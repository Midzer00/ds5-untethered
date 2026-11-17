// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Kopru.Core.Audio;
using Kopru.Core.Haptics;

namespace Kopru.Core.Bridge;

// Audio haptics (the game's sound turned into haptics, AudioHapticsRunner) joins the controller's haptics like the game's
// own haptics audio does:
//  - the game's native haptics win: while the game's channels 3-4 carried sound within GameHapticsQuietGap the frames are
//    dropped (NativeHaptics);
//  - classic rumble is mixed into the frames (the rumble refill stays out of the way while they arrive, as it does for
//    the game's audio), so the user's rumble level still sets how strong the rumble part is;
//  - while the game streams (silent) audio to the virtual pad itself, the frames are added into that stream's frames
//    instead of being queued a second time (both run on their own clock; queued separately they would double up);
//  - otherwise they go into the haptics mixer and wake the stream; silence goes in as exact zeros, so the stream idles.
public sealed partial class ControllerBridge : IAudioHapticsOutput
{
	// Frames of audio haptics waiting to be added into the game's own audio stream (guarded by audioGate).
	private readonly sbyte[] overlayLeft = new sbyte[HapticsMixer.MaxFrames];
	private readonly sbyte[] overlayRight = new sbyte[HapticsMixer.MaxFrames];
	private int overlayHead, overlayCount;

	private long lastAudioHaptics = long.MinValue / 2;

	// The game played its own haptics within GameHapticsQuietGap: audio haptics stays quiet.
	public bool NativeHapticsActive => Stopwatch.GetElapsedTime(Interlocked.Read(ref lastGameHapticsSound)) <= GameHapticsQuietGap;

	// Audio haptics frames played (diagnostics, tests).
	public long AudioHapticsFrames => Interlocked.Read(ref audioHapticsFrames);

	private long audioHapticsFrames;

	public AudioHapticsPush PushAudioHaptics(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right)
	{
		if (disposed)
		{
			return AudioHapticsPush.NoController;
		}
		if (left.Length != right.Length)
		{
			throw new ArgumentException("left and right differ in length", nameof(right));
		}
		if (NativeHapticsActive)
		{
			return AudioHapticsPush.NativeHaptics;
		}
		int count = left.Length;
		long now = Time.GetTimestamp();
		lock (audioGate)
		{
			Interlocked.Exchange(ref lastAudioHaptics, now);
			if (now - Interlocked.Read(ref lastAudio) <= AudioIdleTicks)
			{
				// The game's own audio stream carries them (OnAudio adds them in).
				AddOverlay(left, right);
			}
			else
			{
				overlayHead = overlayCount = 0;
				while (!left.IsEmpty)
				{
					int n = Math.Min(left.Length, frameLeft.Length);
					Span<sbyte> l = frameLeft.AsSpan(0, n);
					Span<sbyte> r = frameRight.AsSpan(0, n);
					left[..n].CopyTo(l);
					right[..n].CopyTo(r);
					MixRumble(l, r);
					session.Haptics.Push(l, r);
					left = left[n..];
					right = right[n..];
				}
			}
		}
		Interlocked.Add(ref audioHapticsFrames, count);
		session.WakeHaptics();
		return AudioHapticsPush.Played;
	}

	// Drops the frames still waiting to join the game's stream and hands the timing back to the rumble refill at once.
	public void StopAudioHaptics()
	{
		lock (audioGate)
		{
			overlayHead = overlayCount = 0;
			Interlocked.Exchange(ref lastAudioHaptics, long.MinValue / 2);
		}
	}

	// Frames waiting to join the game's own audio stream (tests).
	public int AudioHapticsWaiting
	{
		get
		{
			lock (audioGate)
			{
				return overlayCount;
			}
		}
	}

	// Audio haptics arrived within AudioIdleTicks: the rumble refill leaves the timing to it.
	private bool AudioHapticsFlowing => Time.GetTimestamp() - Interlocked.Read(ref lastAudioHaptics) <= AudioIdleTicks;

	// Caller holds audioGate. Queues frames to add into the game's stream; the oldest go when it is full.
	private void AddOverlay(ReadOnlySpan<sbyte> left, ReadOnlySpan<sbyte> right)
	{
		int size = overlayLeft.Length;
		for (int i = 0; i < left.Length; i++)
		{
			if (overlayCount == size)
			{
				overlayHead = (overlayHead + 1) % size;
				overlayCount--;
			}
			int at = (overlayHead + overlayCount) % size;
			overlayLeft[at] = left[i];
			overlayRight[at] = right[i];
			overlayCount++;
		}
	}

	// Caller holds audioGate. Adds queued audio haptics into the game's converted frames.
	private void MixOverlay(Span<sbyte> left, Span<sbyte> right)
	{
		int n = Math.Min(left.Length, overlayCount);
		for (int i = 0; i < n; i++)
		{
			left[i] = Add(left[i], overlayLeft[overlayHead]);
			right[i] = Add(right[i], overlayRight[overlayHead]);
			overlayHead = (overlayHead + 1) % overlayLeft.Length;
		}
		overlayCount -= n;
	}
}
